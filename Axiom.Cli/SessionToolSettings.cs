using System.Collections.Generic;
using Axiom.Core.Agent;
using Axiom.Core.Council;

namespace Axiom.Cli;

// Manual tool enablement + approval mode for the current chat session.
internal sealed class SessionToolSettings
{
    public bool CalculatorEnabled { get; set; } = true;
    public bool WebSearchEnabled { get; set; } = true;
    public bool SandboxEnabled { get; set; }
    public bool CouncilEnabled { get; set; } = true;

    /// <summary>Auto | Ask | Plan — how freely tools may mutate the workspace.</summary>
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Auto;

    public CriticSeverityPolicy CriticSeverity { get; set; } = CriticSeverityPolicy.Strict;
    public bool ParallelExplore { get; set; } = true;
    public bool UserInLoopCritic { get; set; }
    public bool PostMergeCritic { get; set; } = true;
    public CouncilRoleVisibility RoleVisibility { get; set; } = CouncilRoleVisibility.Full;

    public IEnumerable<(string Name, bool Enabled)> AsList()
    {
        yield return ("council", CouncilEnabled);
        yield return ("calculator", CalculatorEnabled);
        yield return ("web-search", WebSearchEnabled);
        yield return ("sandbox", SandboxEnabled);
    }

    public string ApprovalLabel => ApprovalMode switch
    {
        ApprovalMode.Ask => "ask",
        ApprovalMode.Plan => "plan",
        _ => "auto"
    };

    public string CouncilLabel
    {
        get
        {
            if (!CouncilEnabled)
                return "agent";
            string sev = Axiom.Core.Council.CriticSeverity.Describe(CriticSeverity);
            return $"council/{sev}";
        }
    }

    public bool TrySetApproval(string name)
    {
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "auto":
            case "yolo":
                ApprovalMode = ApprovalMode.Auto;
                return true;
            case "ask":
            case "confirm":
            case "safe":
                ApprovalMode = ApprovalMode.Ask;
                return true;
            case "plan":
            case "readonly":
            case "read-only":
            case "dry":
            case "dry-run":
                ApprovalMode = ApprovalMode.Plan;
                return true;
            default:
                return false;
        }
    }

    public bool TrySet(string name, bool enabled)
    {
        switch (name.ToLowerInvariant())
        {
            case "calc":
            case "calculator":
                CalculatorEnabled = enabled;
                return true;
            case "web":
            case "web-search":
            case "websearch":
                WebSearchEnabled = enabled;
                return true;
            case "sandbox":
            case "python":
                SandboxEnabled = enabled;
                return true;
            case "council":
            case "multi":
            case "agents":
                CouncilEnabled = enabled;
                return true;
            default:
                return false;
        }
    }

    public bool TryToggle(string name, out bool nowEnabled)
    {
        nowEnabled = false;
        switch (name.ToLowerInvariant())
        {
            case "calc":
            case "calculator":
                CalculatorEnabled = !CalculatorEnabled;
                nowEnabled = CalculatorEnabled;
                return true;
            case "web":
            case "web-search":
            case "websearch":
                WebSearchEnabled = !WebSearchEnabled;
                nowEnabled = WebSearchEnabled;
                return true;
            case "sandbox":
            case "python":
                SandboxEnabled = !SandboxEnabled;
                nowEnabled = SandboxEnabled;
                return true;
            case "council":
            case "multi":
            case "agents":
                CouncilEnabled = !CouncilEnabled;
                nowEnabled = CouncilEnabled;
                return true;
            default:
                return false;
        }
    }

    public CouncilToolOptions ToCouncilTools() => new(
        SandboxEnabled: SandboxEnabled,
        CalculatorEnabled: CalculatorEnabled,
        WebSearchEnabled: WebSearchEnabled,
        AgenticBuilderEnabled: true,
        SeverityPolicy: CriticSeverity,
        ParallelExplore: ParallelExplore,
        UserInLoopCritic: UserInLoopCritic,
        PostMergeCritic: PostMergeCritic);
}
