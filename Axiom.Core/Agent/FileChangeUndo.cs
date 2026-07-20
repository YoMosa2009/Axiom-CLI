using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    public sealed record FileSnapshot(
        string Path,
        string? PreviousContent,
        bool ExistedBefore,
        DateTime Utc);

    public sealed record UndoTurn(
        DateTime Utc,
        string Label,
        IReadOnlyList<FileSnapshot> Files);

    /// <summary>
    /// Snapshots file contents before agent writes so the user can /undo the last turn.
    /// </summary>
    public sealed class FileChangeUndo
    {
        private readonly object _gate = new();
        private readonly List<UndoTurn> _stack = new();
        private readonly List<FileSnapshot> _currentTurn = new();
        private string _currentLabel = "turn";
        private const int MaxTurns = 20;
        private const int MaxFileBytes = 2_000_000;

        public void BeginTurn(string label)
        {
            lock (_gate)
            {
                _currentTurn.Clear();
                _currentLabel = string.IsNullOrWhiteSpace(label) ? "turn" : label.Trim();
            }
        }

        public void RecordBeforeWrite(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            lock (_gate)
            {
                if (_currentTurn.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                    return;

                bool existed = File.Exists(path);
                string? content = null;
                if (existed)
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length <= MaxFileBytes)
                            content = File.ReadAllText(path);
                        else
                            content = null; // too large — undo will delete-only if created, else skip restore
                    }
                    catch
                    {
                        content = null;
                    }
                }

                _currentTurn.Add(new FileSnapshot(path, content, existed, DateTime.UtcNow));
            }
        }

        public void CommitTurn()
        {
            lock (_gate)
            {
                if (_currentTurn.Count == 0)
                    return;

                _stack.Add(new UndoTurn(DateTime.UtcNow, _currentLabel, _currentTurn.ToList()));
                while (_stack.Count > MaxTurns)
                    _stack.RemoveAt(0);
                _currentTurn.Clear();
            }
        }

        public bool CanUndo
        {
            get { lock (_gate) return _stack.Count > 0; }
        }

        public int PendingCount
        {
            get { lock (_gate) return _stack.Count; }
        }

        public UndoTurn? PeekLast()
        {
            lock (_gate)
                return _stack.Count == 0 ? null : _stack[^1];
        }

        public IReadOnlyList<FileSnapshot> PeekLastFiles()
        {
            lock (_gate)
                return _stack.Count == 0
                    ? Array.Empty<FileSnapshot>()
                    : _stack[^1].Files;
        }

        /// <summary>Restores the most recent turn. Returns a human summary.</summary>
        public string UndoLast()
        {
            UndoTurn turn;
            lock (_gate)
            {
                if (_stack.Count == 0)
                    return "Nothing to undo.";
                turn = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
            }

            int restored = 0;
            int deleted = 0;
            int skipped = 0;
            var lines = new StringBuilder();
            lines.AppendLine($"Undo “{turn.Label}” ({turn.Utc.ToLocalTime():g}):");

            // Reverse order so nested creates unwind cleanly.
            foreach (FileSnapshot snap in turn.Files.Reverse())
            {
                try
                {
                    if (!snap.ExistedBefore)
                    {
                        if (File.Exists(snap.Path))
                        {
                            File.Delete(snap.Path);
                            deleted++;
                            lines.AppendLine($"  - deleted {snap.Path}");
                        }
                        else skipped++;
                    }
                    else if (snap.PreviousContent != null)
                    {
                        string? dir = Path.GetDirectoryName(snap.Path);
                        if (!string.IsNullOrWhiteSpace(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(snap.Path, snap.PreviousContent);
                        restored++;
                        lines.AppendLine($"  - restored {snap.Path}");
                    }
                    else
                    {
                        skipped++;
                        lines.AppendLine($"  - skipped (no snapshot) {snap.Path}");
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    lines.AppendLine($"  - failed {snap.Path}: {ex.Message}");
                }
            }

            lines.AppendLine($"Restored {restored}, deleted {deleted}, skipped {skipped}.");
            return lines.ToString().TrimEnd();
        }
    }
}
