using System.Collections.Generic;

namespace Axiom.Cli;

// Manual tool enablement for the current chat session. Calculator is always safe (pure math);
// web search leaves the machine (network calls, no side effects) so it defaults on; the Python
// sandbox executes arbitrary code locally, so it defaults off until the user opts in with /tools.
internal sealed class SessionToolSettings
{
    public bool CalculatorEnabled { get; set; } = true;
    public bool WebSearchEnabled { get; set; } = true;
    public bool SandboxEnabled { get; set; }

    public IEnumerable<(string Name, bool Enabled)> AsList()
    {
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
            default:
                return false;
        }
    }
}
