using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Axiom.Cli.Ui;

// Opens the OS folder browser so the user can click a directory and press Open/Select.
// Returns null if cancelled or if no dialog host is available.
internal static class NativeFolderPicker
{
    public static string? PickFolder(string? startPath = null)
    {
        string start = Directory.Exists(startPath) ? startPath! : Environment.CurrentDirectory;

        try
        {
            if (OperatingSystem.IsWindows())
                return PickWindows(start);
            if (OperatingSystem.IsMacOS())
                return PickMac(start);
            return PickLinux(start);
        }
        catch
        {
            return null;
        }
    }

    private static string? PickWindows(string start)
    {
        // FolderBrowserDialog via PowerShell — works on Windows without extra packages.
        string startEsc = start.Replace("'", "''");
        string script =
            "Add-Type -AssemblyName System.Windows.Forms; " +
            "$d = New-Object System.Windows.Forms.FolderBrowserDialog; " +
            "$d.Description = 'Select Axiom workspace folder'; " +
            "$d.ShowNewFolderButton = $true; " +
            $"$d.SelectedPath = '{startEsc}'; " +
            "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { " +
            "  Write-Output $d.SelectedPath " +
            "}";

        return RunCapture("powershell.exe", "-NoProfile -STA -Command " + QuotePs(script));
    }

    private static string? PickMac(string start)
    {
        string prompt = "Select Axiom workspace folder";
        // choose folder returns an alias; POSIX path converts to a normal path ending in /
        string script =
            $"try\n" +
            $"  set theFolder to choose folder with prompt \"{prompt}\" default location (POSIX file \"{EscapeApple(start)}\")\n" +
            $"  return POSIX path of theFolder\n" +
            $"on error\n" +
            $"  return \"\"\n" +
            $"end try";

        string? path = RunCaptureArgs("osascript", ["-e", script]);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = path.Trim().TrimEnd('/');
        return Directory.Exists(path) ? path : null;
    }

    private static string? PickLinux(string start)
    {
        // Prefer zenity (GNOME), then kdialog (KDE), then yad.
        string? path =
            RunCapture("zenity", $"--file-selection --directory --title=Select\\ Axiom\\ workspace\\ folder --filename={QuoteSh(start.TrimEnd('/') + "/")}")
            ?? RunCapture("kdialog", $"--getexistingdirectory {QuoteSh(start)}")
            ?? RunCapture("yad", $"--file-selection --directory --title=Select\\ Axiom\\ workspace\\ folder --filename={QuoteSh(start.TrimEnd('/') + "/")}");

        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = path.Trim();
        return Directory.Exists(path) ? path : null;
    }

    private static string? RunCapture(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p == null)
                return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                return null;
            string line = output.Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line.Split('\n')[0].Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? RunCaptureArgs(string fileName, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            if (p == null)
                return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                return null;
            string line = output.Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line.Split('\n')[0].Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string QuotePs(string s) => "\"" + s.Replace("\"", "`\"") + "\"";
    private static string QuoteSh(string s) => "'" + s.Replace("'", "'\\''") + "'";
    private static string EscapeApple(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
