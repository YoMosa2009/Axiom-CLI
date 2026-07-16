using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Axiom.Cli;

// Optionally opens chat in a dedicated terminal window so the TUI owns the surface.
// Ownership is marked with AXIOM_CLI_OWNED so the child never re-spawns itself.
//
// Windows: prefer Windows Terminal / PowerShell in a new window.
// macOS / Linux: prefer a new terminal tab/window when a known host is available; otherwise
// fall through and run the full-window TUI in the current terminal (same experience).
internal static class SessionLauncher
{
    public const string OwnedEnvironmentVariable = "AXIOM_CLI_OWNED";

    public static bool IsOwnedSession =>
        string.Equals(Environment.GetEnvironmentVariable(OwnedEnvironmentVariable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Returns true when the caller should exit (child was launched), false when this process
    /// should continue running chat itself (already owned, non-interactive, or launch failed).
    /// </summary>
    public static bool TryLaunchOwnedChatWindow(IReadOnlyList<string> originalArgs)
    {
        if (IsOwnedSession)
            return false;

        // Piped/scripted use must stay in-process.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        // Explicit opt-out: run in the current terminal on every OS.
        if (string.Equals(Environment.GetEnvironmentVariable("AXIOM_CLI_NO_NEW_WINDOW"), "1", StringComparison.Ordinal))
            return false;

        if (!TryResolveLaunchTarget(out string fileName, out List<string> prefixArgs))
            return false;

        var childArgs = new List<string>(prefixArgs);
        bool sawChat = false;
        for (int i = 0; i < originalArgs.Count; i++)
        {
            string arg = originalArgs[i];
            if (arg.Equals("--owned", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i == 0 && arg.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                sawChat = true;
                childArgs.Add("chat");
                continue;
            }
            childArgs.Add(arg);
        }

        if (!sawChat)
            childArgs.Insert(prefixArgs.Count, "chat");

        childArgs.Add("--owned");

        try
        {
            if (OperatingSystem.IsWindows())
                return LaunchWindows(fileName, childArgs);
            if (OperatingSystem.IsMacOS())
                return LaunchMac(fileName, childArgs);
            if (OperatingSystem.IsLinux())
                return LaunchLinux(fileName, childArgs);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveLaunchTarget(out string fileName, out List<string> prefixArgs)
    {
        fileName = string.Empty;
        prefixArgs = new List<string>();

        string? processPath = Environment.ProcessPath;
        string? entryDll = Assembly.GetEntryAssembly()?.Location;

        bool processIsDotnet = !string.IsNullOrWhiteSpace(processPath)
            && Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        if (processIsDotnet && !string.IsNullOrWhiteSpace(entryDll) && File.Exists(entryDll))
        {
            fileName = processPath!;
            prefixArgs.Add("exec");
            prefixArgs.Add(entryDll);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entryDll))
        {
            string? dir = Path.GetDirectoryName(entryDll);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "axiom.exe" : "axiom");
                if (File.Exists(apphost))
                {
                    fileName = apphost;
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath) && !processIsDotnet)
        {
            fileName = processPath;
            return true;
        }

        return false;
    }

    private static bool LaunchWindows(string fileName, List<string> childArgs)
    {
        string argLine = string.Join(' ', childArgs.Select(QuoteArg));
        string workDir = Environment.CurrentDirectory;

        if (TryStart("wt.exe", $"new-tab --title Axiom -d \"{workDir}\" \"{fileName}\" {argLine}", workDir))
            return true;

        string psFile = fileName.Replace("'", "''");
        string psArgs = argLine.Replace("'", "''");
        if (TryStart("powershell.exe",
                $"-NoLogo -NoExit -Command \"$Host.UI.RawUI.WindowTitle='Axiom'; Set-Location -LiteralPath '{workDir.Replace("'", "''")}'; & '{psFile}' {psArgs}\"",
                workDir))
            return true;

        if (TryStart("pwsh",
                $"-NoLogo -NoExit -Command \"$Host.UI.RawUI.WindowTitle='Axiom'; Set-Location -LiteralPath '{workDir.Replace("'", "''")}'; & '{psFile}' {psArgs}\"",
                workDir))
            return true;

        var direct = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argLine,
            WorkingDirectory = workDir,
            UseShellExecute = true,
        };
        Process.Start(direct);
        return true;
    }

    private static bool LaunchMac(string fileName, List<string> childArgs)
    {
        string workDir = Environment.CurrentDirectory;
        // Escape for embedding inside an AppleScript double-quoted string.
        static string Esc(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        string argLine = string.Join(' ', childArgs.Select(a => Esc(a).Contains(' ') ? $"\\\"{Esc(a)}\\\"" : Esc(a)));
        string inner = $"cd \\\"{Esc(workDir)}\\\" ; \\\"{Esc(fileName)}\\\" {argLine}";
        string script = $"tell application \"Terminal\" to do script \"{inner}\"";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using Process? p = Process.Start(psi);
            if (p == null)
                return false;
            // Don't block the parent on Terminal forever — a quick wait confirms launch.
            if (!p.WaitForExit(3000))
                return true;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool LaunchLinux(string fileName, List<string> childArgs)
    {
        string workDir = Environment.CurrentDirectory;
        string argLine = string.Join(' ', childArgs.Select(QuoteArgShell));
        string cmd = $"cd {QuoteArgShell(workDir)} && {QuoteArgShell(fileName)} {argLine}; exec bash";

        // Common terminal emulators (first match wins).
        (string exe, string args)[] hosts =
        [
            ("gnome-terminal", $"--working-directory={workDir} -- {fileName} {string.Join(' ', childArgs.Select(QuoteArg))}"),
            ("kgx", $"--working-directory={workDir} -- {fileName} {string.Join(' ', childArgs.Select(QuoteArg))}"),
            ("konsole", $"-e {fileName} {string.Join(' ', childArgs.Select(QuoteArg))}"),
            ("xfce4-terminal", $"--working-directory={workDir} -e \"{fileName} {argLine}\""),
            ("xterm", $"-e bash -lc {QuoteArg(cmd)}"),
            ("x-terminal-emulator", $"-e bash -lc {QuoteArg(cmd)}"),
        ];

        foreach ((string exe, string args) in hosts)
        {
            if (TryStart(exe, args, workDir))
                return true;
        }

        // No external terminal found — run TUI in this shell.
        return false;
    }

    private static bool TryStart(string fileName, string arguments, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = true,
            };
            using Process? p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";
        if (arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\''))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }

    private static string QuoteArgShell(string arg)
    {
        // Single-quote for POSIX shells, escape embedded single quotes.
        return "'" + (arg ?? string.Empty).Replace("'", "'\\''") + "'";
    }

    public static void PrepareOwnedConsole()
    {
        Environment.SetEnvironmentVariable(OwnedEnvironmentVariable, "1");

        try { Console.Title = "Axiom"; } catch { /* ignore */ }

        // Buffer resize is mainly a Windows console concept; Unix terminals size via the host.
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            int targetW = Math.Clamp(Console.LargestWindowWidth, 80, 160);
            int targetH = Math.Clamp(Console.LargestWindowHeight, 30, 50);
            if (Console.BufferWidth < targetW)
                Console.SetBufferSize(targetW, Math.Max(Console.BufferHeight, targetH * 4));
            if (Console.WindowWidth < targetW || Console.WindowHeight < targetH)
                Console.SetWindowSize(
                    Math.Min(targetW, Console.LargestWindowWidth),
                    Math.Min(targetH, Console.LargestWindowHeight));
        }
        catch
        {
            // Some hosts refuse resize — not fatal.
        }
    }
}
