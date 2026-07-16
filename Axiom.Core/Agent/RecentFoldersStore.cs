using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Axiom.Core.Agent
{
    // Persistent MRU list of directories the user attached via @ or the agent workspace.
    public sealed class RecentFoldersStore
    {
        private const int MaxEntries = 12;
        private readonly string _path;
        private readonly List<string> _folders = new();
        private readonly object _gate = new();

        public RecentFoldersStore(string? path = null)
        {
            _path = path ?? Path.Combine(AppPaths.Root, "recent_folders.json");
            Load();
        }

        public IReadOnlyList<string> GetRecent()
        {
            lock (_gate)
            {
                return _folders
                    .Where(Directory.Exists)
                    .ToList();
            }
        }

        public void Remember(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            string full;
            try { full = Path.GetFullPath(folderPath.Trim().Trim('"')); }
            catch { return; }

            if (!Directory.Exists(full))
                return;

            lock (_gate)
            {
                _folders.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
                _folders.Insert(0, full);
                while (_folders.Count > MaxEntries)
                    _folders.RemoveAt(_folders.Count - 1);
                SaveUnlocked();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return;
                string json = File.ReadAllText(_path);
                string[]? items = JsonSerializer.Deserialize<string[]>(json);
                if (items == null)
                    return;
                lock (_gate)
                {
                    _folders.Clear();
                    foreach (string item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item) && Directory.Exists(item))
                            _folders.Add(Path.GetFullPath(item));
                    }
                }
            }
            catch
            {
                // Corrupt cache is non-fatal.
            }
        }

        private void SaveUnlocked()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_folders));
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
