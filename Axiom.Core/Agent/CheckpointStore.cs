using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Axiom.Core.Agent
{
    public sealed class NamedCheckpoint
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime Utc { get; set; } = DateTime.UtcNow;
        public string WorkspaceRoot { get; set; } = "";
        public List<FileSnapshot> Files { get; set; } = new();
    }

    public sealed class CheckpointListItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime Utc { get; set; }
        public int FileCount { get; set; }
    }

    /// <summary>
    /// Named multi-file snapshots for restore beyond single-turn /undo.
    /// </summary>
    public sealed class CheckpointStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _dir;
        private const int MaxFileBytes = 2_000_000;
        private const int MaxCheckpoints = 30;

        public CheckpointStore(string? directory = null)
        {
            _dir = directory ?? Path.Combine(AppPaths.Root, "Checkpoints");
            Directory.CreateDirectory(_dir);
        }

        public IReadOnlyList<CheckpointListItem> List(int max = 20)
        {
            try
            {
                return Directory.EnumerateFiles(_dir, "*.json")
                    .Select(path =>
                    {
                        try
                        {
                            var c = JsonSerializer.Deserialize<NamedCheckpoint>(File.ReadAllText(path), JsonOptions);
                            if (c == null || string.IsNullOrWhiteSpace(c.Id))
                                return null;
                            return new CheckpointListItem
                            {
                                Id = c.Id,
                                Name = c.Name,
                                Utc = c.Utc,
                                FileCount = c.Files?.Count ?? 0
                            };
                        }
                        catch { return null; }
                    })
                    .Where(x => x != null)
                    .Cast<CheckpointListItem>()
                    .OrderByDescending(x => x.Utc)
                    .Take(max)
                    .ToList();
            }
            catch
            {
                return Array.Empty<CheckpointListItem>();
            }
        }

        public NamedCheckpoint? Load(string idOrPrefix)
        {
            if (string.IsNullOrWhiteSpace(idOrPrefix))
                return null;
            string key = idOrPrefix.Trim();
            string exact = Path.Combine(_dir, key + ".json");
            if (File.Exists(exact))
                return Read(exact);

            if (int.TryParse(key, out int n) && n >= 1)
            {
                var list = List();
                if (n <= list.Count)
                    return Load(list[n - 1].Id);
            }

            foreach (string file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Read(file);
            }

            // Match by checkpoint display name
            foreach (var item in List(50))
            {
                if (item.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Load(item.Id);
            }

            return null;
        }

        public string CreateFromPaths(string name, string workspaceRoot, IEnumerable<string> absolutePaths)
        {
            var files = new List<FileSnapshot>();
            foreach (string path in absolutePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                bool existed = File.Exists(path);
                string? content = null;
                if (existed)
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length <= MaxFileBytes)
                            content = File.ReadAllText(path);
                    }
                    catch { content = null; }
                }
                files.Add(new FileSnapshot(path, content, existed, DateTime.UtcNow));
            }

            // If no paths given, snapshot dirty git files when possible
            if (files.Count == 0 && !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
            {
                foreach (string rel in TryListChangedFiles(workspaceRoot).Take(80))
                {
                    string full = Path.GetFullPath(Path.Combine(workspaceRoot, rel));
                    if (!File.Exists(full))
                        continue;
                    try
                    {
                        var info = new FileInfo(full);
                        string? content = info.Length <= MaxFileBytes ? File.ReadAllText(full) : null;
                        files.Add(new FileSnapshot(full, content, true, DateTime.UtcNow));
                    }
                    catch { /* skip */ }
                }
            }

            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            string display = string.IsNullOrWhiteSpace(name) ? "checkpoint" : name.Trim();
            if (display.Length > 60)
                display = display[..57] + "...";

            var cp = new NamedCheckpoint
            {
                Id = id,
                Name = display,
                Utc = DateTime.UtcNow,
                WorkspaceRoot = workspaceRoot ?? "",
                Files = files
            };

            string pathOut = Path.Combine(_dir, id + ".json");
            File.WriteAllText(pathOut, JsonSerializer.Serialize(cp, JsonOptions));
            PruneOld();
            return id;
        }

        public string CreateFromUndoTurn(string name, string workspaceRoot, UndoTurn turn)
            => CreateFromSnapshots(name, workspaceRoot, turn.Files);

        public string CreateFromSnapshots(string name, string workspaceRoot, IReadOnlyList<FileSnapshot> snapshots)
        {
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            string display = string.IsNullOrWhiteSpace(name) ? "checkpoint" : name.Trim();
            if (display.Length > 60)
                display = display[..57] + "...";

            var cp = new NamedCheckpoint
            {
                Id = id,
                Name = display,
                Utc = DateTime.UtcNow,
                WorkspaceRoot = workspaceRoot ?? "",
                Files = snapshots.ToList()
            };
            File.WriteAllText(Path.Combine(_dir, id + ".json"), JsonSerializer.Serialize(cp, JsonOptions));
            PruneOld();
            return id;
        }

        public string Restore(string idOrPrefix)
        {
            NamedCheckpoint? cp = Load(idOrPrefix);
            if (cp == null)
                return "Checkpoint not found.";

            int restored = 0, deleted = 0, skipped = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"Restore “{cp.Name}” ({cp.Utc.ToLocalTime():g}):");

            foreach (FileSnapshot snap in cp.Files.AsEnumerable().Reverse())
            {
                try
                {
                    if (!snap.ExistedBefore)
                    {
                        if (File.Exists(snap.Path))
                        {
                            File.Delete(snap.Path);
                            deleted++;
                            sb.AppendLine($"  - deleted {snap.Path}");
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
                        sb.AppendLine($"  - restored {snap.Path}");
                    }
                    else
                    {
                        skipped++;
                        sb.AppendLine($"  - skipped {snap.Path}");
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    sb.AppendLine($"  - failed {snap.Path}: {ex.Message}");
                }
            }

            sb.AppendLine($"Restored {restored}, deleted {deleted}, skipped {skipped}.");
            return sb.ToString().TrimEnd();
        }

        private void PruneOld()
        {
            try
            {
                var files = Directory.EnumerateFiles(_dir, "*.json")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(MaxCheckpoints)
                    .ToList();
                foreach (var f in files)
                {
                    try { f.Delete(); } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }

        private static IEnumerable<string> TryListChangedFiles(string root)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("status");
                psi.ArgumentList.Add("--porcelain");
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null)
                    yield break;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (line.Length < 4)
                        continue;
                    string path = line[3..].Trim().Trim('"');
                    if (path.Contains(" -> ", StringComparison.Ordinal))
                        path = path.Split(" -> ", 2)[^1].Trim();
                    if (path.Length > 0)
                        yield return path.Replace('\\', '/');
                }
            }
            finally { }
        }

        private static NamedCheckpoint? Read(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<NamedCheckpoint>(File.ReadAllText(path), JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }
}
