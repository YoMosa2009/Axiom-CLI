using System;
using System.Collections.Generic;
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
    private static async Task<int> Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "chat";
        UpdateInstaller.CleanupPendingBackups();

        try
        {
            return command switch
            {
                "config" => await RunConfigAsync(),
                "chat" => await RunChatAsync(),
                "code" => await RunCodeAsync(string.Join(' ', args[1..])),
                "update" => await RunUpdateAsync(),
                "help" or "--help" or "-h" => ShowHelp(),
                _ => ShowUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Fatal error:[/] {ex.Message}");
            return 1;
        }
    }

    private static int ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]axiom[/] — Axiom CLI (cloud mode)");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("  [green]axiom config[/]        Set your OpenRouter API key");
        AnsiConsole.MarkupLine("  [green]axiom chat[/]          Start an interactive cloud chat session (default)");
        AnsiConsole.MarkupLine("  [green]axiom code <task>[/]   Run the Architect/Builder/Critic council against the current directory");
        AnsiConsole.MarkupLine("  [green]axiom update[/]        Download and install the latest release");
        AnsiConsole.MarkupLine("  [green]axiom help[/]          Show this help");
        return 0;
    }

    private static async Task<int> RunUpdateAsync()
    {
        AnsiConsole.MarkupLineInterpolated($"[grey]Current version: {UpdateCheckService.GetCurrentVersion()}[/]");
        AnsiConsole.MarkupLine("Checking for updates...");
        (bool success, string message) = await UpdateInstaller.ApplyLatestAsync(CancellationToken.None);
        AnsiConsole.MarkupLine(success ? $"[green]{message.EscapeMarkup()}[/]" : $"[red]{message.EscapeMarkup()}[/]");
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
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]A new version ({update.LatestVersionTag}) is available — run 'axiom update' to install it.[/]");
        }
    }

    private static int ShowUnknownCommand(string command)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Unknown command:[/] {command}");
        ShowHelp();
        return 1;
    }

    private static Task<int> RunConfigAsync()
    {
        using var db = new DatabaseService();
        if (!db.IsReady)
        {
            AnsiConsole.MarkupLine("[red]Local database is unavailable — cannot store settings.[/]");
            return Task.FromResult(1);
        }

        string? existing = db.LoadOpenRouterApiKey();
        if (!string.IsNullOrWhiteSpace(existing))
            AnsiConsole.MarkupLine($"[grey]An API key is already configured (ending in ...{Last4(existing)}).[/]");

        AnsiConsole.Markup("Enter your [green]OpenRouter API key[/] (from openrouter.ai/keys): ");
        string apiKey = ReadLine(secret: true) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[red]No key entered.[/]");
            return Task.FromResult(1);
        }

        db.SaveOpenRouterApiKey(apiKey);
        AnsiConsole.MarkupLine("[green]Saved.[/] Run [green]axiom chat[/] to start chatting.");
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

    private static async Task<int> RunChatAsync()
    {
        await ShowUpdateNoticeIfAvailableAsync();

        using var db = new DatabaseService();
        string? apiKey = db.IsReady ? db.LoadOpenRouterApiKey() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[yellow]No OpenRouter API key configured.[/] Run [green]axiom config[/] first.");
            return 1;
        }

        var chatService = new OpenRouterChatService();
        chatService.SetApiKey(apiKey);
        IChatPipeline pipeline = new CloudChatPipeline(chatService, OpenRouterChatService.Eidos1ModelId);
        string modelLabel = OpenRouterChatService.Eidos1ModelId;
        var tools = new SessionToolSettings();

        ConsoleUi.ShowWelcome(modelLabel, tools);

        var history = new List<OpenRouterMessage>();
        int turnCount = 0;
        while (true)
        {
            ConsoleUi.PromptDivider();
            AnsiConsole.Markup("[cyan1]❯[/] ");
            string? input = ReadLine(secret: false);
            if (input == null)
                break; // stdin closed (EOF) — exit cleanly instead of looping forever
            if (string.IsNullOrWhiteSpace(input))
                continue;
            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;

            if (TryHandleSlashCommand(input, tools, history))
                continue;

            string userMessage = await AugmentWithToolResultsAsync(input, tools);

            var request = new ChatPipelineRequest(
                FoundationSystemPrompt.Text,
                userMessage,
                history);

            var thinking = new ConsoleUi.ThinkingIndicator();
            bool firstToken = true;
            try
            {
                ChatPipelineResult result = await pipeline.ExecuteAsync(
                    request,
                    onToken: token =>
                    {
                        if (firstToken)
                        {
                            thinking.Stop();
                            AnsiConsole.Markup("[grey]axiom›[/] ");
                            firstToken = false;
                        }
                        Console.Write(token);
                    },
                    CancellationToken.None);

                thinking.Stop();
                Console.WriteLine();
                history.Add(new OpenRouterMessage("user", input));
                history.Add(new OpenRouterMessage("assistant", result.ResponseText));
                turnCount++;
                ConsoleUi.StatusFooter(modelLabel, tools, turnCount);
            }
            catch (Exception ex)
            {
                thinking.Stop();
                Console.WriteLine();
                AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
            }
        }

        return 0;
    }

    private static async Task<int> RunCodeAsync(string task)
    {
        await ShowUpdateNoticeIfAvailableAsync();

        if (string.IsNullOrWhiteSpace(task))
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] axiom code \"<describe what you want done>\"");
            return 1;
        }

        using var db = new DatabaseService();
        string? apiKey = db.IsReady ? db.LoadOpenRouterApiKey() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[yellow]No OpenRouter API key configured.[/] Run [green]axiom config[/] first.");
            return 1;
        }

        var chatService = new OpenRouterChatService();
        chatService.SetApiKey(apiKey);
        // The council stays on one model for the whole task rather than falling back mid-run —
        // a silent model swap leaves the replacement unable to continue what the first model
        // started (see the WPF app's changelog for the incident this fixed).
        IChatPipeline pipeline = new CloudChatPipeline(chatService, OpenRouterChatService.WorkplaceCouncilDefaultModelId);
        var workspaceAccess = new WorkspaceAccessService();
        var orchestrator = new CouncilOrchestrator(pipeline, OpenRouterChatService.WorkplaceCouncilDefaultModelId, workspaceAccess);

        string root = Environment.CurrentDirectory;
        WorkspaceIndexResult index = workspaceAccess.IndexWorkspace(root);
        AnsiConsole.MarkupLineInterpolated($"[grey]Connected workspace: {root} ({index.Files.Count} files)[/]");

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
            AnsiConsole.MarkupLine("[red]The council could not produce an applyable patch.[/]");
            if (!string.IsNullOrWhiteSpace(result.FinalText))
                AnsiConsole.MarkupLine(result.FinalText.EscapeMarkup());
            return 1;
        }

        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Proposed changes:[/]");
        foreach (WorkspaceFilePatch filePatch in result.Patch.Files)
        {
            string target = workspaceAccess.ResolvePatchTargetPath(workspaceState, filePatch);
            string before = System.IO.File.Exists(target) ? System.IO.File.ReadAllText(target) : string.Empty;
            string after = workspaceAccess.MaterializePatchContent(workspaceState, filePatch);
            DiffRenderer.Render(filePatch.RelativePath, before, after);
            AnsiConsole.WriteLine();
        }

        AnsiConsole.Markup("Apply these changes? [green]y[/]/[red]n[/]: ");
        string? answer = ReadLine(secret: false);
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[grey]Discarded — no files were changed.[/]");
            return 0;
        }

        WorkspacePatchApplyResult applyResult = workspaceAccess.ApplyPatchProposal(workspaceState, result.Patch);
        AnsiConsole.MarkupLineInterpolated($"[green]{applyResult.Summary}[/]");
        return 0;
    }

    private static bool TryHandleSlashCommand(string input, SessionToolSettings tools, List<OpenRouterMessage> history)
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
                    if (tools.TrySet(parts[1], enabled))
                        AnsiConsole.MarkupLineInterpolated($"[green]{parts[1]}[/] set to {(enabled ? "on" : "off")}.");
                    else
                        AnsiConsole.MarkupLineInterpolated($"[red]Unknown tool:[/] {parts[1]}");
                }
                ConsoleUi.ShowToolsPanel(tools);
                return true;

            case "/clear":
                history.Clear();
                AnsiConsole.MarkupLine("[grey]Conversation cleared.[/]");
                return true;

            case "/help":
                AnsiConsole.MarkupLine("[grey]/tools[/], [grey]/clear[/], [grey]exit[/]");
                return true;

            default:
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown command:[/] {parts[0]}");
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
