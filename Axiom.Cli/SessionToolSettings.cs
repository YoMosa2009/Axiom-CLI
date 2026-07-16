using System.Collections.Generic;

namespace Axiom.Cli;

// Manual tool enablement for the current chat session.
// Council defaults on (main multi-agent feature from the desktop app).
// Calculator is always safe; web search leaves the machine (defaults on);
// Python sandbox defaults off until the user opts in.
internal sealed class SessionToolSettings
{
    public bool CalculatorEnabled { get; set; } = true;
    public bool WebSearchEnabled { get; set; } = true;
    public bool SandboxEnabled { get; set; }
    public bool CouncilEnabled { get; set; } = true;

    public IEnumerable<(string Name, bool Enabled)> AsList()
    {
        yield return ("council", CouncilEnabled);
        yield return ("calculator", CalculatorEnabled);
        yield return ("web-search", WebSearchEnabled);
        yield return ("sandbox", SandboxEnabled);
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
}
