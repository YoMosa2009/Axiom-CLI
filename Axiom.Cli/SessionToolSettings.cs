using System.Collections.Generic;
using Axiom.Core.Agent;
using Axiom.Core.Council;

namespace Axiom.Cli;

// Manual tool enablement + approval mode for the current chat session.
internal sealed class SessionToolSettings
{
    public bool CalculatorEnabled { get; set; } = true;
    public bool WebSearchEnabled { get; set; }
    public bool SandboxEnabled { get; set; }
    public bool CouncilEnabled { get; set; }

    /// <summary>Auto | Ask | Plan — how freely tools may mutate the workspace.</summary>
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Auto;

    /// <summary>Low | Medium | High | Max — Kestral 1's per-turn reasoning/round/token budget.
    /// Has no effect on Eidos 1 / Hepha 1; see EffortLevel.cs.</summary>
    public EffortLevel EffortLevel { get; set; } = EffortLevel.Medium;

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

    public string EffortLabel => EffortPolicy.Label(EffortLevel);

    public bool TrySetEffort(string name)
    {
        if (!EffortPolicy.TryParse(name, out EffortLevel level))
            return false;
        EffortLevel = level;
        return true;
    }

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
