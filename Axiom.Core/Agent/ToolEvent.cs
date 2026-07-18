using System;

namespace Axiom.Core.Agent
{
    public enum ToolEventPhase
    {
        Started,
        Finished,
        Denied,
        Planned
    }

    public sealed record ToolEvent(
        ToolEventPhase Phase,
        string ToolName,
        string Detail,
        string? ResultPreview = null,
        bool IsError = false);
}
