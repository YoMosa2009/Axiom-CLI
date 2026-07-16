using Axiom.Core.Workspace;

namespace Axiom.Core.Council
{
    // Session tool rails the council pipeline may use (mirrors desktop Workplace toggles).
    // Static validation always runs when Builder output looks like code; sandbox execution
    // is gated by SandboxEnabled.
    public sealed record CouncilToolOptions(
        bool SandboxEnabled = false,
        bool CalculatorEnabled = true,
        bool WebSearchEnabled = true)
    {
        public static CouncilToolOptions Default { get; } = new();
    }

    public sealed record CouncilRequest(
        string UserPrompt,
        ConnectedWorkspaceState? Workspace,
        CouncilToolOptions? Tools = null);

    public enum CouncilEventKind
    {
        Status,
        ArchitectOutput,
        BuilderOutput,
        CriticOutput,
        Warning,
        Completed,
        Failed
    }

    public sealed record CouncilEvent(CouncilEventKind Kind, string Message);

    public sealed record CouncilResult(
        bool Success,
        string FinalText,
        WorkspacePatchProposal? Patch,
        CriticReport FinalCriticReport);
}
