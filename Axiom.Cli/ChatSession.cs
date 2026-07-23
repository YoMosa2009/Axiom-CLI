using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Council;

namespace Axiom.Cli;

// Live chat session state shared by Program and the full-window ChatTui.
internal sealed class ChatSession
{
    private const long DefaultKestralMemoryByteBudget = 10_000_000_000L; // 10 GB

    private readonly object _contextGate = new();
    private OpenRouterChatService? _trackedUsageService;
    private int _peakPromptTokensThisTurn;
    private KestralMemoryStore? _kestralMemoryStore;
    private bool _kestralMemoryAttempted;

    public required OpenRouterChatService ChatService { get; init; }
    public required string ModelId { get; set; }
    public required string ModelLabel { get; set; }
    public SessionToolSettings Tools { get; } = new();
    public List<OpenRouterMessage> History { get; } = new();
    public required WorkspaceSession Workspace { get; init; }
    public required AgentToolExecutor ToolExecutor { get; init; }

    // Per-machine override for kestral's persistent memory store (path/budget) -- set from the
    // active UserProfile at session construction. Null values fall back to AppPaths' cross
    // -platform default and a 10GB budget.
    public string? KestralMemoryDir { get; set; }
    public long? KestralMemoryByteBudget { get; set; }

    public void ApplyToolSettings()
    {
        ToolExecutor.WebSearchEnabled = Tools.WebSearchEnabled;
        ToolExecutor.ApprovalMode = Tools.ApprovalMode;
        ToolExecutor.SubagentHandler = async (kind, task, ct) =>
        {
            var runner = new SubagentRunner(ChatService, ToolExecutor, Workspace, ModelId);
            return await runner.RunAsync(kind, task, onStatus: null, onToolEvent: null, ct);
        };
    }

    public AgentLoop CreateAgent()
    {
        ApplyToolSettings();
        return new AgentLoop(ChatService, ToolExecutor, Workspace, ModelId, ResolveKestralMemory());
    }

    public CouncilOrchestrator CreateCouncil()
    {
        ApplyToolSettings();
        var pipeline = new CloudChatPipeline(ChatService, ModelId);
        return new CouncilOrchestrator(
            pipeline,
            ModelId,
            workspace: null,
            sandbox: null,
            chat: ChatService,
            agentTools: ToolExecutor,
            kestralMemory: ResolveKestralMemory());
    }

    // Lazy + cached for the session's lifetime: only ever constructed when kestral is the active
    // model, so eidos/hepha sessions never touch disk for this at all. If the user switches models
    // mid-session, re-resolves (a stale store for a since-abandoned model id is harmless -- it's
    // simply not passed to a future non-kestral Create*() call).
    private KestralMemoryStore? ResolveKestralMemory()
    {
        bool isCustomEndpoint = string.Equals(ModelId, OpenRouterChatService.CustomEndpointModelId, StringComparison.OrdinalIgnoreCase);
        if (!isCustomEndpoint)
            return null;
        if (_kestralMemoryAttempted)
            return _kestralMemoryStore;

        _kestralMemoryAttempted = true;
        string dir = !string.IsNullOrWhiteSpace(KestralMemoryDir) ? KestralMemoryDir! : AppPaths.KestralMemoryRoot;
        long budget = KestralMemoryByteBudget is > 0 ? KestralMemoryByteBudget.Value : DefaultKestralMemoryByteBudget;
        string dbPath = Path.Combine(dir, "kestral_memory.db");
        _kestralMemoryStore = new KestralMemoryStore(dbPath, budget);
        return _kestralMemoryStore;
    }

    public CouncilToolOptions CouncilTools()
        => CouncilToolOptions.ForModel(Tools.ToCouncilTools(), ModelId);

    public void BeginContextTurn()
    {
        EnsureUsageTracking();
        string system = FoundationSystemPrompt.Apply("You are Axiom.");
        int historyTokens = ChatService.EstimateConversationTokensForBudget(History, system);
        lock (_contextGate)
            _peakPromptTokensThisTurn = historyTokens;
    }

    public (int Used, int Max) EstimateContext()
    {
        EnsureUsageTracking();
        int max = ChatService.GetApproximateContextWindowTokens(ModelId);
        string system = FoundationSystemPrompt.Apply("You are Axiom.");
        int historyTokens = ChatService.EstimateConversationTokensForBudget(History, system);
        int observedPromptTokens;
        lock (_contextGate)
        {
            _peakPromptTokensThisTurn = Math.Max(_peakPromptTokensThisTurn, historyTokens);
            observedPromptTokens = _peakPromptTokensThisTurn;
        }

        // Council calls carry plans, workspace retrieval, tool schemas, and Critic evidence that
        // do not live in chat history. Show the largest real (or service-estimated) prompt used
        // during this turn rather than pretending the header is only the short chat transcript.
        int used = Math.Max(historyTokens, observedPromptTokens);
        return (used, max);
    }

    private void EnsureUsageTracking()
    {
        if (ReferenceEquals(_trackedUsageService, ChatService))
            return;

        lock (_contextGate)
        {
            if (ReferenceEquals(_trackedUsageService, ChatService))
                return;

            if (_trackedUsageService != null)
                _trackedUsageService.TokenUsageRecorded -= OnTokenUsageRecorded;
            _trackedUsageService = ChatService;
            _trackedUsageService.TokenUsageRecorded += OnTokenUsageRecorded;
        }
    }

    private void OnTokenUsageRecorded(OpenRouterTokenUsage usage)
    {
        if (usage?.PromptTokens <= 0)
            return;

        lock (_contextGate)
            _peakPromptTokensThisTurn = Math.Max(_peakPromptTokensThisTurn, usage.PromptTokens);
    }
}
