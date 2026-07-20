using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Workspace;

namespace Axiom.Core.Council
{
    // Session tool rails the council pipeline may use (mirrors desktop Workplace toggles).
    // Static validation always runs when Builder output looks like code; sandbox execution
    // is gated by SandboxEnabled. Agentic tools (write_file/shell) are provided by the host
    // when AgentToolExecutor is wired into CouncilOrchestrator.
    public sealed record CouncilToolOptions(
        bool SandboxEnabled = false,
        bool CalculatorEnabled = true,
        bool WebSearchEnabled = true,
        bool AgenticBuilderEnabled = true,
        CriticSeverityPolicy SeverityPolicy = CriticSeverityPolicy.Strict,
        bool ParallelExplore = true,
        bool UserInLoopCritic = false,
        bool PostMergeCritic = true)
    {
        public static CouncilToolOptions Default { get; } = new();
    }

    /// <summary>
    /// Host callback for user-in-the-loop Critic: return 1-based indices of issues to fix.
    /// Empty list = accept remaining and stop. Null = fix all blocking issues.
    /// </summary>
    public delegate Task<IReadOnlyList<int>?> CriticIssuePicker(
        IReadOnlyList<CriticIssue> blockingIssues,
        CancellationToken cancellationToken);

    public sealed record CouncilRequest(
        string UserPrompt,
        ConnectedWorkspaceState? Workspace,
        CouncilToolOptions? Tools = null,
        CriticIssuePicker? OnPickCriticIssues = null);

    public enum CouncilEventKind
    {
        Status,
        ArchitectOutput,
        BuilderOutput,
        CriticOutput,
        ExploreOutput,
        Tool,
        Token,
        Warning,
        Completed,
        Failed
    }

    public sealed record CouncilEvent(CouncilEventKind Kind, string Message);

    public sealed record CouncilResult(
        bool Success,
        string FinalText,
        WorkspacePatchProposal? Patch,
        CriticReport FinalCriticReport,
        int ToolCallCount = 0,
        IReadOnlyList<string>? ChangedFiles = null,
        string? ApplySummary = null,
        bool Cancelled = false,
        string? ExploreSummary = null);
}
