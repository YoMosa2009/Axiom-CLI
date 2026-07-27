using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Axiom.Core.Chat;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Compacts long chat history: summarize old turns, drop bulky tool payloads.
    /// </summary>
    public static class ConversationCompactor
    {
        public const int SoftWarnTokens = 24_000;
        public const int HardCompactTokens = 48_000;
        public const int KeepRecentMessages = 8;

        public sealed record CompactResult(
            List<OpenRouterMessage> Messages,
            string? SummaryBlock,
            bool Compacted,
            string? BudgetWarning);

        public static CompactResult Compact(
            IReadOnlyList<OpenRouterMessage> history,
            int estimatedTokens,
            int contextWindowTokens,
            bool scaleThresholdsToWindow = false,
            int? keepRecentMessagesOverride = null)
        {
            var list = history?.ToList() ?? new List<OpenRouterMessage>();
            string? warn = null;

            // SoftWarnTokens/HardCompactTokens (24k/48k) were calibrated back when Kestral 1's real
            // window was ~45k -- close enough to the hard ceiling that it rarely mattered in
            // practice. The window has since grown (114688 and climbing as more headroom gets
            // validated), and holding the same fixed ceiling there means compaction fires earlier
            // and earlier relative to what's actually available, directly cutting into how long a
            // session stays usable before losing detail to summarization. Scale proportionally to
            // the real window for custom-endpoint callers instead, with a generous sanity ceiling
            // rather than the old fixed one; cloud model behavior (scaleThresholdsToWindow: false)
            // is unchanged.
            int softCeiling = scaleThresholdsToWindow ? 200_000 : SoftWarnTokens;
            int hardCeiling = scaleThresholdsToWindow ? 260_000 : HardCompactTokens;
            int soft = Math.Min(softCeiling, Math.Max(4000, contextWindowTokens * 45 / 100));
            int hard = Math.Min(hardCeiling, Math.Max(8000, contextWindowTokens * 70 / 100));

            if (estimatedTokens >= soft)
            {
                warn = estimatedTokens >= hard
                    ? $"Context is large (~{estimatedTokens:N0} tokens). History was compacted."
                    : $"Context growing (~{estimatedTokens:N0} / {contextWindowTokens:N0} tokens). Prefer concise tools.";
            }

            // Always strip oversized tool/assistant payloads from older messages.
            TrimToolSpam(list);

            int keep = keepRecentMessagesOverride is > 0 ? keepRecentMessagesOverride.Value : KeepRecentMessages;

            if (estimatedTokens < hard || list.Count <= keep + 2)
            {
                return new CompactResult(list, null, Compacted: false, warn);
            }

            if (list.Count <= keep)
                return new CompactResult(list, null, false, warn);

            var old = list.Take(list.Count - keep).ToList();
            var recent = list.Skip(list.Count - keep).ToList();
            string summary = SummarizeLocally(old);
            var compacted = new List<OpenRouterMessage>();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                compacted.Add(new OpenRouterMessage(
                    "user",
                    "[COMPACTED EARLIER CONVERSATION]\n" + summary + "\n[END COMPACTED]",
                    PreserveFullText: true));
                compacted.Add(new OpenRouterMessage(
                    "assistant",
                    "Understood — I'll use the compacted summary as background and focus on the recent turns.",
                    PreserveFullText: true));
            }
            compacted.AddRange(recent);
            return new CompactResult(compacted, summary, Compacted: true, warn);
        }

        public static string BudgetStatus(int used, int max)
        {
            if (max <= 0)
                return "";
            double pct = 100.0 * used / max;
            if (pct >= 70)
                return $"⚠ context {used:N0}/{max:N0} ({pct:0}%)";
            if (pct >= 50)
                return $"context {used:N0}/{max:N0} ({pct:0}%)";
            return "";
        }

        private static void TrimToolSpam(List<OpenRouterMessage> list)
        {
            // Keep last 4 messages intact; trim earlier tool/assistant blobs.
            int protectFrom = Math.Max(0, list.Count - 4);
            for (int i = 0; i < protectFrom; i++)
            {
                var m = list[i];
                if (m.Role is "tool" or "assistant")
                {
                    string c = m.Text ?? "";
                    if (c.Length > 1200)
                    {
                        list[i] = new OpenRouterMessage(
                            m.Role,
                            c[..1000] + "\n...[earlier tool/output compacted]",
                            ToolCallId: m.ToolCallId,
                            ToolCalls: m.ToolCalls,
                            PreserveFullText: true);
                    }
                }
            }
        }

        private static string SummarizeLocally(IReadOnlyList<OpenRouterMessage> old)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Earlier turns (auto-summarized, no model call):");
            int n = 0;
            foreach (var m in old)
            {
                if (m.Role is not ("user" or "assistant"))
                    continue;
                string c = (m.Text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (c.Length == 0)
                    continue;
                if (c.Length > 180)
                    c = c[..177] + "…";
                sb.AppendLine($"- {m.Role}: {c}");
                if (++n >= 16)
                    break;
            }
            return sb.ToString().TrimEnd();
        }
    }
}
