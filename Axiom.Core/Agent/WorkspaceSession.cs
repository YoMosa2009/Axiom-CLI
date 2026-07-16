using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    // Directories the user has attached for this chat session. All agent filesystem + shell
    // work is constrained to these roots (and their descendants) only — no path traversal out.
    public sealed class WorkspaceSession
    {
        private readonly List<string> _roots = new();
        private readonly RecentFoldersStore _recent;
        private readonly object _gate = new();
        private bool _exclusive;

        public WorkspaceSession(RecentFoldersStore? recent = null, bool attachCwd = true)
        {
            _recent = recent ?? new RecentFoldersStore();
            if (attachCwd)
                TryAttach(Environment.CurrentDirectory, remember: false);
        }

        public IReadOnlyList<string> Roots
        {
            get { lock (_gate) return _roots.ToList(); }
        }

        /// <summary>When true, only the explicitly set roots are allowed (no silent multi-root drift).</summary>
        public bool IsExclusive
        {
            get { lock (_gate) return _exclusive; }
        }

        public string PrimaryRoot
        {
            get
            {
                lock (_gate)
                    return _roots.Count > 0 ? _roots[0] : Environment.CurrentDirectory;
            }
        }

        public RecentFoldersStore Recent => _recent;

        public bool TryAttach(string path, bool remember = true)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!TryNormalizeExistingDirectory(path, out string full))
                return false;

            lock (_gate)
            {
                // In exclusive mode, attach replaces the single locked root rather than stacking.
                if (_exclusive)
                {
                    _roots.Clear();
                    _roots.Add(full);
                }
                else
                {
                    _roots.RemoveAll(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));
                    _roots.Insert(0, full);
                }
            }

            if (remember)
                _recent.Remember(full);

            return true;
        }

        /// <summary>
        /// Locks the agent to exactly one directory. All other roots are dropped.
        /// The agent cannot operate outside this tree until the user changes it.
        /// </summary>
        public bool TrySetExclusive(string path, bool remember = true)
        {
            if (!TryNormalizeExistingDirectory(path, out string full))
                return false;

            lock (_gate)
            {
                _roots.Clear();
                _roots.Add(full);
                _exclusive = true;
            }

            if (remember)
                _recent.Remember(full);

            return true;
        }

        public void SetRoots(IEnumerable<string> roots, bool exclusive)
        {
            lock (_gate)
            {
                _roots.Clear();
                foreach (string r in roots)
                {
                    if (TryNormalizeExistingDirectory(r, out string full)
                        && !_roots.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                        _roots.Add(full);
                }
                _exclusive = exclusive;
                if (_roots.Count == 0)
                {
                    _exclusive = false;
                    TryAttach(Environment.CurrentDirectory, remember: false);
                }
            }
        }

        public void ClearToCwd()
        {
            lock (_gate)
            {
                _roots.Clear();
                _exclusive = false;
            }
            TryAttach(Environment.CurrentDirectory, remember: false);
        }

        public bool IsPathAllowed(string path)
        {
            if (!TryNormalizePath(path, out string full))
                return false;

            lock (_gate)
            {
                if (_roots.Count == 0)
                    return false;

                foreach (string root in _roots)
                {
                    if (IsUnderRoot(full, root))
                        return true;
                }
            }

            return false;
        }

        public string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return PrimaryRoot;

            path = path.Trim().Trim('"');
            if (Path.IsPathRooted(path))
            {
                TryNormalizePath(path, out string rooted);
                return rooted;
            }

            TryNormalizePath(Path.Combine(PrimaryRoot, path), out string combined);
            return combined;
        }

        /// <summary>
        /// Returns false if any absolute path token in a shell command resolves outside the sandbox.
        /// Relative paths are resolved against <paramref name="cwd"/> (which must already be allowed).
        /// </summary>
        public bool TryValidateShellCommand(string command, string cwd, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                reason = "empty command";
                return false;
            }

            if (!IsPathAllowed(cwd))
            {
                reason = $"working directory outside workspace: {cwd}";
                return false;
            }

            // Block obvious escape hatch patterns.
            string lowered = command.ToLowerInvariant();
            string[] blocked =
            [
                "set-location -path /", "set-location /", "cd /", "cd \\",
                "push-location /", "chroot ", "mount ", "net use ",
                "new-psdrive ", "subst "
            ];
            foreach (string b in blocked)
            {
                if (lowered.Contains(b, StringComparison.Ordinal))
                {
                    reason = $"blocked sandbox escape pattern: {b.Trim()}";
                    return false;
                }
            }

            // Validate absolute path tokens (Windows drive or UNC or Unix root).
            foreach (string token in ExtractPathTokens(command))
            {
                string candidate = token.Trim().Trim('"', '\'');
                if (candidate.Length == 0)
                    continue;

                // Skip pure flags / switches
                if (candidate.StartsWith('-') || candidate.StartsWith('/'))
                {
                    // Unix absolute still starts with / — allow only if it's a path with more chars
                    if (candidate.Length > 1 && candidate[1] is not ('-' or '?') && candidate.Contains('/'))
                    {
                        // might be unix path
                    }
                    else if (candidate.StartsWith('-') || (candidate.Length == 2 && char.IsLetter(candidate[1])))
                        continue;
                }

                bool looksAbsolute =
                    Path.IsPathRooted(candidate)
                    || (candidate.Length >= 2 && char.IsLetter(candidate[0]) && candidate[1] == ':')
                    || candidate.StartsWith(@"\\", StringComparison.Ordinal)
                    || candidate.StartsWith('/');

                if (!looksAbsolute)
                {
                    // Relative: resolve under cwd and ensure still inside.
                    if (!TryNormalizePath(Path.Combine(cwd, candidate), out string relFull))
                        continue;
                    // Only enforce if the token looks path-like (has separator or extension-ish).
                    if ((candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains(".."))
                        && !IsPathAllowed(relFull))
                    {
                        reason = $"path leaves workspace: {candidate}";
                        return false;
                    }
                    continue;
                }

                if (!TryNormalizePath(candidate, out string absFull))
                {
                    reason = $"invalid path: {candidate}";
                    return false;
                }

                // If the path doesn't exist yet, still require it under a root (writes).
                if (!IsPathAllowed(absFull))
                {
                    reason = $"absolute path outside workspace: {absFull}";
                    return false;
                }
            }

            // `..` segments that would walk above cwd
            if (command.Contains("..", StringComparison.Ordinal))
            {
                // Soft check: if any .. path resolves outside after combining with cwd, reject.
                foreach (string token in ExtractPathTokens(command))
                {
                    if (!token.Contains("..", StringComparison.Ordinal))
                        continue;
                    string cand = token.Trim().Trim('"', '\'');
                    string combined = Path.IsPathRooted(cand) ? cand : Path.Combine(cwd, cand);
                    if (TryNormalizePath(combined, out string full) && !IsPathAllowed(full))
                    {
                        reason = $"path traversal outside workspace: {cand}";
                        return false;
                    }
                }
            }

            return true;
        }

        public string BuildContextBlock()
        {
            lock (_gate)
            {
                if (_roots.Count == 0)
                    return string.Empty;

                var sb = new StringBuilder();
                sb.AppendLine("[[ATTACHED WORKSPACES — SANDBOX]]");
                sb.AppendLine(_exclusive
                    ? "EXCLUSIVE LOCK: you may ONLY operate inside this directory tree. Leaving it is forbidden."
                    : "You may read, write, list, run shell commands, build, and download ONLY inside these directories:");
                for (int i = 0; i < _roots.Count; i++)
                {
                    string label = i == 0 ? "primary" : $"extra-{i}";
                    sb.AppendLine($"- ({label}) {_roots[i]}");
                }
                sb.AppendLine("Never use absolute paths outside these roots. Never cd to parent folders above them.");
                sb.AppendLine("Prefer relative paths from the primary workspace. Use the provided tools for filesystem and shell work.");
                sb.AppendLine("[[END ATTACHED WORKSPACES]]");
                return sb.ToString();
            }
        }

        public static bool TryNormalizeExistingDirectory(string path, out string full)
        {
            full = string.Empty;
            if (!TryNormalizePath(path, out full))
                return false;
            return Directory.Exists(full);
        }

        public static bool TryNormalizePath(string path, out string full)
        {
            full = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                full = Path.GetFullPath(path.Trim().Trim('"').Trim('\''));
                return !string.IsNullOrWhiteSpace(full);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsUnderRoot(string fullPath, string rootPath)
        {
            try
            {
                string rootFull = Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string candidate = Path.GetFullPath(fullPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
                    return true;

                string prefix = rootFull + Path.DirectorySeparatorChar;
                return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> ExtractPathTokens(string command)
        {
            var tokens = new List<string>();
            // Quoted segments
            var quoted = System.Text.RegularExpressions.Regex.Matches(command, "\"([^\"]+)\"|'([^']+)'");
            foreach (System.Text.RegularExpressions.Match m in quoted)
            {
                string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(v))
                    tokens.Add(v);
            }

            // Unquoted whitespace tokens
            foreach (string part in command.Split(new[] { ' ', '\t', '\r', '\n', ';', '|', '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.Length >= 2 && (p.Contains('\\') || p.Contains('/') || p.Contains(':') || p.Contains("..")))
                    tokens.Add(p);
            }

            return tokens;
        }
    }
}
