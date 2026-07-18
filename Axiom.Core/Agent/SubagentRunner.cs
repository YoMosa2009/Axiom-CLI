using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Axiom.Core.Council;

namespace Axiom.Core.Agent
{
    public enum SubagentKind
    {
        Explore,
        Tests,
        Fix
    }

    public sealed record SubagentResult(
        SubagentKind Kind,
        string Summary,
        int ToolCalls,
        IReadOnlyList<string> WrittenPaths);

    // Bounded specialist loops the Builder/council can spawn for explore / tests / fix passes.
    public sealed class SubagentRunner
    {
        private readonly OpenRouterChatService _chat;
        private readonly AgentToolExecutor _tools;
        private readonly WorkspaceSession _workspace;
        private readonly string _modelId;

        public SubagentRunner(
            OpenRouterChatService chat,
            AgentToolExecutor tools,
            WorkspaceSession workspace,
            string modelId)
        {
            _chat = chat;
            _tools = tools;
            _workspace = workspace;
            _modelId = modelId;
        }

        public async Task<SubagentResult> RunAsync(
            SubagentKind kind,
            string task,
            Action<string>? onStatus,
            Action<ToolEvent>? onToolEvent,
            CancellationToken cancellationToken)
        {
            string system = FoundationSystemPrompt.Apply(BuildSystem(kind));
            string memory = ProjectMemory.BuildContextBlock(_workspace.PrimaryRoot);
            string ws = _workspace.BuildContextBlock();
            var sb = new StringBuilder();
            sb.AppendLine(task);
            if (!string.IsNullOrWhiteSpace(memory))
                sb.AppendLine().AppendLine(memory);
            if (!string.IsNullOrWhiteSpace(ws))
                sb.AppendLine().AppendLine(ws);

            _tools.ClearWrittenPaths();
            var loop = new ToolCallingLoop(_chat, _tools, _modelId, maxRounds: kind == SubagentKind.Explore ? 8 : 10);
            AgentToolExecutor.ToolScope scope = kind == SubagentKind.Explore
                ? AgentToolExecutor.ToolScope.Inspect
                : AgentToolExecutor.ToolScope.Full;

            ToolCallingResult result = await loop.RunAsync(
                system,
                sb.ToString(),
                onStatus,
                cancellationToken,
                scope,
                onToolEvent: onToolEvent,
                onToken: null);

            return new SubagentResult(
                kind,
                result.FinalText,
                result.ToolCallCount,
                _tools.WrittenPaths);
        }

        private static string BuildSystem(SubagentKind kind) => kind switch
        {
            SubagentKind.Explore =>
                "You are an explore-only subagent. Use list_dir, read_file, search_files, and non-destructive shell. " +
                "Do not modify files. Return a concise map of relevant paths, symbols, and how they connect.",
            SubagentKind.Tests =>
                "You are a tests subagent. Add or fix tests for the user's request. Use write_file and run_shell/diagnostics. " +
                "Prefer failing tests first when missing coverage, then implement until green when feasible.",
            SubagentKind.Fix =>
                "You are a fix subagent. Reproduce and fix the reported issue with minimal diffs. " +
                "Use tools, run diagnostics, and summarize what changed.",
            _ => "You are a focused coding subagent. Complete the assigned task with tools."
        };
    }
}
