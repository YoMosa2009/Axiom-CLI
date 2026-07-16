using System;

namespace Axiom.Cli.Ui;

// Human-readable phase labels for what the agent is doing — shown live and as a turn summary.
internal static class ActivityStatus
{
    public const string Ready = "Ready";
    public const string Thinking = "Thinking";
    public const string Planning = "Planning";
    public const string Working = "Working";
    public const string Searching = "Searching";
    public const string Reading = "Reading files";
    public const string Writing = "Writing files";
    public const string Running = "Running command";
    public const string Building = "Building";
    public const string Downloading = "Downloading";
    public const string Listing = "Listing files";
    public const string Reviewing = "Reviewing";
    public const string Generating = "Generating response";
    public const string Streaming = "Streaming";
    public const string Waiting = "Waiting on model";
    public const string ToolFinished = "Tool finished";
    public const string TaskCompleted = "Task completed";
    public const string Finished = "Finished";
    public const string WorkedOn = "Worked on";
    public const string Stopped = "Stopped";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Idle = "Idle";
    public const string Connecting = "Connecting";
    public const string Retrying = "Retrying";
    public const string Compiling = "Compiling";
    public const string Testing = "Testing";
    public const string Installing = "Installing";

    public static string FromToolName(string toolName, bool completed = false)
    {
        string phase = toolName?.ToLowerInvariant() switch
        {
            "run_shell" => Running,
            "read_file" => Reading,
            "write_file" => Writing,
            "list_dir" => Listing,
            "download_file" => Downloading,
            "search_files" => Searching,
            _ => Working
        };

        return completed ? $"{ToolFinished}: {ShortTool(toolName)}" : $"{phase} · {ShortTool(toolName)}";
    }

    public static string FromShellHint(string command)
    {
        string c = (command ?? string.Empty).ToLowerInvariant();
        if (c.Contains("dotnet build") || c.Contains("msbuild") || c.Contains("cmake") || c.Contains("make ") || c.Contains("cargo build") || c.Contains("npm run build") || c.Contains("gradle"))
            return Building;
        if (c.Contains("dotnet test") || c.Contains("pytest") || c.Contains("npm test") || c.Contains("cargo test"))
            return Testing;
        if (c.Contains("npm install") || c.Contains("pip install") || c.Contains("dotnet restore") || c.Contains("cargo add"))
            return Installing;
        if (c.Contains("javac") || c.Contains("csc ") || c.Contains("cl "))
            return Compiling;
        return Running;
    }

    public static string SummarizeTurn(TimeSpan elapsed, int toolCallCount, bool failed = false, bool cancelled = false)
    {
        if (cancelled)
            return $"{Cancelled} · {ConsoleUi.FormatDuration(elapsed)}";
        if (failed)
            return $"{Failed} · {ConsoleUi.FormatDuration(elapsed)}";

        string duration = ConsoleUi.FormatDuration(elapsed);
        if (toolCallCount <= 0)
            return $"{Finished} · Worked for {duration}";

        return $"{TaskCompleted} · {WorkedOn} {toolCallCount} step{(toolCallCount == 1 ? "" : "s")} · {duration}";
    }

    private static string ShortTool(string? name) => string.IsNullOrWhiteSpace(name)
        ? "tool"
        : name.Replace('_', ' ');
}
