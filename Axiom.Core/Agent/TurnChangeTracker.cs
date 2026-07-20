using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    public sealed class TurnFileChange
    {
        public string Path { get; set; } = "";
        public string? Before { get; set; }
        public string? After { get; set; }
        public bool Created { get; set; }
        public bool Accepted { get; set; } = true;
    }

    /// <summary>
    /// Tracks per-file before/after for the last agent turn to support partial accept.
    /// </summary>
    public sealed class TurnChangeTracker
    {
        private readonly object _gate = new();
        private readonly List<TurnFileChange> _files = new();

        public void BeginTurn()
        {
            lock (_gate) _files.Clear();
        }

        public void NoteBeforeWrite(string path, string? beforeContent, bool existed)
        {
            lock (_gate)
            {
                if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                    return;
                _files.Add(new TurnFileChange
                {
                    Path = path,
                    Before = beforeContent,
                    Created = !existed,
                    Accepted = true
                });
            }
        }

        public void NoteAfterWrite(string path, string afterContent)
        {
            lock (_gate)
            {
                var f = _files.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                if (f != null)
                    f.After = afterContent;
            }
        }

        public IReadOnlyList<TurnFileChange> Files
        {
            get { lock (_gate) return _files.Select(Clone).ToList(); }
        }

        public string Summarize()
        {
            lock (_gate)
            {
                if (_files.Count == 0)
                    return "No file changes in the last turn.";
                var sb = new StringBuilder();
                sb.AppendLine($"Last turn changed {_files.Count} file(s):");
                int i = 1;
                foreach (var f in _files)
                {
                    string flag = f.Created ? "create" : "edit";
                    string acc = f.Accepted ? "kept" : "rejected";
                    sb.AppendLine($"  {i++}. [{flag}/{acc}] {f.Path}");
                }
                sb.AppendLine("Partial accept: /accept 1,3  ·  /reject 2  ·  /accept all  ·  /reject all");
                return sb.ToString().TrimEnd();
            }
        }

        public string Accept(IEnumerable<int> indices1Based)
        {
            lock (_gate)
            {
                var set = new HashSet<int>(indices1Based);
                for (int i = 0; i < _files.Count; i++)
                {
                    if (set.Contains(i + 1))
                        _files[i].Accepted = true;
                }
                return Summarize();
            }
        }

        public string AcceptAll()
        {
            lock (_gate)
            {
                foreach (var f in _files)
                    f.Accepted = true;
                return "All last-turn file changes kept.";
            }
        }

        public string Reject(IEnumerable<int> indices1Based)
        {
            var results = new StringBuilder();
            lock (_gate)
            {
                var set = new HashSet<int>(indices1Based);
                for (int i = 0; i < _files.Count; i++)
                {
                    if (!set.Contains(i + 1))
                        continue;
                    var f = _files[i];
                    try
                    {
                        if (f.Created)
                        {
                            if (File.Exists(f.Path))
                                File.Delete(f.Path);
                        }
                        else if (f.Before != null)
                        {
                            File.WriteAllText(f.Path, f.Before);
                        }
                        f.Accepted = false;
                        results.AppendLine($"Rejected {f.Path}");
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"Failed {f.Path}: {ex.Message}");
                    }
                }
            }
            if (results.Length == 0)
                return "No matching file indices. Use /changes to list.";
            return results.ToString().TrimEnd();
        }

        public string RejectAll()
        {
            List<int> all;
            lock (_gate)
                all = Enumerable.Range(1, _files.Count).ToList();
            return Reject(all);
        }

        private static TurnFileChange Clone(TurnFileChange f) => new()
        {
            Path = f.Path,
            Before = f.Before,
            After = f.After,
            Created = f.Created,
            Accepted = f.Accepted
        };
    }
}
