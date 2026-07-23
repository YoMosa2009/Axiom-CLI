using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Axiom.Core.Persistence
{
    // Ported from the WPF app's DatabaseService, swapping System.Data.SQLite (poor Linux/macOS
    // native support) for Microsoft.Data.Sqlite, and Windows DPAPI for the cross-platform
    // ISecretStore seam (see ISecretStore.cs).
    public class DatabaseService : IDisposable
    {
        private const string OpenRouterApiKeySettingKey = "openrouter_api_key";
        private const string CustomEndpointApiKeySettingKey = "custom_endpoint_api_key";
        public const string CustomEndpointBaseUrlSettingKey = "custom_endpoint_base_url";
        public const string CustomEndpointModelIdSettingKey = "custom_endpoint_model_id";
        public const string CustomEndpointContextWindowSettingKey = "custom_endpoint_context_window_tokens";
        private readonly SqliteConnection _connection;
        private readonly ISecretStore _secretStore;
        private readonly PersistentSettingsStore _settingsBackup;
        private readonly object _gate = new();
        private bool _isInitialized;
        private bool _disposed;

        public bool IsReady => _isInitialized && !_disposed;

        public DatabaseService(ISecretStore? secretStore = null, string? databasePath = null)
        {
            _secretStore = secretStore ?? SecretStoreFactory.Create();
            _settingsBackup = new PersistentSettingsStore();
            try
            {
                string connectionString = $"Data Source={databasePath ?? AppPaths.DatabaseFile}";
                _connection = new SqliteConnection(connectionString);
                _connection.Open();
                InitializeDatabase();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DatabaseService init error: {ex.Message}");
                _isInitialized = false;
            }
        }

        public void DeleteChat(int chatId)
        {
            if (!IsReady || chatId <= 0) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "DELETE FROM Chats WHERE Id = @id";
                    command.Parameters.AddWithValue("@id", chatId);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteChat error: {ex.Message}");
            }
        }

        private void InitializeDatabase()
        {
            lock (_gate)
            {
                using var pragma = _connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();

                using var command = _connection.CreateCommand();

                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Chats (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ChatName TEXT NOT NULL,
                        Content TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                command.ExecuteNonQuery();

                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS UserFacts (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        FactKey TEXT UNIQUE NOT NULL,
                        FactValue TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                command.ExecuteNonQuery();

                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Settings (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SettingKey TEXT UNIQUE NOT NULL,
                        SettingValue TEXT,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                command.ExecuteNonQuery();
            }
        }

        public void SaveChat(int chatId, string chatName, string content)
        {
            if (!IsReady) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();

                    if (chatId == 0)
                    {
                        command.CommandText = @"
                            INSERT INTO Chats (ChatName, Content, CreatedAt, UpdatedAt)
                            VALUES (@chatName, @content, @now, @now)";
                    }
                    else
                    {
                        command.CommandText = @"
                            UPDATE Chats SET Content = @content, UpdatedAt = @now
                            WHERE Id = @id";
                        command.Parameters.AddWithValue("@id", chatId);
                    }

                    command.Parameters.AddWithValue("@chatName", chatName);
                    command.Parameters.AddWithValue("@content", content ?? "");
                    command.Parameters.AddWithValue("@now", DateTime.Now);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveChat error: {ex.Message}");
            }
        }

        public List<(int Id, string ChatName)> GetAllChats()
        {
            var chats = new List<(int, string)>();
            if (!IsReady) return chats;

            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT Id, ChatName FROM Chats ORDER BY CreatedAt DESC";

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        chats.Add((reader.GetInt32(0), reader.GetString(1)));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetAllChats error: {ex.Message}");
            }

            return chats;
        }

        public string GetChatContent(int chatId)
        {
            if (!IsReady) return "";
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT Content FROM Chats WHERE Id = @id";
                    command.Parameters.AddWithValue("@id", chatId);
                    var result = command.ExecuteScalar();
                    return result?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetChatContent error: {ex.Message}");
                return "";
            }
        }

        public void SaveUserFact(string key, string value)
        {
            if (!IsReady) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = @"
                        INSERT OR REPLACE INTO UserFacts (FactKey, FactValue, CreatedAt, UpdatedAt)
                        VALUES (@key, @value, COALESCE((SELECT CreatedAt FROM UserFacts WHERE FactKey = @key), @now), @now)";

                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@value", value);
                    command.Parameters.AddWithValue("@now", DateTime.Now);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveUserFact error: {ex.Message}");
            }
        }

        public string GetUserFact(string key)
        {
            if (!IsReady) return "";
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT FactValue FROM UserFacts WHERE FactKey = @key";
                    command.Parameters.AddWithValue("@key", key);
                    var result = command.ExecuteScalar();
                    return result?.ToString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetUserFact error: {ex.Message}");
                return "";
            }
        }

        public void SaveSetting(string key, string value)
        {
            // Persist before SQLite: a transient lock must not erase the user's next launch.
            try { _settingsBackup.Save(key, value ?? string.Empty); } catch { /* best effort */ }
            if (!IsReady) return;
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = @"
                        INSERT OR REPLACE INTO Settings (SettingKey, SettingValue, UpdatedAt)
                        VALUES (@key, @value, @now)";

                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@value", value);
                    command.Parameters.AddWithValue("@now", DateTime.Now);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveSetting error: {ex.Message}");
            }
        }

        public string GetSetting(string key)
        {
            if (!IsReady)
                return _settingsBackup.Get(key);
            try
            {
                lock (_gate)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = "SELECT SettingValue FROM Settings WHERE SettingKey = @key";
                    command.Parameters.AddWithValue("@key", key);
                    var result = command.ExecuteScalar();
                    string value = result?.ToString() ?? "";
                    return string.IsNullOrWhiteSpace(value) ? _settingsBackup.Get(key) : value;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetSetting error: {ex.Message}");
                return _settingsBackup.Get(key);
            }
        }

        public void SaveOpenRouterApiKey(string apiKey)
        {
            try
            {
                string normalized = (apiKey ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    SaveSetting(OpenRouterApiKeySettingKey, string.Empty);
                    return;
                }

                SaveSetting(OpenRouterApiKeySettingKey, _secretStore.Protect(normalized));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveOpenRouterApiKey error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("DatabaseService.SaveOpenRouterApiKey", ex);
            }
        }

        public string? LoadOpenRouterApiKey()
        {
            try
            {
                string stored = GetSetting(OpenRouterApiKeySettingKey);
                if (string.IsNullOrWhiteSpace(stored))
                    return null;

                string decryptedKey = _secretStore.Unprotect(stored).Trim();
                return string.IsNullOrWhiteSpace(decryptedKey) ? null : decryptedKey;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadOpenRouterApiKey error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("DatabaseService.LoadOpenRouterApiKey", ex);
                return TryLoadBackupSecret(OpenRouterApiKeySettingKey);
            }
        }

        public void SaveCustomEndpointApiKey(string apiKey)
        {
            try
            {
                string normalized = (apiKey ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    SaveSetting(CustomEndpointApiKeySettingKey, string.Empty);
                    return;
                }

                SaveSetting(CustomEndpointApiKeySettingKey, _secretStore.Protect(normalized));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveCustomEndpointApiKey error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("DatabaseService.SaveCustomEndpointApiKey", ex);
            }
        }

        public string? LoadCustomEndpointApiKey()
        {
            try
            {
                string stored = GetSetting(CustomEndpointApiKeySettingKey);
                if (string.IsNullOrWhiteSpace(stored))
                    return null;

                string decryptedKey = _secretStore.Unprotect(stored).Trim();
                return string.IsNullOrWhiteSpace(decryptedKey) ? null : decryptedKey;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadCustomEndpointApiKey error: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("DatabaseService.LoadCustomEndpointApiKey", ex);
                return TryLoadBackupSecret(CustomEndpointApiKeySettingKey);
            }
        }

        private string? TryLoadBackupSecret(string settingKey)
        {
            try
            {
                string stored = _settingsBackup.Get(settingKey);
                if (string.IsNullOrWhiteSpace(stored))
                    return null;
                string plaintext = _secretStore.Unprotect(stored).Trim();
                return string.IsNullOrWhiteSpace(plaintext) ? null : plaintext;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DatabaseService.Dispose error: {ex.Message}");
            }
        }
    }
}
