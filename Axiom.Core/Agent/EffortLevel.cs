namespace Axiom.Core.Agent
{
    /// <summary>
    /// How much inference-time budget Kestral 1 (the self-hosted custom endpoint) gets per turn:
    /// whether it's allowed to reason before answering, how many tool-call rounds it gets before
    /// being forced to wrap up, and how many tokens it may spend on a single response. Cloud models
    /// (Eidos 1 / Hepha 1) are unaffected -- see the gateForCustomEndpoint guards at every call site
    /// that reads this.
    /// </summary>
    public enum EffortLevel
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Max = 3
    }

    /// <summary>
    /// Concrete per-tier values for EffortLevel. Kept in one place so the tiers stay coherent
    /// (rounds/tokens/reasoning move together, not as independently-tunable knobs scattered across
    /// call sites) and so ToolCallingLoop's own hard clamps stay in sync with the highest tier.
    /// </summary>
    public static class EffortPolicy
    {
        /// <summary>Whether the model is allowed to emit a &lt;think&gt;...&lt;/think&gt; reasoning
        /// pass before its final answer. Off at Low for the fastest possible turnaround on simple
        /// asks; on everywhere else, since OmniCoder-9B's reasoning chains are real test-time-compute
        /// quality gains, not a cosmetic toggle.</summary>
        public static bool ThinkEnabled(EffortLevel level) => level != EffortLevel.Low;

        /// <summary>Tool-call rounds before the agent is forced to produce a final answer -- the
        /// direct lever for "more time to build/run commands/do things".</summary>
        public static int MaxRounds(EffortLevel level) => level switch
        {
            EffortLevel.Low => 6,
            EffortLevel.Medium => ToolCallingLoop.DefaultMaxRounds, // 12 -- unchanged existing default
            EffortLevel.High => 20,
            EffortLevel.Max => 30,
            _ => ToolCallingLoop.DefaultMaxRounds
        };

        /// <summary>Per-turn completion token budget (covers both the reasoning pass and the final
        /// answer, since Ollama shares one token stream for both). Medium matches the existing 8,192
        /// default this codebase already validated empirically against real Cloudflare/heartbeat
        /// behavior; High/Max scale up from there. Max deliberately stays well short of the
        /// 131,072-token context ceiling -- a single turn consuming the entire window would leave
        /// nothing for prompt/history, and per-turn generation time scales with this budget too.</summary>
        public static int MaxTokens(EffortLevel level) => level switch
        {
            EffortLevel.Low => 2_048,
            EffortLevel.Medium => 8_192,
            EffortLevel.High => 16_384,
            EffortLevel.Max => 32_768,
            _ => 8_192
        };

        /// <summary>How many of the most recent messages ConversationCompactor keeps verbatim
        /// before summarizing older history. Medium matches the existing Kestral-scaled default (16,
        /// vs. the base 8 for cloud models); higher tiers keep more detail in view at the cost of a
        /// larger prompt, lower tiers compact sooner to keep turns fast.</summary>
        public static int KeepRecentMessages(EffortLevel level) => level switch
        {
            EffortLevel.Low => 8,
            EffortLevel.Medium => 16,
            EffortLevel.High => 24,
            EffortLevel.Max => 32,
            _ => 16
        };

        public static string Label(EffortLevel level) => level switch
        {
            EffortLevel.Low => "low",
            EffortLevel.Medium => "medium",
            EffortLevel.High => "high",
            EffortLevel.Max => "max",
            _ => "medium"
        };

        public static string Description(EffortLevel level) => level switch
        {
            EffortLevel.Low => "Fastest — no reasoning pass, 6 tool rounds, 2K tokens/turn",
            EffortLevel.Medium => "Default — reasoning on, 12 tool rounds, 8K tokens/turn",
            EffortLevel.High => "Thorough — reasoning on, 20 tool rounds, 16K tokens/turn",
            EffortLevel.Max => "Maximum — reasoning on, 30 tool rounds, 32K tokens/turn",
            _ => string.Empty
        };

        public static bool TryParse(string? value, out EffortLevel level)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "low":
                case "fast":
                case "quick":
                    level = EffortLevel.Low;
                    return true;
                case "medium":
                case "med":
                case "default":
                case "normal":
                    level = EffortLevel.Medium;
                    return true;
                case "high":
                    level = EffortLevel.High;
                    return true;
                case "max":
                case "maximum":
                case "ultra":
                    level = EffortLevel.Max;
                    return true;
                default:
                    level = EffortLevel.Medium;
                    return false;
            }
        }
    }
}
