using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Axiom.Core.Persistence
{
    // Atomic mirror of Settings. SQLite remains primary, while this preserves configuration
    // across a transient lock or recovery failure. Sensitive values arrive pre-encrypted.
    internal sealed class PersistentSettingsStore
    {
        private readonly string _path;
        private readonly object _gate = new();

        public PersistentSettingsStore(string? path = null)
        {
            _path = path ?? Path.Combine(AppPaths.Root, "settings.backup.json");
        }

        public string Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            lock (_gate)
            {
                Dictionary<string, string> values = Load();
                return values.TryGetValue(key, out string? value) ? value ?? string.Empty : string.Empty;
            }
        }

        public void Save(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            lock (_gate)
            {
                Dictionary<string, string> values = Load();
                if (string.IsNullOrWhiteSpace(value))
                    values.Remove(key);
                else
                    values[key] = value;

                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(values));
                File.Move(temporaryPath, _path, overwrite: true);
            }
        }

        private Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(_path))
                    return new Dictionary<string, string>(StringComparer.Ordinal);

                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }
}
