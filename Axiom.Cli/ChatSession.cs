using System.Collections.Generic;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Council;

namespace Axiom.Cli;

// Live chat session state shared by Program and the full-window ChatTui.
internal sealed class ChatSession
{
    public required OpenRouterChatService ChatService { get; init; }
    public required string ModelId { get; set; }
    public required string ModelLabel { get; set; }
    public SessionToolSettings Tools { get; } = new();
    public List<OpenRouterMessage> History { get; } = new();
    public required WorkspaceSession Workspace { get; init; }
    public required AgentToolExecutor ToolExecutor { get; init; }

    public AgentLoop CreateAgent()
    {
        ToolExecutor.WebSearchEnabled = Tools.WebSearchEnabled;
        return new AgentLoop(ChatService, ToolExecutor, Workspace, ModelId);
    }

    public CouncilOrchestrator CreateCouncil()
    {
        // Council uses the same cloud pipeline abstraction as the desktop Workplace council.
        var pipeline = new CloudChatPipeline(ChatService, ModelId);
        return new CouncilOrchestrator(pipeline, ModelId);
    }

    public (int Used, int Max) EstimateContext()
    {
        int max = ChatService.GetApproximateContextWindowTokens(ModelId);
        string system = FoundationSystemPrompt.Apply("You are Axiom.");
        int used = ChatService.EstimateConversationTokens(History, system);
        return (used, max);
    }
}
