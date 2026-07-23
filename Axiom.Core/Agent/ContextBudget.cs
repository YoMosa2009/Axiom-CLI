using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    // Kestral-only safety net: today, workspaceContext/effectiveUser blocks (project memory,
    // repo map/retrieval, task contract, explore lane, etc.) are concatenated with no aggregate
    // size check, and OpenRouterChatService.TrimConversationHistory explicitly exempts the
    // current turn's content from trimming -- so an oversized blob is sent to Ollama as-is with
    // no truncation anywhere in the pipeline. This clamps the *combined* size for kestral before
    // it reaches the model, keeping the highest-priority blocks and truncating/dropping the rest.
    // Not used for cloud models (eidos/hepha) -- their unbounded 131k+ windows don't need this.
    public static class ContextBudget
    {
        public readonly record struct Block(string Label, string Text, int Priority);

        // Shared ratio validated for kestral's real ~9k-token window (CouncilOrchestrator's
        // WorkspaceContextBudgetChars=60,000 assumes a 131k+-token cloud window) -- same formula
        // used everywhere a per-turn char budget needs to scale with the model's actual context
        // window instead of a second hand-written copy.
        public static int CharBudgetForContextWindow(int contextWindowTokens)
        {
            const double shareOfWindow = 0.35;
            const double approxCharsPerToken = 3.5;
            return (int)(contextWindowTokens * shareOfWindow * approxCharsPerToken);
        }

        // Highest priority first; ties keep original relative order (stable sort).
        public static string EnforceBudget(IReadOnlyList<Block> blocks, int maxChars)
        {
            if (blocks.Count == 0 || maxChars <= 0)
                return string.Empty;

            var ordered = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                .OrderByDescending(b => b.Priority)
                .ToList();

            var sb = new StringBuilder();
            int remaining = maxChars;
            bool first = true;
            foreach (Block block in ordered)
            {
                if (remaining <= 0)
                    break;

                string text = block.Text.Trim();
                string separator = first ? "" : "\n\n";
                int available = remaining - separator.Length;
                if (available <= 0)
                    break;

                if (text.Length > available)
                {
                    // Only worth including a truncated tail if there's meaningful room left;
                    // otherwise drop this (and every lower-priority) block entirely.
                    if (available < 200)
                        break;
                    text = text[..available] + "\n[...truncated to fit context budget...]";
                }

                sb.Append(separator).Append(text);
                remaining -= separator.Length + text.Length;
                first = false;
            }

            return sb.ToString();
        }
    }
}
