using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Lexical chunk retrieval over the workspace (no embeddings required).
    /// </summary>
    public static class RepoRetrievalService
    {
        private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "packages",
            "dist", "build", "coverage", ".next", ".nuxt", ".turbo", "target", "__pycache__",
            ".venv", "venv", "vendor"
        };

        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "that", "this", "from", "into", "your", "have",
            "what", "when", "where", "which", "please", "could", "would", "should", "about",
            "file", "code", "make", "need", "just", "like", "want", "help", "using"
        };

        public static string Retrieve(string root, string query, int maxChunks = 5, int maxChars = 4000)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || string.IsNullOrWhiteSpace(query))
                return string.Empty;

            var keywords = ExtractKeywords(query);
            if (keywords.Count == 0)
                return string.Empty;

            maxChunks = Math.Clamp(maxChunks, 1, 12);
            maxChars = Math.Clamp(maxChars, 800, 16_000);

            var scored = new List<(double Score, string Path, string Snippet)>();
            foreach (string file in EnumerateTextFiles(root).Take(400))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length is 0 or > 250_000)
                        continue;
                    string text = File.ReadAllText(file);
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    double pathBoost = keywords.Count(k => rel.Contains(k, StringComparison.OrdinalIgnoreCase)) * 2.0;
                    var chunks = Chunk(text, 40);
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        string chunk = chunks[i];
                        int hits = keywords.Count(k => chunk.Contains(k, StringComparison.OrdinalIgnoreCase));
                        if (hits == 0 && pathBoost == 0)
                            continue;
                        double score = hits * 3.0 + pathBoost;
                        if (chunk.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                            score += 5;
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

        private static List<string> Chunk(string text, int linesPerChunk)
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

        private static HashSet<string> ExtractKeywords(string query)
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

        private static IEnumerable<string> EnumerateTextFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                IEnumerable<string> subDirs;
                IEnumerable<string> files;
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir);
                    files = Directory.EnumerateFiles(dir);
                }
                catch { continue; }

                foreach (string d in subDirs)
                {
                    string name = Path.GetFileName(d);
                    if (!IgnoredDirs.Contains(name))
                        stack.Push(d);
                }

                foreach (string f in files)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif"
                        or ".webp" or ".ico" or ".zip" or ".7z" or ".pdf" or ".woff" or ".map")
                        continue;
                    yield return f;
                }
            }
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
