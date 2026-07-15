using Axiom.Core.Workspace;

namespace Axiom.Core.Council
{
    public sealed record CouncilRequest(
        string UserPrompt,
        ConnectedWorkspaceState? Workspace);

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
