using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    // Directories the user has attached for this chat session. The agent may read/write/run
    // commands inside these roots (and their descendants) only.
    public sealed class WorkspaceSession
    {
        private readonly List<string> _roots = new();
        private readonly RecentFoldersStore _recent;
        private readonly object _gate = new();

        public WorkspaceSession(RecentFoldersStore? recent = null)
        {
            _recent = recent ?? new RecentFoldersStore();
            // Default: current working directory is always available as a soft workspace.
            TryAttach(Environment.CurrentDirectory, remember: false);
        }

        public IReadOnlyList<string> Roots
        {
            get { lock (_gate) return _roots.ToList(); }
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

            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); }
            catch { return false; }

            if (!Directory.Exists(full))
                return false;

            lock (_gate)
            {
                _roots.RemoveAll(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));
                _roots.Insert(0, full);
            }

            if (remember)
                _recent.Remember(full);

            return true;
        }

        public bool IsPathAllowed(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return false; }

            lock (_gate)
            {
                foreach (string root in _roots)
                {
                    string rootFull = Path.GetFullPath(root)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    string candidate = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase))
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
                return Path.GetFullPath(path);

            return Path.GetFullPath(Path.Combine(PrimaryRoot, path));
        }

        public string BuildContextBlock()
        {
            lock (_gate)
            {
                if (_roots.Count == 0)
                    return string.Empty;

                var sb = new StringBuilder();
                sb.AppendLine("[[ATTACHED WORKSPACES]]");
                sb.AppendLine("You may read, write, list, run shell commands, build, and download inside these directories:");
                for (int i = 0; i < _roots.Count; i++)
                {
                    string label = i == 0 ? "primary" : $"extra-{i}";
                    sb.AppendLine($"- ({label}) {_roots[i]}");
                }
                sb.AppendLine("Prefer relative paths from the primary workspace. Use the provided tools for filesystem and shell work.");
                sb.AppendLine("[[END ATTACHED WORKSPACES]]");
                return sb.ToString();
            }
        }
    }
}
