using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Cli.Ui;
using Axiom.Core;
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

    private static async Task<int> Main(string[] args)
    {
        string? modelOverride = ExtractModelFlag(ref args);
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "chat";
        UpdateInstaller.CleanupPendingBackups();

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

    // Pulls "--model <id>" out of the args wherever it appears (e.g. "axiom code --model hepha
    // <task>" or "axiom chat --model eidos"), leaving the remaining args for normal parsing.
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
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine($"  [{gold}]axiom config[/]                  Set your OpenRouter API key");
        AnsiConsole.MarkupLine($"  [{gold}]axiom chat[/] [[--model <id>]]     Start an interactive cloud chat session (default)");
        AnsiConsole.MarkupLine($"  [{gold}]axiom code[/] [[--model <id>]] <task>   Run the Architect/Builder/Critic council against the current directory");
        AnsiConsole.MarkupLine($"  [{gold}]axiom update[/]                  Download and install the latest release");
        AnsiConsole.MarkupLine($"  [{gold}]axiom help[/]                    Show this help");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine($"[{muted}]Models: eidos (Eidos 1, general-purpose) · hepha (Hepha 1, code-specialized)[/]");
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

    // Fire-and-forget, bounded to a couple seconds so a slow/unreachable GitHub never delays
    // startup — the check result (if it arrives in time) is only shown after the session ends,
    // mirroring the WPF app's non-blocking startup update notice.
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
        string apiKey = ReadLine(secret: true) ?? string.Empty;

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

    // Spectre's interactive prompts refuse redirected/piped stdin outright ("non-interactive
    // mode"), which breaks scripting and CI use. Fall back to a plain Console.ReadLine() in that
    // case; only use Spectre's masked-input prompt on a real interactive terminal.
    private static string? ReadLine(bool secret)
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        return secret
            ? AnsiConsole.Prompt(new TextPrompt<string>(string.Empty).Secret())
            : AnsiConsole.Prompt(new TextPrompt<string>(string.Empty).AllowEmpty());
    }

    private sealed class ChatSession
    {
        public required OpenRouterChatService ChatService { get; init; }
        public required string ModelId { get; set; }
        public required string ModelLabel { get; set; }
        public IChatPipeline Pipeline { get; private set; } = null!;
        public SessionToolSettings Tools { get; } = new();
        public List<OpenRouterMessage> History { get; } = new();

        public void RebuildPipeline() => Pipeline = new CloudChatPipeline(ChatService, ModelId);
    }

    private static async Task<int> RunChatAsync(string? modelOverride)
    {
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
        var session = new ChatSession { ChatService = chatService, ModelId = modelId, ModelLabel = modelLabel };
        session.RebuildPipeline();

        ConsoleUi.ShowWelcome(session.ModelLabel, session.Tools);

        int turnCount = 0;
        while (true)
        {
            ConsoleUi.PromptDivider();
            AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.Gold)}]❯[/] ");
            string? input = ReadLine(secret: false);
            if (input == null)
                break; // stdin closed (EOF) — exit cleanly instead of looping forever
            if (string.IsNullOrWhiteSpace(input))
                continue;
            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;

            if (TryHandleSlashCommand(input, session))
                continue;

            string userMessage = await AugmentWithToolResultsAsync(input, session.Tools);

            var request = new ChatPipelineRequest(FoundationSystemPrompt.Text, userMessage, session.History);

            var thinking = new ConsoleUi.ThinkingIndicator();
            bool firstToken = true;
            try
            {
                ChatPipelineResult result = await session.Pipeline.ExecuteAsync(
                    request,
                    onToken: token =>
                    {
                        if (firstToken)
                        {
                            thinking.Stop();
                            AnsiConsole.Markup($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]axiom›[/] ");
                            firstToken = false;
                        }
                        Console.Write(token);
                    },
                    CancellationToken.None);

                thinking.Stop();
                Console.WriteLine();
                session.History.Add(new OpenRouterMessage("user", input));
                session.History.Add(new OpenRouterMessage("assistant", result.ResponseText));
                turnCount++;
                ConsoleUi.StatusFooter(session.ModelLabel, session.Tools, turnCount);
            }
            catch (Exception ex)
            {
                thinking.Stop();
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Error:[/] {ex.Message.EscapeMarkup()}");
            }
        }

        return 0;
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

        // The council stays on one model for the whole task rather than falling back mid-run — a
        // silent model swap leaves the replacement unable to continue what the first model
        // started (see the WPF app's changelog for the incident this fixed). --model overrides
        // the council's usual default; otherwise it keeps using the GUI app's council default.
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

        CouncilResult result = await orchestrator.RunAsync(
            new CouncilRequest(task, workspaceState),
            CouncilConsoleRenderer.Create(),
            CancellationToken.None);

        if (!result.Success || result.Patch == null)
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]The council could not produce an applyable patch.[/]");
            if (!string.IsNullOrWhiteSpace(result.FinalText))
                AnsiConsole.MarkupLine(result.FinalText.EscapeMarkup());
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

        AnsiConsole.Markup($"Apply these changes? [{AxiomTheme.Hex(AxiomTheme.Success)}]y[/]/[{AxiomTheme.Hex(AxiomTheme.Error)}]n[/]: ");
        string? answer = ReadLine(secret: false);
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Discarded — no files were changed.[/]");
            return 0;
        }

        WorkspacePatchApplyResult applyResult = workspaceAccess.ApplyPatchProposal(workspaceState, result.Patch);
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]{applyResult.Summary.EscapeMarkup()}[/]");
        return 0;
    }

    private static bool TryHandleSlashCommand(string input, ChatSession session)
    {
        if (!input.StartsWith('/'))
            return false;

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
                        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]{parts[1]}[/] set to {(enabled ? "on" : "off")}.");
                    else
                        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Unknown tool:[/] {parts[1].EscapeMarkup()}");
                }
                ConsoleUi.ShowToolsPanel(session.Tools);
                return true;

            case "/model":
                if (parts.Length >= 2)
                {
                    if (TryResolveModel(parts[1], out var match))
                    {
                        session.ModelId = match.Id;
                        session.ModelLabel = match.Label;
                        session.RebuildPipeline();
                        AnsiConsole.MarkupLine($"Switched to [{AxiomTheme.Hex(AxiomTheme.Gold)}]{match.Label}[/].");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Unknown model:[/] {parts[1].EscapeMarkup()}");
                    }
                }
                ConsoleUi.ShowModelPanel(session.ModelLabel, ModelCatalog);
                return true;

            case "/clear":
                session.History.Clear();
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Conversation cleared.[/]");
                return true;

            case "/help":
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Gold)}]/tools[/], [{AxiomTheme.Hex(AxiomTheme.Gold)}]/model[/], [{AxiomTheme.Hex(AxiomTheme.Gold)}]/clear[/], [{AxiomTheme.Hex(AxiomTheme.Gold)}]exit[/]");
                return true;

            default:
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Unknown command:[/] {parts[0].EscapeMarkup()}");
                return true;
        }
    }

    // Mirrors the WPF app's deterministic tool-intent routing (LocalToolIntentRouter): a
    // calculator/web-search/sandbox request detected in the user's own text is executed locally
    // and the result is handed to the model as grounded context, rather than trusting the model
    // to compute or recall it. Each tool only runs when the user has enabled it via /tools.
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
