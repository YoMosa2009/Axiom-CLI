namespace Axiom.Core.Agent
{
    /// <summary>
    /// How aggressively the agent may mutate the workspace / run shell.
    /// </summary>
    public enum ApprovalMode
    {
        /// <summary>Write/shell free inside the sandbox (default coding-agent feel).</summary>
        Auto = 0,

        /// <summary>Prompt the host before write_file, run_shell (mutating), download.</summary>
        Ask = 1,

        /// <summary>No disk mutation; tools return a plan/diff proposal instead of applying.</summary>
        Plan = 2
    }

    public sealed record ToolApprovalRequest(
        string ToolName,
        string Summary,
        string Detail);

    public delegate Task<bool> ToolApprovalHandler(ToolApprovalRequest request, CancellationToken token);
}
