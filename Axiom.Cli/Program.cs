using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        (OpenRouterChatService.Hepha1ModelId, "Hepha 1", "Code-specialized"),
        (OpenRouterChatService.CustomEndpointModelId, "Kestral 1", "Your self-hosted endpoint")
    ];

    private static readonly Regex PathInMessageRegex = new(
        @"(?<q>[""'])(?<path>[^""']+)\k<q>|(?<path>(?:[A-Za-z]:\\|/)[^\s""']+)",
        RegexOptions.Compiled);

    private static async Task<int> Main(string[] args)
    {
        bool ownedFlag = args.Any(a => a.Equals("--owned", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("--owned", StringComparison.OrdinalIgnoreCase)).ToArray();

        string? modelOverride = ExtractFlag(ref args, "--model");
        string? profileOverride = ExtractFlag(ref args, "--profile") ?? ExtractFlag(ref args, "-p");
        bool yesFlag = ExtractSwitch(ref args, "--yes") || ExtractSwitch(ref args, "-y");
        bool jsonFlag = ExtractSwitch(ref args, "--json");

        // Deep link: `axiom .` or `axiom C:\repo` locks that folder and opens chat.
        string? bootstrapPath = null;
        if (args.Length > 0 && !IsReservedCommand(args[0]) && LooksLikePathArg(args[0]))
        {
            bootstrapPath = args[0];
            args = args.Skip(1).ToArray();
        }

        string? modelOverride2 = ExtractFlag(ref args, "--model");
        if (!string.IsNullOrWhiteSpace(modelOverride2))
            modelOverride = modelOverride2;

        // Bare `axiom` (no subcommand) opens the chat TUI. `axiom chat` remains an alias.
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        bool isChatEntry = command is "" or "chat";
        UpdateInstaller.CleanupPendingBackups();
        ConsoleUi.ConfigureConsole();

        if (isChatEntry && !ownedFlag && !SessionLauncher.IsOwnedSession)
        {
            var launchArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(profileOverride))
            {
                launchArgs.Add("--profile");
                launchArgs.Add(profileOverride);
            }
            if (!string.IsNullOrWhiteSpace(bootstrapPath))
                launchArgs.Add(bootstrapPath);
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
                "" or "chat" => await RunChatAsync(modelOverride, profileOverride, bootstrapPath),
                "code" => await RunCodeAsync(string.Join(' ', args.Skip(1)), modelOverride, yesFlag, jsonFlag, profileOverride),
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

    private static bool IsReservedCommand(string arg)
    {
        string c = arg.ToLowerInvariant();
        return c is "config" or "chat" or "code" or "update" or "help" or "--help" or "-h";
    }

    private static bool LooksLikePathArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return false;
        if (arg is "." or ".." || arg.StartsWith("./") || arg.StartsWith(".\\") || arg.StartsWith("~/"))
            return true;
        if (arg.Length >= 2 && char.IsLetter(arg[0]) && arg[1] == ':')
            return true;
        if (arg.StartsWith('/') || arg.StartsWith('\\'))
            return true;
        try
        {
            string full = Path.GetFullPath(arg.Trim().Trim('"'));
            return Directory.Exists(full) || File.Exists(full);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractFlag(ref string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
            return null;

        string value = args[index + 1];
        args = args.Where((_, i) => i != index && i != index + 1).ToArray();
        return value;
    }

    private static bool ExtractSwitch(ref string[] args, string name)
    {
        int before = args.Length;
        args = args.Where(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return args.Length < before;
    }

    private static string? ExtractModelFlag(ref string[] args) => ExtractFlag(ref args, "--model");

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
        AnsiConsole.MarkupLine($"  [{gold}]axiom[/] [[path]] [[--model <id>]] [[--profile <name>]]");
        AnsiConsole.MarkupLine($"                              Full-window TUI (default). path locks workspace.");
        AnsiConsole.MarkupLine($"  [{gold}]axiom config[/]                  Set your OpenRouter API key and/or self-hosted endpoint");
        AnsiConsole.MarkupLine($"  [{gold}]axiom code[/] [[--yes]] [[--json]] [[--model <id>]] <task>");
        AnsiConsole.MarkupLine($"                              Council on cwd; --yes auto-apply patch; --json machine output");
        AnsiConsole.MarkupLine($"  [{gold}]axiom update[/]                  Download and install the latest release");
        AnsiConsole.MarkupLine($"  [{gold}]axiom help[/]                    Show this help");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[{muted}]Chat: Ctrl+K palette · /continue · /export · /rename · /mode · Esc stop[/]");
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
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Tip: run [/][{AxiomTheme.Hex(AxiomTheme.Gold)}]axiom[/][{AxiomTheme.Hex(AxiomTheme.SystemMuted)}] with no args to open chat.[/]");
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

        string? existingOpenRouterKey = db.LoadOpenRouterApiKey();
        if (!string.IsNullOrWhiteSpace(existingOpenRouterKey))
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]An API key is already configured (ending in ...{Last4(existingOpenRouterKey)}).[/]");

        AnsiConsole.Markup($"Enter your [{AxiomTheme.Hex(AxiomTheme.Gold)}]OpenRouter API key[/] (from openrouter.ai/keys), or leave blank to skip: ");
        string apiKey = ReadLineSecret() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            db.SaveOpenRouterApiKey(apiKey);
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]OpenRouter key saved.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Self-hosted endpoint[/] [{AxiomTheme.Hex(AxiomTheme.SystemMuted)}](optional — leave any field blank to skip)[/]");

        string existingBaseUrl = db.GetSetting(DatabaseService.CustomEndpointBaseUrlSettingKey);
        string? existingCustomKey = db.LoadCustomEndpointApiKey();
        if (!string.IsNullOrWhiteSpace(existingBaseUrl) || !string.IsNullOrWhiteSpace(existingCustomKey))
        {
            string keyHint = string.IsNullOrWhiteSpace(existingCustomKey) ? "" : $", key ending in ...{Last4(existingCustomKey)}";
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.SystemMuted)}]Currently configured: {(string.IsNullOrWhiteSpace(existingBaseUrl) ? "(no base URL)" : existingBaseUrl)}{keyHint}[/]");
        }

        AnsiConsole.Markup("Base URL (e.g. https://your-server.example.com/v1): ");
        string baseUrlInput = (ReadLinePlain() ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(baseUrlInput))
            db.SaveSetting(DatabaseService.CustomEndpointBaseUrlSettingKey, baseUrlInput);

        AnsiConsole.Markup("Model id (e.g. llama3.1:8b): ");
        string modelIdInput = (ReadLinePlain() ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(modelIdInput))
            db.SaveSetting(DatabaseService.CustomEndpointModelIdSettingKey, modelIdInput);

        AnsiConsole.Markup("API key: ");
        string customKeyInput = ReadLineSecret() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(customKeyInput))
            db.SaveCustomEndpointApiKey(customKeyInput);

        bool configuredCustomEndpoint = !string.IsNullOrWhiteSpace(baseUrlInput)
            || !string.IsNullOrWhiteSpace(modelIdInput)
            || !string.IsNullOrWhiteSpace(customKeyInput);
        if (configuredCustomEndpoint)
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]Custom endpoint saved.[/]");

        bool savedAnything = !string.IsNullOrWhiteSpace(apiKey)
            || !string.IsNullOrWhiteSpace(existingOpenRouterKey)
            || configuredCustomEndpoint
            || !string.IsNullOrWhiteSpace(existingBaseUrl);
        if (!savedAnything)
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Nothing entered.[/]");
            return Task.FromResult(1);
        }

        AnsiConsole.MarkupLine($"Run [{AxiomTheme.Hex(AxiomTheme.Gold)}]axiom[/] to start chatting.");
        return Task.FromResult(0);
    }

    private static string Last4(string value) => value.Length <= 4 ? value : value[^4..];

    // Both credentials can coexist on the one shared chatService instance -- which one is
    // actually used per-request is decided by which model alias is selected, not by which
    // credential was loaded. Blank values are harmless: HasValidCustomEndpoint just stays false.
    private static void ApplyStoredCustomEndpoint(DatabaseService db, OpenRouterChatService chatService)
    {
        string baseUrl = db.IsReady ? db.GetSetting(DatabaseService.CustomEndpointBaseUrlSettingKey) : string.Empty;
        string modelId = db.IsReady ? db.GetSetting(DatabaseService.CustomEndpointModelIdSettingKey) : string.Empty;
        string apiKey = db.IsReady ? (db.LoadCustomEndpointApiKey() ?? string.Empty) : string.Empty;
        chatService.SetCustomEndpoint(baseUrl, apiKey, modelId);
    }

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

    private static async Task<int> RunChatAsync(string? modelOverride, string? profileOverride, string? bootstrapPath)
    {
        // Update notice before entering the alt-screen TUI (so it isn't swallowed).
        await ShowUpdateNoticeIfAvailableAsync();

        using var db = new DatabaseService();
        string? apiKey = db.IsReady ? db.LoadOpenRouterApiKey() : null;

        var profiles = new UserProfileStore();
        string profileName = profiles.ResolveActiveName(profileOverride);
        UserProfileStore.ActiveProfileName = profileName;
        UserProfile profile = profiles.Load(profileName);

        (string modelId, string modelLabel) = ResolveInitialModel(modelOverride);
        if (string.IsNullOrWhiteSpace(modelOverride)
            && !string.IsNullOrWhiteSpace(profile.DefaultModelId))
        {
            modelId = profile.DefaultModelId!;
            modelLabel = string.IsNullOrWhiteSpace(profile.DefaultModelLabel)
                ? modelId
                : profile.DefaultModelLabel!;
        }

        var chatService = new OpenRouterChatService();
        if (!string.IsNullOrWhiteSpace(apiKey))
            chatService.SetApiKey(apiKey);
        ApplyStoredCustomEndpoint(db, chatService);

        var recent = new RecentFoldersStore();
        var workspace = new WorkspaceSession(recent);
        if (profile.WorkspaceRoots.Count > 0)
            workspace.SetRoots(profile.WorkspaceRoots, profile.WorkspaceExclusive);

        // Deep link: axiom .  /  axiom C:\repo
        if (!string.IsNullOrWhiteSpace(bootstrapPath))
        {
            string path = bootstrapPath.Trim().Trim('"');
            if (path is "." or "..")
                path = Path.GetFullPath(path);
            else if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(path);
            if (File.Exists(path))
                path = Path.GetDirectoryName(path) ?? path;
            if (Directory.Exists(path))
                workspace.TrySetExclusive(path);
        }

        var toolExecutor = new AgentToolExecutor(workspace);
        var session = new ChatSession
        {
            ChatService = chatService,
            ModelId = modelId,
            ModelLabel = modelLabel,
            Workspace = workspace,
            ToolExecutor = toolExecutor
        };
        session.Tools.CouncilEnabled = profile.CouncilEnabled;
        session.Tools.WebSearchEnabled = profile.WebSearchEnabled;
        session.Tools.SandboxEnabled = profile.SandboxEnabled;
        session.Tools.CalculatorEnabled = profile.CalculatorEnabled;
        session.Tools.ApprovalMode = UserProfileStore.ParseApproval(profile.ApprovalMode);
        session.ApplyToolSettings();

        using var tui = new ChatTui();
        tui.SetProfile(profileName, profiles, profile);
        return await tui.RunAsync(
            session,
            ModelCatalog,
            handleSlash: (input, s) =>
            {
                TryHandleSlashCommand(input, s, tui);
                return Task.CompletedTask;
            },
            augmentTools: async (input, s) => await AugmentWithToolResultsAsync(input, s.Tools),
            attachPaths: AttachPathsMentionedInMessage,
            saveApiKey: key =>
            {
                if (!db.IsReady)
                    return false;
                db.SaveOpenRouterApiKey(key);
                return true;
            });
    }

    private static void AttachPathsMentionedInMessage(string input, ChatSession session)
    {
        // Paths pasted in chat messages lock the sandbox exclusively when they are real folders.
        foreach (Match match in PathInMessageRegex.Matches(input))
        {
            string path = match.Groups["path"].Value;
            session.Workspace.TrySetExclusive(path);
        }
    }

    private static (string Id, string Label) ResolveInitialModel(string? modelOverride)
    {
        if (!string.IsNullOrWhiteSpace(modelOverride) && TryResolveModel(modelOverride, out var match))
            return (match.Id, match.Label);

        return (OpenRouterChatService.Eidos1ModelId, "Eidos 1");
    }

    private static async Task<int> RunCodeAsync(
        string task,
        string? modelOverride,
        bool yesFlag,
        bool jsonFlag,
        string? profileOverride)
    {
        await ShowUpdateNoticeIfAvailableAsync();

        // Strip residual flags that may remain in the joined task string.
        task = (task ?? string.Empty)
            .Replace("--yes", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-y", "", StringComparison.OrdinalIgnoreCase)
            .Replace("--json", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (string.IsNullOrWhiteSpace(task))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]Usage:[/] axiom code [--yes] [--json] \"<task>\"");
            return 1;
        }

        using var db = new DatabaseService();
        string? apiKey = db.IsReady ? db.LoadOpenRouterApiKey() : null;

        var chatService = new OpenRouterChatService();
        if (!string.IsNullOrWhiteSpace(apiKey))
            chatService.SetApiKey(apiKey);
        ApplyStoredCustomEndpoint(db, chatService);

        if (!chatService.HasAnyValidCloudCredential)
        {
            if (jsonFlag)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = "No OpenRouter API key or custom endpoint configured. Run axiom config." }));
                return 1;
            }
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Warning)}]No OpenRouter API key or custom endpoint configured.[/] Run [{AxiomTheme.Hex(AxiomTheme.Gold)}]axiom config[/] first.");
            return 1;
        }

        var profiles = new UserProfileStore();
        string profileName = profiles.ResolveActiveName(profileOverride);
        UserProfile profile = profiles.Load(profileName);

        string councilModelId = OpenRouterChatService.WorkplaceCouncilDefaultModelId;
        string councilModelLabel = "Workplace Council default";
        if (!string.IsNullOrWhiteSpace(modelOverride) && TryResolveModel(modelOverride, out var match))
        {
            councilModelId = match.Id;
            councilModelLabel = match.Label;
        }
        else if (!string.IsNullOrWhiteSpace(profile.DefaultModelId))
        {
            councilModelId = profile.DefaultModelId!;
            councilModelLabel = profile.DefaultModelLabel ?? councilModelId;
        }

        IChatPipeline pipeline = new CloudChatPipeline(chatService, councilModelId);
        var workspaceAccess = new WorkspaceAccessService();

        string root = Environment.CurrentDirectory;
        var workspaceSession = new WorkspaceSession(attachCwd: false);
        workspaceSession.TrySetExclusive(root, remember: false);
        var agentTools = new AgentToolExecutor(workspaceSession)
        {
            WebSearchEnabled = profile.WebSearchEnabled,
            ApprovalMode = yesFlag ? ApprovalMode.Auto : UserProfileStore.ParseApproval(profile.ApprovalMode)
        };
        var orchestrator = new CouncilOrchestrator(
            pipeline,
            councilModelId,
            workspaceAccess,
            sandbox: null,
            chat: chatService,
            agentTools: agentTools);

        ConnectedWorkspaceState workspaceState = workspaceAccess.CreateFolderConnection(root);
        // --yes: auto-apply patch envelopes (CI / non-interactive).
        workspaceState.AutoApplyCodebaseChanges = yesFlag;
        string muted = AxiomTheme.Hex(AxiomTheme.SystemMuted);
        if (!jsonFlag)
        {
            AnsiConsole.MarkupLine($"[{muted}]Connected workspace:[/] {root} [{muted}]({workspaceState.IndexedFileCount} files)[/]");
            AnsiConsole.MarkupLine($"[{muted}]Model:[/] [{AxiomTheme.Hex(AxiomTheme.Gold)}]{councilModelLabel}[/]");
            AnsiConsole.MarkupLine($"[{muted}]Flags:[/] yes={yesFlag}  json={jsonFlag}  profile={profileName}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var codeTools = new CouncilToolOptions(
            SandboxEnabled: profile.SandboxEnabled || true,
            WebSearchEnabled: profile.WebSearchEnabled,
            AgenticBuilderEnabled: true);
        CouncilResult result = await orchestrator.RunAsync(
            new CouncilRequest(task, workspaceState, codeTools),
            jsonFlag ? null : CouncilConsoleRenderer.Create(),
            CancellationToken.None);
        sw.Stop();

        if (jsonFlag)
        {
            var payload = new
            {
                ok = result.Success || result.ToolCallCount > 0,
                cancelled = result.Cancelled,
                toolCalls = result.ToolCallCount,
                changedFiles = result.ChangedFiles,
                applySummary = result.ApplySummary,
                finalText = result.FinalText,
                hasPatch = result.Patch != null,
                criticIssues = result.FinalCriticReport?.FindingsCount ?? 0,
                elapsedMs = (int)sw.Elapsed.TotalMilliseconds
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return payload.ok ? 0 : 1;
        }

        if (!result.Success
            && result.Patch == null
            && result.ToolCallCount == 0
            && string.IsNullOrWhiteSpace(result.FinalText))
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Error)}]The council could not complete the task.[/]");
            ConsoleUi.WriteTurnSummary(ActivityStatus.SummarizeTurn(sw.Elapsed, 0, failed: true));
            return 1;
        }

        if (result.ToolCallCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"[{muted}]Builder tool calls:[/] {result.ToolCallCount} " +
                $"(files may already be written under {root})");
        }

        if (result.ChangedFiles is { Count: > 0 })
        {
            foreach (string f in result.ChangedFiles)
                AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]  wrote[/] {f.EscapeMarkup()}");
        }

        if (result.Patch == null)
        {
            if (!string.IsNullOrWhiteSpace(result.FinalText))
            {
                ConsoleUi.WriteAssistantHeader();
                LinkText.WriteWithLinks(result.FinalText);
                Console.WriteLine();
            }
            ConsoleUi.WriteTurnSummary(ActivityStatus.SummarizeTurn(sw.Elapsed, result.ToolCallCount));
            return result.Success || result.ToolCallCount > 0 ? 0 : 1;
        }

        if (yesFlag && result.ApplySummary != null)
        {
            AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]{result.ApplySummary.EscapeMarkup()}[/]");
            ConsoleUi.WriteTurnSummary(ActivityStatus.SummarizeTurn(sw.Elapsed, result.ToolCallCount));
            return 0;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold {AxiomTheme.Hex(AxiomTheme.TextPrimary)}]Proposed patch envelope:[/]");
        AnsiConsole.WriteLine();
        foreach (WorkspaceFilePatch filePatch in result.Patch.Files)
        {
            string target = workspaceAccess.ResolvePatchTargetPath(workspaceState, filePatch);
            string before = System.IO.File.Exists(target) ? System.IO.File.ReadAllText(target) : string.Empty;
            string after = workspaceAccess.MaterializePatchContent(workspaceState, filePatch);
            DiffRenderer.Render(filePatch.RelativePath, before, after);
            AnsiConsole.WriteLine();
        }

        ConsoleUi.WriteTurnSummary(ActivityStatus.SummarizeTurn(sw.Elapsed, result.ToolCallCount));
        AnsiConsole.Markup($"Apply patch envelope? [{AxiomTheme.Hex(AxiomTheme.Success)}]y[/]/[{AxiomTheme.Hex(AxiomTheme.Error)}]n[/]: ");
        string? answer = ReadLinePlain();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                $"[{muted}]Patch envelope discarded. " +
                $"(Any write_file tool results already on disk were not rolled back.)[/]");
            return 0;
        }

        WorkspacePatchApplyResult applyResult = workspaceAccess.ApplyPatchProposal(workspaceState, result.Patch);
        AnsiConsole.MarkupLine($"[{AxiomTheme.Hex(AxiomTheme.Success)}]{applyResult.Summary.EscapeMarkup()}[/]");
        return 0;
    }

    private static bool TryHandleSlashCommand(string input, ChatSession session, ChatTui tui)
    {
        if (!input.StartsWith('/'))
            return false;

        void Say(string msg) => tui.Notify(msg);

        string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();

        switch (cmd)
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

            case "/browse":
            case "/folder":
            case "/open":
                // Open native file explorer / folder dialog.
                tui.BrowseWorkspaceFolder();
                return true;

            case "/workspace":
            case "/ws":
                if (parts.Length >= 2)
                {
                    string sub = parts[1].ToLowerInvariant();
                    if (sub is "pick" or "browse" or "open")
                    {
                        tui.BrowseWorkspaceFolder();
                        return true;
                    }
                    if ((sub is "set" or "lock" or "use") && parts.Length >= 3)
                    {
                        string path = string.Join(' ', parts.Skip(2)).Trim().Trim('"');
                        if (session.Workspace.TrySetExclusive(path))
                            Say(DescribeWorkspaceLock(session.Workspace.PrimaryRoot));
                        else
                            Say($"Could not lock workspace (folder missing?): {path}");
                        return true;
                    }
                    if (sub is "cwd" or ".")
                    {
                        session.Workspace.TrySetExclusive(Environment.CurrentDirectory);
                        Say(DescribeWorkspaceLock(session.Workspace.PrimaryRoot));
                        return true;
                    }
                    if (sub is "clear" or "reset")
                    {
                        session.Workspace.ClearToCwd();
                        Say($"Workspace reset to cwd: {session.Workspace.PrimaryRoot}");
                        return true;
                    }
                    // Treat remaining args as a path: /workspace C:\foo
                    string direct = string.Join(' ', parts.Skip(1)).Trim().Trim('"');
                    if (session.Workspace.TrySetExclusive(direct))
                        Say(DescribeWorkspaceLock(session.Workspace.PrimaryRoot));
                    else
                        Say($"Could not lock workspace: {direct}");
                    return true;
                }
                Say(session.Workspace.IsExclusive ? "Workspace (exclusive lock):" : "Workspace roots:");
                foreach (string root in session.Workspace.Roots)
                    Say($"  • {root}");
                Say("Pick folder:  /browse  ·  @ then Browse…  ·  /workspace pick");
                Say("Or type a path:  /workspace <path>  ·  /workspace cwd");
                Say("The model can read/write/run only inside the locked folder — it does have access there.");
                return true;

            case "/sessions":
            case "/session":
            {
                string action = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
                if (action is "list" or "ls" || (parts.Length == 1 && cmd == "/sessions"))
                {
                    var list = tui.ListSessions();
                    if (list.Count == 0)
                    {
                        Say("No saved sessions yet. Chats auto-save after each turn.");
                        Say("Tip: /delete removes this chat’s save and starts fresh.");
                        return true;
                    }
                    Say("Saved sessions (auto-saved):");
                    int i = 1;
                    foreach (var item in list)
                    {
                        string cur = string.Equals(item.Id, tui.CurrentSessionId, StringComparison.OrdinalIgnoreCase)
                            ? "  ← current"
                            : "";
                        Say($"  {i,2}. {item.Title}  ·  {item.UpdatedAt.ToLocalTime():g}  ·  {item.ModelLabel}{cur}");
                        i++;
                    }
                    Say("Load: /session load <n>   Pick UI: /pick   Delete: /del <n> · /del · /del all");
                    Say("Rename: /rename title   ·   Or Ctrl+K → Session picker");
                    return true;
                }
                if ((action is "load" or "open" or "resume") && parts.Length >= 3)
                {
                    string key = string.Join(' ', parts.Skip(2));
                    if (tui.TryLoadSession(key, out string err))
                        Say("Loaded session.");
                    else
                        Say(err);
                    return true;
                }
                if ((action is "delete" or "rm" or "remove") && parts.Length >= 3)
                {
                    string key = string.Join(' ', parts.Skip(2));
                    return HandleSessionDelete(tui, key);
                }
                Say("Usage: /sessions  ·  /session load <n>  ·  /del  ·  /del <n>  ·  /del all");
                return true;
            }

            // Seamless delete: /del  ·  /delete  ·  /rm  ·  /del 2  ·  /del all
            case "/delete":
            case "/del":
            case "/rm":
            {
                string key = parts.Length >= 2 ? string.Join(' ', parts.Skip(1)).Trim() : string.Empty;
                return HandleSessionDelete(tui, key);
            }

            case "/clear":
                session.History.Clear();
                tui.ClearTranscript();
                Say("Conversation cleared. (Saved file kept — use /del to remove it and start fresh.)");
                return true;

            case "/undo":
                tui.UndoLastTurn();
                return true;

            case "/checkpoint":
            case "/cp":
                tui.HandleCheckpoint(parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : "");
                return true;

            case "/plan":
                tui.HandlePlan(parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : "");
                return true;

            case "/changes":
                tui.HandleChanges();
                return true;

            case "/accept":
                tui.HandleAccept(parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : "all");
                return true;

            case "/reject":
                tui.HandleReject(parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : "all");
                return true;

            case "/replay":
                _ = tui.HandleReplayAsync();
                return true;

            case "/jobs":
                tui.HandleJobs(parts.Length >= 2 ? parts[1] : null);
                return true;

            case "/watch":
                tui.HandleWatch(parts.Length >= 2 ? parts[1] : null);
                return true;

            case "/sticky":
                tui.HandleSticky(parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : "");
                return true;

            case "/pr":
                _ = tui.HandlePrAsync(
                    parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : "");
                return true;

            case "/network":
            case "/offline":
                tui.HandleNetwork(parts.Length >= 2 ? parts[1] : (cmd == "/offline" ? "off" : null));
                return true;

            case "/policy":
                tui.HandlePolicy();
                return true;

            case "/spec":
                tui.HandleSpec(parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : null);
                return true;

            case "/map":
                tui.HandleRepoMap();
                return true;

            case "/council":
                tui.HandleCouncil(parts.Length >= 2 ? string.Join(' ', parts.Skip(1)) : "");
                return true;

            case "/continue":
            case "/cont":
                // Fire-and-forget continuation on the TUI loop.
                _ = tui.ContinueLastTaskAsync();
                return true;

            case "/rename":
            {
                string title = parts.Length >= 2 ? string.Join(' ', parts.Skip(1)).Trim().Trim('"') : string.Empty;
                if (!tui.TryRenameCurrentSession(title, out string renErr))
                    Say(string.IsNullOrEmpty(renErr) ? "Usage: /rename short title" : renErr);
                return true;
            }

            case "/export":
            {
                bool lastOnly = parts.Length >= 2
                    && parts[1].Equals("last", StringComparison.OrdinalIgnoreCase);
                tui.ExportTranscript(lastOnly);
                return true;
            }

            case "/pick":
            case "/picker":
                tui.OpenSessionPicker();
                return true;

            case "/palette":
                tui.OpenCommandPalette();
                return true;

            case "/mode":
            {
                if (parts.Length >= 2 && session.Tools.TrySetApproval(parts[1]))
                {
                    session.ApplyToolSettings();
                    Say($"Approval mode → {session.Tools.ApprovalLabel}  (auto | ask | plan · Ctrl+Shift+M)");
                }
                else if (parts.Length >= 2)
                {
                    Say($"Unknown mode: {parts[1]}  ·  use auto | ask | plan");
                }
                else
                {
                    Say($"Current approval mode: {session.Tools.ApprovalLabel}");
                    Say("  auto — write/shell freely in sandbox");
                    Say("  ask  — confirm each write/shell/download");
                    Say("  plan — no mutations; tools return Plan-only previews");
                    Say("Set: /mode ask   ·   Cycle: Ctrl+Shift+M");
                }
                return true;
            }

            case "/resume":
            {
                var list = tui.ListSessions();
                if (list.Count == 0)
                {
                    Say("No saved sessions to resume.");
                    return true;
                }
                if (tui.TryLoadSession(list[0].Id, out string err))
                    Say($"Resumed: {list[0].Title}");
                else
                    Say(err);
                return true;
            }

            case "/help":
            case "/?":
                Say("Commands:");
                Say("  /help                 Show this help");
                Say("  Ctrl+K                Command palette (all features)");
                Say("  Ctrl+Shift+M          Cycle approval mode auto→ask→plan");
                Say("  /tools [name on|off]  Toggle council / calculator / web-search / sandbox");
                Say("  /mode [auto|ask|plan] Approval mode for writes/shell");
                Say("  /model [eidos|hepha|kestral]  Switch cloud model");
                Say("  /browse               Open file explorer and pick a work folder");
                Say("  /workspace [path]     Show or lock the agent work folder");
                Say("  @                     Recent folders + Browse… (native picker)");
                Say("  /sessions             List auto-saved sessions");
                Say("  /pick                 Interactive session picker (↑↓ Enter · d delete)");
                Say("  /session load <n>     Resume a saved session");
                Say("  /resume               Resume most recent session");
                Say("  /rename <title>       Name this session");
                Say("  /export [last]        Export transcript markdown");
                Say("  /continue             Re-run last task after stop/error");
                Say("  /del · /del <n> · /del all   Delete sessions");
                Say("  /undo                 Restore files from last agent turn");
                Say("  /checkpoint [name]    Snapshot dirty/changed files");
                Say("  /checkpoint list|restore <id>  List or restore");
                Say("  /plan [clear]         Show multi-step plan board");
                Say("  /changes              List last-turn file changes");
                Say("  /accept [all|1,3]     Keep last-turn file changes");
                Say("  /reject [all|2]       Revert selected last-turn files");
                Say("  /replay               Re-run last mutating tool plan");
                Say("  /jobs [id]            Background shell jobs");
                Say("  /watch [on|off]       Watch workspace for external edits");
                Say("  /sticky [goal] [n]    Sticky multi-turn goal (or clear)");
                Say("  /pr [title]           Push + open GitHub PR via gh");
                Say("  /network [on|off|ask] Network tools: online / offline / ask");
                Say("  /policy               Show shell policy path + builtins");
                Say("  /spec [title]         Write SPEC.md from this chat");
                Say("  /map                  Show repo map for locked folder");
                Say("  /council […]          severity, explore, loop, post-merge, roles");
                Say("  /clear                Clear transcript (keeps save file)");
                Say("  Esc                   Stop in-flight agent/council turn");
                Say("  ↑↓ scroll             (also PgUp/PgDn, Shift+arrows, wheel)");
                Say("  exit                  Leave chat");
                Say("CLI: axiom [path] · axiom --profile name · axiom code --yes --json \"task\"");
                Say("Project memory: AXIOM.md / AGENTS.md / .axiom/rules.md");
                return true;

            default:
                Say($"Unknown command: {parts[0]}  ·  type /help");
                return true;
        }
    }

    private static bool HandleSessionDelete(ChatTui tui, string key)
    {
        key = (key ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(key) || key is "current" or "." or "this")
        {
            tui.DeleteCurrentAndStartFresh();
            return true;
        }

        if (key is "all" or "*")
        {
            tui.DeleteAllSessionsAndStartFresh();
            return true;
        }

        string currentId = tui.CurrentSessionId;
        if (tui.DeleteSession(key))
        {
            bool wasCurrent = !tui.ListSessions().Any(s =>
                string.Equals(s.Id, currentId, StringComparison.OrdinalIgnoreCase));
            if (wasCurrent)
            {
                // File for this chat is gone — roll the UI to a blank session id.
                tui.DeleteCurrentAndStartFresh();
            }
            else
            {
                tui.Notify($"Deleted session {key}.");
            }
            return true;
        }

        tui.Notify($"Could not delete session: {key}  ·  try /sessions then /del <number>");
        return true;
    }

    private static string DescribeWorkspaceLock(string path)
    {
        try
        {
            var access = new WorkspaceAccessService();
            WorkspaceIndexResult index = access.IndexWorkspace(path);
            return $"Workspace locked to: {path} · {index.Files.Count} readable file(s) indexed — the model can see this folder.";
        }
        catch
        {
            return $"Workspace locked to: {path} — the model can work inside this folder.";
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
