using System;
using System.Collections.Generic;
using System.IO;

namespace Axiom.Core.Agent
{
    // Shared workspace file-walk rules, extracted from RepoRetrievalService so KestralMemoryStore
    // (and anything else that needs to enumerate indexable text files) uses the identical
    // ignore-list/extension-filter instead of a second copy that can silently drift.
    internal static class WorkspaceFileScan
    {
        public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "packages",
            "dist", "build", "coverage", ".next", ".nuxt", ".turbo", "target", "__pycache__",
            ".venv", "venv", "vendor"
        };

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif",
            ".webp", ".ico", ".zip", ".7z", ".pdf", ".woff", ".map"
        };

        public static IEnumerable<string> EnumerateTextFiles(string root)
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
                    if (!BinaryExtensions.Contains(ext))
                        yield return f;
                }
            }
        }
    }
}
