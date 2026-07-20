using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Watches the workspace for external file changes and surfaces them to the TUI.
    /// </summary>
    public sealed class WorkspaceWatchService : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentQueue<string> _events = new();
        private readonly HashSet<string> _recent = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastClear = DateTime.UtcNow;
        private string _root = "";
        private bool _enabled;

        public bool IsEnabled => _enabled;

        public void Start(string root)
        {
            Stop();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            _root = Path.GetFullPath(root);
            try
            {
                _watcher = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024
                };
                _watcher.Changed += OnEvent;
                _watcher.Created += OnEvent;
                _watcher.Deleted += OnEvent;
                _watcher.Renamed += OnRename;
                _watcher.EnableRaisingEvents = true;
                _enabled = true;
            }
            catch
            {
                _enabled = false;
            }
        }

        public void Stop()
        {
            _enabled = false;
            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                }
                catch { /* ignore */ }
                _watcher = null;
            }
            while (_events.TryDequeue(out _)) { }
            lock (_recent) _recent.Clear();
        }

        public IReadOnlyList<string> Drain(int max = 12)
        {
            if ((DateTime.UtcNow - _lastClear).TotalSeconds > 30)
            {
                lock (_recent) _recent.Clear();
                _lastClear = DateTime.UtcNow;
            }

            var list = new List<string>();
            while (list.Count < max && _events.TryDequeue(out string? e) && e != null)
                list.Add(e);
            return list;
        }

        private void OnRename(object sender, RenamedEventArgs e) => Enqueue(e.FullPath, "renamed");
        private void OnEvent(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath, e.ChangeType.ToString().ToLowerInvariant());

        private void Enqueue(string fullPath, string kind)
        {
            try
            {
                string name = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                    return;
                string ext = Path.GetExtension(name);
                if (ext is ".dll" or ".exe" or ".pdb" or ".tmp" or ".log")
                    return;
                // skip noise dirs
                string rel = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/node_modules/")
                    || rel.Contains("/.git/"))
                    return;

                lock (_recent)
                {
                    string key = rel + "|" + kind;
                    if (!_recent.Add(key))
                        return;
                    if (_recent.Count > 200)
                        _recent.Clear();
                }

                _events.Enqueue($"{kind}: {rel}");
            }
            catch { /* ignore */ }
        }

        public void Dispose() => Stop();
    }
}
