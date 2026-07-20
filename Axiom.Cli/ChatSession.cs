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

    public CouncilToolOptions CouncilTools() => Tools.ToCouncilTools();

    public (int Used, int Max) EstimateContext()
    {
        int max = ChatService.GetApproximateContextWindowTokens(ModelId);
        string system = FoundationSystemPrompt.Apply("You are Axiom.");
        int used = ChatService.EstimateConversationTokens(History, system);
        return (used, max);
    }
}
