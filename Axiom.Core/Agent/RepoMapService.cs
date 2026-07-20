using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Cheap structural outline of a workspace: top dirs, key files, and symbol-ish lines.
    /// </summary>
    public static class RepoMapService
    {
        private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "packages",
            "dist", "build", "coverage", ".next", ".nuxt", ".turbo", "target", "__pycache__",
            ".venv", "venv", "vendor"
        };

        private static readonly string[] CodeExt =
        [
            ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
            ".cpp", ".h", ".hpp", ".c", ".rb", ".php", ".swift"
        ];

        private static readonly Regex SymbolLine = new(
            @"^\s*(public\s+|private\s+|internal\s+|protected\s+|export\s+|async\s+)?" +
            @"(class|struct|interface|enum|record|function|def|fn|func|namespace|module)\s+(\w+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public static string Build(string root, int maxChars = 3500)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return string.Empty;

            maxChars = Math.Clamp(maxChars, 800, 12_000);
            var sb = new StringBuilder();
            sb.AppendLine("[[REPO MAP]]");
            sb.AppendLine($"root: {root}");

            // Top-level entries
            try
            {
                var topDirs = Directory.EnumerateDirectories(root)
                    .Select(Path.GetFileName)
                    .Where(n => n != null && !IgnoredDirs.Contains(n!))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(24)
                    .ToList();
                var topFiles = Directory.EnumerateFiles(root)
                    .Select(Path.GetFileName)
                    .Where(n => n != null)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .ToList();
                if (topDirs.Count > 0)
                    sb.AppendLine("dirs: " + string.Join(", ", topDirs));
                if (topFiles.Count > 0)
                    sb.AppendLine("files: " + string.Join(", ", topFiles));
            }
            catch { /* ignore */ }

            // Markers
            string[] markers =
            [
                "*.sln", "*.csproj", "package.json", "pyproject.toml", "Cargo.toml", "go.mod",
                "Dockerfile", "README.md", "AXIOM.md", "AGENTS.md"
            ];
            var found = new List<string>();
            foreach (string m in markers)
            {
                try
                {
                    if (m.Contains('*'))
                    {
                        foreach (string f in Directory.EnumerateFiles(root, m, SearchOption.TopDirectoryOnly).Take(4))
                            found.Add(Path.GetFileName(f)!);
                    }
                    else if (File.Exists(Path.Combine(root, m)))
                        found.Add(m);
                }
                catch { /* ignore */ }
            }
            if (found.Count > 0)
                sb.AppendLine("markers: " + string.Join(", ", found.Distinct()));

            // Symbol samples from code files
            sb.AppendLine("symbols (sample):");
            int symbolLines = 0;
            foreach (string file in EnumerateCodeFiles(root).Take(80))
            {
                if (sb.Length > maxChars - 200 || symbolLines >= 40)
                    break;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 200_000)
                        continue;
                    string text = File.ReadAllText(file);
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    foreach (Match match in SymbolLine.Matches(text))
                    {
                        if (symbolLines >= 40 || sb.Length > maxChars - 100)
                            break;
                        string kind = match.Groups[2].Value;
                        string name = match.Groups[3].Value;
                        sb.AppendLine($"  {rel}: {kind} {name}");
                        symbolLines++;
                    }
                }
                catch { /* skip */ }
            }

            sb.AppendLine("[[END REPO MAP]]");
            string result = sb.ToString();
            if (result.Length > maxChars)
                return result[..maxChars] + "\n...[repo map truncated]\n[[END REPO MAP]]";
            return result;
        }

        private static IEnumerable<string> EnumerateCodeFiles(string root)
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
                    string ext = Path.GetExtension(f);
                    if (CodeExt.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        yield return f;
                }
            }
        }
    }
}
