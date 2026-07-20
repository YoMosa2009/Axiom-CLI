using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Lightweight symbol search (rg/regex) as a practical stand-in for full LSP.
    /// </summary>
    public static class SymbolSearchService
    {
        public static async Task<string> FindAsync(
            string root,
            string symbol,
            string mode,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return "Error: symbol is required.";
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return "Error: workspace root not found.";

            mode = (mode ?? "def").Trim().ToLowerInvariant();
            string pattern = mode switch
            {
                "ref" or "refs" or "references" => $@"\b{Regex.Escape(symbol)}\b",
                _ => BuildDefPattern(symbol)
            };

            string result = await WorkspaceSearchService.SearchAsync(
                root, pattern, glob: null, regex: true, maxHits: 40, token);

            var sb = new StringBuilder();
            sb.AppendLine($"[[SYMBOL SEARCH]] mode={mode} symbol={symbol}");
            sb.AppendLine(result);
            return sb.ToString().TrimEnd();
        }

        private static string BuildDefPattern(string symbol)
        {
            string esc = Regex.Escape(symbol);
            // Common definition forms across C#/TS/JS/Python/Go/Rust
            return string.Join("|",
                $@"\b(class|struct|interface|enum|record)\s+{esc}\b",
                $@"\b(def|async\s+def)\s+{esc}\s*\(",
                $@"\b(function|async\s+function)\s+{esc}\s*\(",
                $@"\b(fn|func)\s+{esc}\s*[\(<]",
                $@"\b(public|private|internal|protected|static|async|void|Task|int|string|bool|var)\s+[\w<>,\s]*\b{esc}\s*\(",
                $@"\b{esc}\s*[:=]\s*(function|\(|async)");
        }
    }
}
