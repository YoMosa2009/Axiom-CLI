using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Core.Chat;

namespace Axiom.Core.Memory
{
    public sealed class StoredUiMessage
    {
        public string Role { get; set; } = "system";
        public string Text { get; set; } = "";
    }

    public sealed class StoredSession
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "Untitled";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string ModelId { get; set; } = "";
        public string ModelLabel { get; set; } = "";
        public List<string> WorkspaceRoots { get; set; } = new();
        public bool WorkspaceExclusive { get; set; }
        public List<StoredUiMessage> Messages { get; set; } = new();
        public List<OpenRouterMessageDto> History { get; set; } = new();
    }

    public sealed class OpenRouterMessageDto
    {
        public string Role { get; set; } = "user";
        public string Text { get; set; } = "";
    }

    public sealed class SessionListItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public string ModelLabel { get; set; } = "";
        public int MessageCount { get; set; }
    }

    // Auto-persists chat sessions under %LOCALAPPDATA%/axiom-cli/Sessions (JSON files).
    public sealed class SessionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _dir;

        public SessionStore(string? directory = null)
        {
            _dir = directory ?? Path.Combine(AppPaths.Root, "Sessions");
            Directory.CreateDirectory(_dir);
        }

        public string DirectoryPath => _dir;

        public IReadOnlyList<SessionListItem> List(int max = 40)
        {
            try
            {
                return Directory.EnumerateFiles(_dir, "*.json")
                    .Select(path =>
                    {
                        try
                        {
                            StoredSession? s = JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(path), JsonOptions);
                            if (s == null || string.IsNullOrWhiteSpace(s.Id))
                                return null;
                            return new SessionListItem
                            {
                                Id = s.Id,
                                Title = string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title,
                                UpdatedAt = s.UpdatedAt,
                                ModelLabel = s.ModelLabel,
                                MessageCount = s.Messages?.Count ?? 0
                            };
                        }
                        catch { return null; }
                    })
                    .Where(x => x != null)
                    .Cast<SessionListItem>()
                    .OrderByDescending(x => x.UpdatedAt)
                    .Take(max)
                    .ToList();
            }
            catch
            {
                return Array.Empty<SessionListItem>();
            }
        }

        public StoredSession? Load(string idOrPrefix)
        {
            if (string.IsNullOrWhiteSpace(idOrPrefix))
                return null;

            string id = idOrPrefix.Trim();
            string exact = Path.Combine(_dir, id + ".json");
            if (File.Exists(exact))
                return ReadFile(exact);

            // Prefix / numeric index from list
            if (int.TryParse(id, out int index) && index >= 1)
            {
                var list = List();
                if (index <= list.Count)
                    return Load(list[index - 1].Id);
            }

            foreach (string file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.StartsWith(id, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(id, StringComparison.OrdinalIgnoreCase))
                    return ReadFile(file);
            }

            return null;
        }

        public void Save(StoredSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id))
                throw new ArgumentException("Session id required.", nameof(session));

            Directory.CreateDirectory(_dir);
            session.UpdatedAt = DateTime.UtcNow;
            if (session.CreatedAt == default)
                session.CreatedAt = session.UpdatedAt;

            string path = Path.Combine(_dir, session.Id + ".json");
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(session, JsonOptions);
            File.WriteAllText(tmp, json);
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }

        public bool Delete(string idOrPrefix)
        {
            StoredSession? s = Load(idOrPrefix);
            if (s == null)
                return false;
            string path = Path.Combine(_dir, s.Id + ".json");
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }

        public static string NewId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

        public static string MakeTitle(string? firstUserMessage)
        {
            string t = (firstUserMessage ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (t.Length == 0)
                return "Untitled session";
            if (t.Length > 60)
                t = t[..57] + "...";
            return t;
        }

        private static StoredSession? ReadFile(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(path), JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }
}
