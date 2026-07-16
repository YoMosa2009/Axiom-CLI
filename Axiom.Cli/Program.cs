using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Cli.Ui;
using Axiom.Core;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Council;
using Axiom.Core.Persistence;
using Axiom.Core.Tools;
using Axiom.Core.Workspace;
using Spectre.Console;

namespace Axiom.Cli;

internal static class Program
{
    private static readonly (string Id, string Label, string Description)[] ModelCatalog =
    [
        (OpenRouterChatService.Eidos1ModelId, "Eidos 1", "General-purpose reasoning"),
        (OpenRouterChatService.Hepha1ModelId, "Hepha 1", "Code-specialized")
    ];

    private static readonly Regex PathInMessageRegex = new(
        @"(?<q>[""'])(?<path>[^""']+)\k<q>|(?<path>(?:[A-Za-z]:\\|/)[^\s""']+)",
        RegexOptions.Compiled);

    private static async Task<int> Main(string[] args)
    {
        bool ownedFlag = args.Any(a => a.Equals("--owned", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("--owned", StringComparison.OrdinalIgnoreCase)).ToArray();

        string? modelOverride = ExtractModelFlag(ref args);
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "chat";
        UpdateInstaller.CleanupPendingBackups();
        ConsoleUi.ConfigureConsole();

        if (command == "chat" && !ownedFlag && !SessionLauncher.IsOwnedSession)
        {
            var launchArgs = new List<string>(args);
            if (!string.IsNullOrWhiteSpace(modelOverride))
            {
                launchArgs.Add("--model");
                launchArgs.Add(modelOverride);
            }

            if (SessionLauncher.TryLaunchOwnedChatWindow(launchArgs))
            {
                AnsiConsole.MarkupLine(
                    $"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Opened Axiom in a new window.[/]");
                return 0;
            }
        }

        if (ownedFlag || SessionLauncher.IsOwnedSession)
            SessionLauncher.PrepareOwnedConsole();

        try
        {
            return command switch
            {
                "config" => await RunConfigAsync(),
                "chat" => await RunChatAsync(modelOverride),
                "code" => await RunCodeAsync(string.Join(' ', args[1..]), modelOverride),
                "update" => await RunUpdateAsync(),
                "help" or "--help" or "-h" => ShowHelp(),
                _ => ShowUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Fatal error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static string? ExtractModelFlag(ref string[] args)
    {
        int index = Array.FindIndex(args, a => a.Equals("--model", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
            return null;

        string value = args[index + 1];
        args = args.Where((_, i) => i != index && i != index + 1).ToArray();
        return value;
    }

    private static bool TryResolveModel(string query, out (string Id, string Label, string Description) model)
    {
        model = ModelCatalog.FirstOrDefault(m =>
            m.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
            || m.Label.Equals(query, StringComparison.OrdinalIgnoreCase)
            || m.Label.Replace(" ", "", StringComparison.Ordinal).Equals(query, StringComparison.OrdinalIgnoreCase)
            || m.Label.Split(' ')[0].Equals(query, StringComparison.OrdinalIgnoreCase));
        return model != default;
    }

    private static int ShowHelp()
    {
        string gold = AxiomTheme.Hex(AxiomTheme.Gold);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        AnsiConsole.MarkupLine($"[bold]axiom[/] [{muted}]— Axiom CLI (cloud mode)[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [{gold}]axiom config[/]                  Set your OpenRouter API key");
        AnsiConsole.MarkupLine($"  [{gold}]axiom chat[/] [[--model <id>]]     Start chat in a dedicated window (Windows)");
        AnsiConsole.MarkupLine($"  [{gold}]axiom code[/] [[--model <id>]] <task>   Run the Architect/Builder/Critic council");
        AnsiConsole.MarkupLine($"  [{gold}]axiom update[/]                  Download and install the latest release");
        AnsiConsole.MarkupLine($"  [{gold}]axiom help[/]                    Show this help");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{muted}]In chat: / tools · @ folders · agent can shell/build/edit inside attached workspaces[/]");
        return 0;
    }

    private static async Task<int> RunUpdateAsync()
    {
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        AnsiConsole.MarkupLine($"[{muted}]Current version: {UpdateCheckService.GetCurrentVersion()}[/]");
        AnsiConsole.MarkupLine("Checking for updates...");
        (bool success, string message) = await UpdateInstaller.ApplyLatestAsync(CancellationToken.None);
        AnsiConsole.MarkupLine(success
            ? $"[{AxiomTheme.Hex(AxiomTheme.Success)}]{message.EscapeMarkup()}[/]"
            : $"[{AxiomTheme.Hex(AxiomTheme.Error)}]{message.EscapeMarkup()}[/]");
        return success ? 0 : 1;
    }

    private static async Task ShowUpdateNoticeIfAvailableAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        UpdateCheckResult? update;
        try
        {
            update = await UpdateCheckService.CheckForUpdateAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (update is { IsNewerVersionAvailable: true })
        {
            AnsiConsole.MarkupLine(
                $"[{AxiomTheme.Hex(AxiomTheme.Warning)}]A new version ({update.LatestVersionTag}) is available — run 'axiom update' to install it.[/]");
        }
    }

    private static int ShowUnknownCommand(string command)
    {
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Unknown command:[/] {command.EscapeMarkup()}");
        ShowHelp();
        return 1;
    }

    private static Task<int> RunConfigAsync()
    {
        using var db = new DatabaseService();
        if (!db.IsReady)
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Local database is unavailable — cannot store settings.[/]");
            return Task.FromResult(1);
        }

        string? existing = db.LoadOpenRouterApiKey();
        if (!string.IsNullOrWhiteSpace(existing))
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]An API key is already configured (ending in ...{Last4(existing)}).[/]");

        AnsiConsole.Markup($"Enter your [{AxiomTheme.Hex(AxiomTheme.Gold)}]OpenRouter API key[/] (from openrouter.ai/keys): ");
        string apiKey = ReadLineSecret() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]No key entered.[/]");
            return Task.FromResult(1);
        }

        db.SaveOpenRouterApiKey(apiKey);
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]Saved.[/] Run [{AxiomTheme.Hex(AxiomTheme.Gold)}]axiom chat[/] to start chatting.");
        return Task.FromResult(0);
    }

