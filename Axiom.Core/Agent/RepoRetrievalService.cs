using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Lexical chunk retrieval over the workspace (no embeddings required).
    /// </summary>
    public static class RepoRetrievalService
    {
        public static string Retrieve(string root, string query, int maxChunks = 5, int maxChars = 4000)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(query))
                return string.Empty;

            var keywords = LexicalScorer.ExtractKeywords(query);
            if (keywords.Count == 0)
                return string.Empty;

            maxChunks = Math.Clamp(maxChunks, 1, 12);
            maxChars = Math.Clamp(maxChars, 800, 16_000);

            var scored = new List<(double Score, string Path, string Snippet)>();
            foreach (string file in WorkspaceFileScan.EnumerateTextFiles(root).Take(400))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length is 0 or > 250_000)
                        continue;
                    string text = File.ReadAllText(file);
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var chunks = LexicalScorer.ChunkLines(text, 40);
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        string chunk = chunks[i];
                        double score = LexicalScorer.Score(chunk, rel, keywords, query);
                        if (score < 2)
                            continue;
                        string snippet = chunk.Length > 700 ? chunk[..700] + "…" : chunk;
                        scored.Add((score, $"{rel}#chunk{i}", snippet));
                    }
                }
                catch { /* skip */ }
            }

            if (scored.Count == 0)
                return string.Empty;

            var top = scored
                .OrderByDescending(s => s.Score)
                .Take(maxChunks)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("[[REPO RETRIEVAL]]");
            sb.AppendLine($"query: {Truncate(query, 120)}");
            int used = sb.Length;
            foreach (var hit in top)
            {
                string block = $"--- {hit.Path} (score {hit.Score:0.0}) ---\n{hit.Snippet}\n";
                if (used + block.Length > maxChars)
                    break;
                sb.Append(block);
                used += block.Length;
            }
            sb.AppendLine("[[END REPO RETRIEVAL]]");
            return sb.ToString().TrimEnd();
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
