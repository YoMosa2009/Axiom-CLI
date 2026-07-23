using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Council;

namespace Axiom.Cli;

// Live chat session state shared by Program and the full-window ChatTui.
internal sealed class ChatSession
{
    private readonly object _contextGate = new();
    private OpenRouterChatService? _trackedUsageService;
    private int _peakPromptTokensThisTurn;

    public required OpenRouterChatService ChatService { get; init; }
    public required string ModelId { get; set; }
    public required string ModelLabel { get; set; }
    public SessionToolSettings Tools { get; } = new();
    public List<OpenRouterMessage> History { get; } = new();
    public required WorkspaceSession Workspace { get; init; }
    public required AgentToolExecutor ToolExecutor { get; init; }

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
        return new AgentLoop(ChatService, ToolExecutor, Workspace, ModelId);
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
            agentTools: ToolExecutor);
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