    private static string Last4(string value) => value.Length <= 4 ? value : value[^4..];

    private static string? ReadLineSecret()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();
        return AnsiConsole.Prompt(new TextPrompt<string>(string.Empty).Secret());
    }

    private static string? ReadLinePlain()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();
        return AnsiConsole.Prompt(new TextPrompt<string>(string.Empty).AllowEmpty());
    }

    private static async Task<int> RunChatAsync(string? modelOverride)
    {
        // Update notice before entering the alt-screen TUI (so it isn't swallowed).
        await ShowUpdateNoticeIfAvailableAsync();

        using var db = new DatabaseService();
        string? apiKey = db.IsReady ? db.LoadOpenRouterApiKey() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Warning)}]No OpenRouter API key configured.[/] Run [{AxiomTheme.Hex(AxiomTheme.Gold)}]axiom config[/] first.");
            return 1;
        }

        (string modelId, string modelLabel) = ResolveInitialModel(modelOverride);
        var chatService = new OpenRouterChatService();
        chatService.SetApiKey(apiKey);

        var recent = new RecentFoldersStore();
        var workspace = new WorkspaceSession(recent);
        var toolExecutor = new AgentToolExecutor(workspace);
        var session = new ChatSession
        {
            ChatService = chatService,
            ModelId = modelId,
            ModelLabel = modelLabel,
            Workspace = workspace,
            ToolExecutor = toolExecutor
        };

        // Full-window alternate-screen TUI — host scrollbar is not part of the UX.
        using var tui = new ChatTui();
        return await tui.RunAsync(
            session,
            ModelCatalog,
            handleSlash: (input, s) =>
            {
                TryHandleSlashCommand(input, s, tui.Notify, tui.ClearTranscript);
                return Task.CompletedTask;
            },
            augmentTools: async (input, s) => await AugmentWithToolResultsAsync(input, s.Tools),
            attachPaths: AttachPathsMentionedInMessage);
    }

    private static void AttachPathsMentionedInMessage(string input, ChatSession session)
    {
        foreach (Match match in PathInMessageRegex.Matches(input))
        {
            string path = match.Groups["path"].Value;
            if (session.Workspace.TryAttach(path))
            {
                // Feedback is surfaced by ChatTui when attach is used via @; path-in-message
                // attaches quietly so the transcript stays clean.
            }
        }
    }

    private static (string Id, string Label) ResolveInitialModel(string? modelOverride)
    {
        if (!string.IsNullOrWhiteSpace(modelOverride) && TryResolveModel(modelOverride, out var match))
            return (match.Id, match.Label);

        return (OpenRouterChatService.Eidos1ModelId, "Eidos 1");
    }

    private static async Task<int> RunCodeAsync(string task, string? modelOverride)
    {
        await ShowUpdateNoticeIfAvailableAsync();

        if (string.IsNullOrWhiteSpace(task))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Usage:[/] axiom code \"<describe what you want done>\"");
            return 1;
        }

        using var db = new DatabaseService();
        string? apiKey = db.IsReady ? db.LoadOpenRouterApiKey() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Warning)}]No OpenRouter API key configured.[/] Run [{AxiomTheme.Hex(AxiomTheme.Gold)}]axiom config[/] first.");
            return 1;
        }

        string councilModelId = OpenRouterChatService.WorkplaceCouncilDefaultModelId;
        string councilModelLabel = "Workplace Council default";
        if (!string.IsNullOrWhiteSpace(modelOverride) && TryResolveModel(modelOverride, out var match))
        {
            councilModelId = match.Id;
            councilModelLabel = match.Label;
        }

        var chatService = new OpenRouterChatService();
        chatService.SetApiKey(apiKey);
        IChatPipeline pipeline = new CloudChatPipeline(chatService, councilModelId);
        var workspaceAccess = new WorkspaceAccessService();
        var orchestrator = new CouncilOrchestrator(pipeline, councilModelId, workspaceAccess);

        string root = Environment.CurrentDirectory;
        WorkspaceIndexResult index = workspaceAccess.IndexWorkspace(root);
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        AnsiConsole.MarkupLine($"[{muted}]Connected workspace:[/] {root} [{muted}]({index.Files.Count} files)[/]");
        AnsiConsole.MarkupLine($"[{muted}]Model:[/] [{AxiomTheme.Hex(AxiomTheme.Gold)}]{councilModelLabel}[/]");

        var workspaceState = new ConnectedWorkspaceState
        {
            CodebaseEditAccessEnabled = true,
            RootPath = root
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        CouncilResult result = await orchestrator.RunAsync(
            new CouncilRequest(task, workspaceState),
            CouncilConsoleRenderer.Create(),
            CancellationToken.None);
        sw.Stop();

        if (!result.Success || result.Patch == null)
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]The council could not produce an applyable patch.[/]");
            if (!string.IsNullOrWhiteSpace(result.FinalText))
            {
                ConsoleUi.WriteAssistantHeader();
                LinkText.WriteWithLinks(result.FinalText);
                Console.WriteLine();
            }
            ConsoleUi.WriteTurnSummary(ActivityStatus.SummarizeTurn(sw.Elapsed, 0, failed: true));
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold {AxiomTheme.Hex(AxiomTheme.TextPrimary)}]Proposed changes:[/]");
        AnsiConsole.WriteLine();
        foreach (WorkspaceFilePatch filePatch in result.Patch.Files)
        {
            string target = workspaceAccess.ResolvePatchTargetPath(workspaceState, filePatch);
            string before = System.IO.File.Exists(target) ? System.IO.File.ReadAllText(target) : string.Empty;
            string after = workspaceAccess.MaterializePatchContent(workspaceState, filePatch);
            DiffRenderer.Render(filePatch.RelativePath, before, after);
            AnsiConsole.WriteLine();
        }

        ConsoleUi.WriteTurnSummary(ActivityStatus.SummarizeTurn(sw.Elapsed, 0));
        AnsiConsole.Markup($"Apply these changes? [{AxiomTheme.Hex(AxiomTheme.Success)}]y[/]/[{AxiomTheme.Hex(AxiomTheme.Error)}]n[/]: ");
        string? answer = ReadLinePlain();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Discarded — no files were changed.[/]");
            return 0;
        }

        WorkspacePatchApplyResult applyResult = workspaceAccess.ApplyPatchProposal(workspaceState, result.Patch);
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]{applyResult.Summary.EscapeMarkup()}[/]");
        return 0;
    }

    private static bool TryHandleSlashCommand(
        string input,
        ChatSession session,
        Action<string>? notify = null,
        Action? clearTranscript = null)
    {
        if (!input.StartsWith('/'))
            return false;

        void Say(string msg)
        {
            if (notify != null)
                notify(msg);
            else
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]{msg.EscapeMarkup()}[/]");
        }

        string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/tools":
                if (parts.Length >= 3 && bool.TryParse(
                        parts[2].Equals("on", StringComparison.OrdinalIgnoreCase) ? "true" :
                        parts[2].Equals("off", StringComparison.OrdinalIgnoreCase) ? "false" : null,
                        out bool enabled))
                {
                    if (session.Tools.TrySet(parts[1], enabled))
                        Say($"{parts[1]} set to {(enabled ? "on" : "off")}.");
                    else
                        Say($"Unknown tool: {parts[1]}");
                }
                foreach ((string name, bool on) in session.Tools.AsList())
                    Say($"  {(on ? "●" : "○")} {name}");
                Say("Tip: type / then ↑↓ + Enter to toggle tools.");
                return true;

            case "/model":
                if (parts.Length >= 2)
                {
                    if (TryResolveModel(parts[1], out var match))
                    {
                        session.ModelId = match.Id;
                        session.ModelLabel = match.Label;
                        Say($"Switched to {match.Label}.");
                    }
                    else
                    {
                        Say($"Unknown model: {parts[1]}");
                    }
                }
                foreach (var m in ModelCatalog)
                    Say($"  {(m.Label == session.ModelLabel ? "●" : "○")} {m.Label} — {m.Description}");
                return true;

            case "/workspace":
            case "/ws":
                Say("Attached workspaces:");
                foreach (string root in session.Workspace.Roots)
                    Say($"  • {root}");
                Say("Tip: type @ to pick a recent folder, or paste a path in your message.");
                return true;

            case "/clear":
                session.History.Clear();
                clearTranscript?.Invoke();
                Say("Conversation cleared.");
                return true;

            case "/help":
                Say("/ tools  ·  @ folders  ·  /workspace  ·  /model  ·  /clear  ·  PgUp/PgDn scroll  ·  exit");
                return true;

            default:
                Say($"Unknown command: {parts[0]}");
                return true;
        }
    }

    private static async Task<string> AugmentWithToolResultsAsync(string input, SessionToolSettings tools)
    {
        if (tools.CalculatorEnabled && CalculatorToolAgent.TryBuildContext(input, out string calcContext, out _))
            return input + calcContext;

        if (!LocalToolIntentRouter.TryRouteIntent(input, tools.WebSearchEnabled, codebaseToolsEnabled: false, out LocalToolIntentRouter.ToolIntent? intent)
            || intent == null)
            return input;

        if (tools.WebSearchEnabled && intent.Tool == LocalToolIntentRouter.ToolWebSearch)
        {
            var webSearch = new WebSearchService();
            string results = await webSearch.SearchTopSnippetsForNormalChatAsync(intent.Query, CancellationToken.None);
            return string.IsNullOrWhiteSpace(results)
                ? input
                : $"{input}\n\n[[WEB SEARCH RESULTS]]\n{results}\n[[END WEB SEARCH RESULTS]]";
        }

        if (tools.SandboxEnabled && intent.Tool == LocalToolIntentRouter.ToolPythonMath)
        {
            var python = new PythonExecutionService();
            PythonExecutionResult result = await python.ExecuteMathScriptAsync(intent.Query);
            return $"{input}\n\n[[PYTHON SANDBOX RESULT]]\n{result.Output}\n[[END PYTHON SANDBOX RESULT]]";
        }

        return input;
    }
}
