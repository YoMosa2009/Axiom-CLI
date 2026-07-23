using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    // Shared keyword-extraction + scoring formula, extracted from RepoRetrievalService so both
    // live-file scoring and KestralMemoryStore's stored-row scoring use the identical, already
    // proven logic instead of two copies that can drift apart.
    internal static class LexicalScorer
    {
        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "that", "this", "from", "into", "your", "have",
            "what", "when", "where", "which", "please", "could", "would", "should", "about",
            "file", "code", "make", "need", "just", "like", "want", "help", "using"
        };

        public static HashSet<string> ExtractKeywords(string query)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(query, @"[A-Za-z_][A-Za-z0-9_\.\-]{2,}"))
            {
                string t = m.Value;
                if (!Stop.Contains(t) && t.Length >= 3)
                    set.Add(t);
            }
            return set;
        }

        // Same weighting RepoRetrievalService validated: keyword hits dominate, a path-name match
        // is a smaller boost, and an exact substring match of the whole query is a strong signal.
        public static double Score(string text, string relPathOrSource, ICollection<string> keywords, string rawQuery)
        {
            double pathBoost = keywords.Count(k => relPathOrSource.Contains(k, StringComparison.OrdinalIgnoreCase)) * 2.0;
            int hits = keywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (hits == 0 && pathBoost == 0)
                return 0;
            double score = hits * 3.0 + pathBoost;
            if (!string.IsNullOrWhiteSpace(rawQuery) && text.Contains(rawQuery.Trim(), StringComparison.OrdinalIgnoreCase))
                score += 5;
            return score;
        }

        public static List<string> ChunkLines(string text, int linesPerChunk)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var list = new List<string>();
            for (int i = 0; i < lines.Length; i += linesPerChunk)
            {
                int take = Math.Min(linesPerChunk, lines.Length - i);
                list.Add(string.Join('\n', lines.Skip(i).Take(take)));
            }
            return list;
        }
    }
}
