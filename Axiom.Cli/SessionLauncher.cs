using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Axiom.Cli;

// Spawns an interactive chat session in a dedicated console window so the agent owns the full
// terminal surface (like Claude Code / Codex), instead of sharing a cluttered host shell.
// Ownership is marked with AXIOM_CLI_OWNED so the child never re-spawns itself.
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

        if (!TryResolveLaunchTarget(out string fileName, out List<string> prefixArgs))
            return false;

        // Preserve user args but force the chat command and strip any prior --owned flag.
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

            // macOS/Linux: stay in the current terminal (window managers differ widely).
            return false;
        }
        catch
        {
            return false;
        }
    }

    // `dotnet run` makes ProcessPath "dotnet.exe"; published installs use the axiom apphost.
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

        // Prefer the nearby apphost (axiom.exe) when present — common for Debug bin output.
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

        // Prefer Windows Terminal when present — better full-window UX — then fall back to a
        // fresh PowerShell host, then to launching the target directly via shell execute.
        if (TryStart("wt.exe", $"new-tab --title Axiom -d \"{workDir}\" \"{fileName}\" {argLine}", workDir))
            return true;

        string psFile = fileName.Replace("'", "''");
        string psArgs = argLine.Replace("'", "''");
        if (TryStart("powershell.exe",
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

    public static void PrepareOwnedConsole()
    {
        Environment.SetEnvironmentVariable(OwnedEnvironmentVariable, "1");

        try { Console.Title = "Axiom"; } catch { /* ignore */ }

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
            // Some hosts (VS debug, redirected) refuse resize — not fatal.
        }
    }
}
