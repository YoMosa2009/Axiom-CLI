using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Axiom.Core.Agent
{
    // Persistent, disk-backed lexical memory for kestral only -- indexes the connected workspace
    // and accumulates conversation history across turns/sessions into a SQLite database, then
    // retrieves the most relevant chunks into the existing (small, ~9k-token) context window
    // instead of trying to make the window itself bigger (not physically possible -- see the
    // Kestral persistent memory plan). Mirrors DatabaseService's connection+lock convention.
    //
    // Staleness note (the Explore-lane-hallucination analog documented in CouncilModels.cs):
    // conversation-memory blocks are labeled distinctly from live-file retrieval and scored with
    // a recency decay, so an old, possibly-superseded claim doesn't outrank current, more relevant
    // information -- retrieval nudges the model to treat past-conversation content as historical,
    // not authoritative.
    public sealed class KestralMemoryStore : IDisposable
    {
        private const int SchemaVersion = 1;
        private const int ChunkLines = 40;
        private const int MaxFilesPerIngest = 400;
        private const long MaxFileBytes = 250_000;
        private const double EvictToFraction = 0.90;

        private readonly SqliteConnection? _connection;
        private readonly object _gate = new();
        private readonly long _byteBudget;
        private bool _disposed;

        public bool IsReady { get; }

        public KestralMemoryStore(string databasePath, long byteBudget)
        {
            _byteBudget = Math.Max(1_000_000, byteBudget);
            try
            {
                string? dir = Path.GetDirectoryName(databasePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                _connection = new SqliteConnection($"Data Source={databasePath}");
                _connection.Open();
                InitializeDatabase();
                IsReady = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KestralMemoryStore init error: {ex.Message}");
                IsReady = false;
            }
        }

        private void InitializeDatabase()
        {
            lock (_gate)
            {
                using (var pragma = _connection!.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=2000;";
                    pragma.ExecuteNonQuery();
                }

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS SchemaInfo (Version INTEGER NOT NULL);

                    CREATE TABLE IF NOT EXISTS FileIndex (
                        WorkspaceRoot TEXT NOT NULL,
                        RelPath TEXT NOT NULL,
                        FileHash TEXT NOT NULL,
                        LastWriteUtc TEXT NOT NULL,
                        LastIndexedUtc TEXT NOT NULL,
                        PRIMARY KEY (WorkspaceRoot, RelPath)
                    );

                    CREATE TABLE IF NOT EXISTS WorkspaceChunks (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkspaceRoot TEXT NOT NULL,
                        RelPath TEXT NOT NULL,
                        ChunkIndex INTEGER NOT NULL,
                        ContentHash TEXT NOT NULL,
                        Body TEXT NOT NULL,
                        ByteSize INTEGER NOT NULL,
                        LastIndexedUtc TEXT NOT NULL,
                        LastHitUtc TEXT,
                        UNIQUE(WorkspaceRoot, RelPath, ChunkIndex)
                    );
                    CREATE INDEX IF NOT EXISTS IX_WorkspaceChunks_Root ON WorkspaceChunks(WorkspaceRoot);

                    CREATE TABLE IF NOT EXISTS ConversationTurns (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        WorkspaceRoot TEXT NOT NULL,
                        UserPrompt TEXT NOT NULL,
                        BuilderOutput TEXT,
                        CriticSummary TEXT,
                        ByteSize INTEGER NOT NULL,
                        CreatedUtc TEXT NOT NULL,
                        LastHitUtc TEXT
                    );
                    CREATE INDEX IF NOT EXISTS IX_ConversationTurns_Root ON ConversationTurns(WorkspaceRoot);
                ";
                cmd.ExecuteNonQuery();

                using var versionCheck = _connection!.CreateCommand();
                versionCheck.CommandText = "SELECT COUNT(*) FROM SchemaInfo";
                long count = (long)versionCheck.ExecuteScalar()!;
                if (count == 0)
                {
                    using var insertVersion = _connection!.CreateCommand();
                    insertVersion.CommandText = "INSERT INTO SchemaInfo (Version) VALUES (@v)";
                    insertVersion.Parameters.AddWithValue("@v", SchemaVersion);
                    insertVersion.ExecuteNonQuery();
                }
            }
        }

        // Hash-gated: unchanged files (by mtime, falling back to content hash) are skipped
        // entirely -- a full re-chunk-and-rewrite only happens for files that actually changed.
        public void IngestWorkspace(string workspaceRoot, CancellationToken ct = default)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return;

            try
            {
                int processed = 0;
                foreach (string file in WorkspaceFileScan.EnumerateTextFiles(workspaceRoot))
                {
                    if (ct.IsCancellationRequested || processed >= MaxFilesPerIngest)
                        break;

                    FileInfo info;
                    try { info = new FileInfo(file); }
                    catch { continue; }
                    if (info.Length is 0 or > MaxFileBytes)
                        continue;

                    string rel = Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
                    string mtimeUtc = info.LastWriteTimeUtc.ToString("O");

                    lock (_gate)
                    {
                        using var check = _connection!.CreateCommand();
                        check.CommandText = "SELECT LastWriteUtc FROM FileIndex WHERE WorkspaceRoot=@root AND RelPath=@rel";
                        check.Parameters.AddWithValue("@root", workspaceRoot);
                        check.Parameters.AddWithValue("@rel", rel);
                        object? existing = check.ExecuteScalar();
                        if (existing is string existingMtime && existingMtime == mtimeUtc)
                            continue; // unchanged since last index -- cheapest possible skip
                    }

                    string text;
                    try { text = File.ReadAllText(file); }
                    catch { continue; }

                    string fileHash = Hash(text);
                    bool fileChanged;
                    lock (_gate)
                    {
                        using var check = _connection!.CreateCommand();
                        check.CommandText = "SELECT FileHash FROM FileIndex WHERE WorkspaceRoot=@root AND RelPath=@rel";
                        check.Parameters.AddWithValue("@root", workspaceRoot);
                        check.Parameters.AddWithValue("@rel", rel);
                        object? existingHash = check.ExecuteScalar();
                        fileChanged = existingHash is not string h || h != fileHash;
                    }

                    if (!fileChanged)
                    {
                        // Content is identical but mtime moved (touch/checkout) -- refresh mtime only.
                        UpsertFileIndexTimestamp(workspaceRoot, rel, fileHash, mtimeUtc);
                        continue;
                    }

                    var chunks = LexicalScorer.ChunkLines(text, ChunkLines);
                    UpsertChunks(workspaceRoot, rel, chunks);
                    UpsertFileIndexTimestamp(workspaceRoot, rel, fileHash, mtimeUtc);
                    processed++;
                }

                RemoveDeletedFiles(workspaceRoot);
                EnforceByteBudget(ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KestralMemoryStore.IngestWorkspace error: {ex.Message}");
            }
        }

        private void UpsertChunks(string workspaceRoot, string relPath, List<string> chunks)
        {
            lock (_gate)
            {
                using var tx = _connection!.BeginTransaction();
                try
                {
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        string body = chunks[i];
                        string hash = Hash(body);
                        using var upsert = _connection.CreateCommand();
                        upsert.Transaction = tx;
                        upsert.CommandText = @"
                            INSERT INTO WorkspaceChunks (WorkspaceRoot, RelPath, ChunkIndex, ContentHash, Body, ByteSize, LastIndexedUtc)
                            VALUES (@root, @rel, @idx, @hash, @body, @size, @now)
                            ON CONFLICT(WorkspaceRoot, RelPath, ChunkIndex) DO UPDATE SET
                                ContentHash = excluded.ContentHash,
                                Body = excluded.Body,
                                ByteSize = excluded.ByteSize,
                                LastIndexedUtc = excluded.LastIndexedUtc
                            WHERE WorkspaceChunks.ContentHash != excluded.ContentHash";
                        upsert.Parameters.AddWithValue("@root", workspaceRoot);
                        upsert.Parameters.AddWithValue("@rel", relPath);
                        upsert.Parameters.AddWithValue("@idx", i);
                        upsert.Parameters.AddWithValue("@hash", hash);
                        upsert.Parameters.AddWithValue("@body", body);
                        upsert.Parameters.AddWithValue("@size", Encoding.UTF8.GetByteCount(body));
                        upsert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                        upsert.ExecuteNonQuery();
                    }

                    // Drop chunks beyond the file's current chunk count (file shrank).
                    using var trim = _connection.CreateCommand();
                    trim.Transaction = tx;
                    trim.CommandText = "DELETE FROM WorkspaceChunks WHERE WorkspaceRoot=@root AND RelPath=@rel AND ChunkIndex >= @count";
                    trim.Parameters.AddWithValue("@root", workspaceRoot);
                    trim.Parameters.AddWithValue("@rel", relPath);
                    trim.Parameters.AddWithValue("@count", chunks.Count);
                    trim.ExecuteNonQuery();

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private void UpsertFileIndexTimestamp(string workspaceRoot, string relPath, string fileHash, string mtimeUtc)
        {
            lock (_gate)
            {
                using var upsert = _connection!.CreateCommand();
                upsert.CommandText = @"
                    INSERT INTO FileIndex (WorkspaceRoot, RelPath, FileHash, LastWriteUtc, LastIndexedUtc)
                    VALUES (@root, @rel, @hash, @mtime, @now)
                    ON CONFLICT(WorkspaceRoot, RelPath) DO UPDATE SET
                        FileHash = excluded.FileHash, LastWriteUtc = excluded.LastWriteUtc, LastIndexedUtc = excluded.LastIndexedUtc";
                upsert.Parameters.AddWithValue("@root", workspaceRoot);
                upsert.Parameters.AddWithValue("@rel", relPath);
                upsert.Parameters.AddWithValue("@hash", fileHash);
                upsert.Parameters.AddWithValue("@mtime", mtimeUtc);
                upsert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                upsert.ExecuteNonQuery();
            }
        }

        private void RemoveDeletedFiles(string workspaceRoot)
        {
            var known = new List<string>();
            lock (_gate)
            {
                using var select = _connection!.CreateCommand();
                select.CommandText = "SELECT RelPath FROM FileIndex WHERE WorkspaceRoot=@root";
                select.Parameters.AddWithValue("@root", workspaceRoot);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    known.Add(reader.GetString(0));
            }

            foreach (string rel in known)
            {
                string full = Path.Combine(workspaceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    continue;

                lock (_gate)
                {
                    using var del1 = _connection!.CreateCommand();
                    del1.CommandText = "DELETE FROM FileIndex WHERE WorkspaceRoot=@root AND RelPath=@rel";
                    del1.Parameters.AddWithValue("@root", workspaceRoot);
                    del1.Parameters.AddWithValue("@rel", rel);
                    del1.ExecuteNonQuery();

                    using var del2 = _connection!.CreateCommand();
                    del2.CommandText = "DELETE FROM WorkspaceChunks WHERE WorkspaceRoot=@root AND RelPath=@rel";
                    del2.Parameters.AddWithValue("@root", workspaceRoot);
                    del2.Parameters.AddWithValue("@rel", rel);
                    del2.ExecuteNonQuery();
                }
            }
        }

        public void RecordTurn(string workspaceRoot, string userPrompt, string? builderOutput, string? criticSummary)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(userPrompt))
                return;

            try
            {
                string builder = builderOutput ?? string.Empty;
                string critic = criticSummary ?? string.Empty;
                int size = Encoding.UTF8.GetByteCount(userPrompt) + Encoding.UTF8.GetByteCount(builder) + Encoding.UTF8.GetByteCount(critic);

                lock (_gate)
                {
                    using var insert = _connection!.CreateCommand();
                    insert.CommandText = @"
                        INSERT INTO ConversationTurns (WorkspaceRoot, UserPrompt, BuilderOutput, CriticSummary, ByteSize, CreatedUtc)
                        VALUES (@root, @prompt, @builder, @critic, @size, @now)";
                    insert.Parameters.AddWithValue("@root", workspaceRoot);
                    insert.Parameters.AddWithValue("@prompt", userPrompt);
                    insert.Parameters.AddWithValue("@builder", builder);
                    insert.Parameters.AddWithValue("@critic", critic);
                    insert.Parameters.AddWithValue("@size", size);
                    insert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                    insert.ExecuteNonQuery();
                }

                EnforceByteBudget();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KestralMemoryStore.RecordTurn error: {ex.Message}");
            }
        }

        private const string ConversationLabel =
            "[[PAST CONVERSATION — historical, verify against current files before relying on this]]";

        // Same output shape as RepoRetrievalService.Retrieve (a labeled text block) so call sites
        // fold it into workspaceContext/effectiveUser identically.
        public string Retrieve(string workspaceRoot, string query, int maxChunks = 5, int maxChars = 4000)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(query))
                return string.Empty;

            var keywords = LexicalScorer.ExtractKeywords(query);
            if (keywords.Count == 0)
                return string.Empty;

            maxChunks = Math.Clamp(maxChunks, 1, 12);
            maxChars = Math.Clamp(maxChars, 800, 16_000);

            var candidates = new List<(double Score, string Label, string Text, string SourceKind, long? RowId)>();

            try
            {
                lock (_gate)
                {
                    using var chunkSelect = _connection!.CreateCommand();
                    chunkSelect.CommandText = "SELECT Id, RelPath, ChunkIndex, Body FROM WorkspaceChunks WHERE WorkspaceRoot=@root";
                    chunkSelect.Parameters.AddWithValue("@root", workspaceRoot);
                    using (var reader = chunkSelect.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long id = reader.GetInt64(0);
                            string rel = reader.GetString(1);
                            int idx = reader.GetInt32(2);
                            string body = reader.GetString(3);
                            double score = LexicalScorer.Score(body, rel, keywords, query);
                            if (score >= 2)
                                candidates.Add((score, $"{rel}#chunk{idx}", body, "chunk", id));
                        }
                    }

                    using var turnSelect = _connection!.CreateCommand();
                    turnSelect.CommandText = "SELECT Id, UserPrompt, BuilderOutput, CreatedUtc FROM ConversationTurns WHERE WorkspaceRoot=@root ORDER BY CreatedUtc DESC LIMIT 200";
                    turnSelect.Parameters.AddWithValue("@root", workspaceRoot);
                    using (var reader = turnSelect.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long id = reader.GetInt64(0);
                            string prompt = reader.GetString(1);
                            string builder = reader.IsDBNull(2) ? "" : reader.GetString(2);
                            DateTime created = DateTime.Parse(reader.GetString(3)).ToUniversalTime();
                            string combined = $"User: {prompt}\nOutcome: {builder}";
                            double score = LexicalScorer.Score(combined, "", keywords, query);
                            if (score < 2)
                                continue;
                            double ageDays = Math.Max(0, (DateTime.UtcNow - created).TotalDays);
                            double recencyFactor = 1.0 / (1.0 + ageDays / 30.0);
                            candidates.Add((score * recencyFactor, $"turn@{created:yyyy-MM-dd}", combined, "turn", id));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KestralMemoryStore.Retrieve query error: {ex.Message}");
                return string.Empty;
            }

            if (candidates.Count == 0)
                return string.Empty;

            var top = candidates.OrderByDescending(c => c.Score).Take(maxChunks).ToList();
            TouchHits(top);

            var chunkHits = top.Where(t => t.SourceKind == "chunk").ToList();
            var turnHits = top.Where(t => t.SourceKind == "turn").ToList();

            var sb = new StringBuilder();
            int used = 0;
            if (chunkHits.Count > 0)
            {
                sb.AppendLine("[[KESTRAL MEMORY — indexed workspace]]");
                used = sb.Length;
                foreach (var hit in chunkHits)
                {
                    string snippet = hit.Text.Length > 700 ? hit.Text[..700] + "…" : hit.Text;
                    string block = $"--- {hit.Label} (score {hit.Score:0.0}) ---\n{snippet}\n";
                    if (used + block.Length > maxChars)
                        break;
                    sb.Append(block);
                    used += block.Length;
                }
                sb.AppendLine("[[END KESTRAL MEMORY]]");
            }

            if (turnHits.Count > 0 && used < maxChars)
            {
                sb.AppendLine(ConversationLabel);
                foreach (var hit in turnHits)
                {
                    string snippet = hit.Text.Length > 500 ? hit.Text[..500] + "…" : hit.Text;
                    string block = $"--- {hit.Label} ---\n{snippet}\n";
                    if (used + block.Length > maxChars)
                        break;
                    sb.Append(block);
                    used += block.Length;
                }
                sb.AppendLine("[[END PAST CONVERSATION]]");
            }

            return sb.ToString().TrimEnd();
        }

        private void TouchHits(List<(double Score, string Label, string Text, string SourceKind, long? RowId)> hits)
        {
            string now = DateTime.UtcNow.ToString("O");
            try
            {
                lock (_gate)
                {
                    foreach (var hit in hits)
                    {
                        if (hit.RowId is not long id)
                            continue;
                        string table = hit.SourceKind == "chunk" ? "WorkspaceChunks" : "ConversationTurns";
                        using var update = _connection!.CreateCommand();
                        update.CommandText = $"UPDATE {table} SET LastHitUtc=@now WHERE Id=@id";
                        update.Parameters.AddWithValue("@now", now);
                        update.Parameters.AddWithValue("@id", id);
                        update.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KestralMemoryStore.TouchHits error: {ex.Message}");
            }
        }

        // LRU eviction: once total stored bytes exceed the budget, delete lowest-LastHitUtc rows
        // (falling back to LastIndexedUtc/CreatedUtc for rows never retrieved) until back to
        // EvictToFraction of budget -- not run on every retrieval, only after ingestion/turn writes.
        public void EnforceByteBudget(CancellationToken ct = default)
        {
            if (!IsReady)
                return;

            try
            {
                long total = GetTotalBytes();
                if (total <= _byteBudget)
                    return;

                long target = (long)(_byteBudget * EvictToFraction);

                while (total > target && !ct.IsCancellationRequested)
                {
                    (string Table, long Id, long Size)? victim = FindEvictionVictim();
                    if (victim == null)
                        break;

                    lock (_gate)
                    {
                        using var del = _connection!.CreateCommand();
                        del.CommandText = $"DELETE FROM {victim.Value.Table} WHERE Id=@id";
                        del.Parameters.AddWithValue("@id", victim.Value.Id);
                        del.ExecuteNonQuery();
                    }
                    total -= victim.Value.Size;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KestralMemoryStore.EnforceByteBudget error: {ex.Message}");
            }
        }

        private long GetTotalBytes()
        {
            lock (_gate)
            {
                using var q1 = _connection!.CreateCommand();
                q1.CommandText = "SELECT COALESCE(SUM(ByteSize),0) FROM WorkspaceChunks";
                long chunks = (long)q1.ExecuteScalar()!;

                using var q2 = _connection!.CreateCommand();
                q2.CommandText = "SELECT COALESCE(SUM(ByteSize),0) FROM ConversationTurns";
                long turns = (long)q2.ExecuteScalar()!;

                return chunks + turns;
            }
        }

        private (string Table, long Id, long Size)? FindEvictionVictim()
        {
            lock (_gate)
            {
                using var chunkQuery = _connection!.CreateCommand();
                chunkQuery.CommandText = @"
                    SELECT Id, ByteSize FROM WorkspaceChunks
                    ORDER BY COALESCE(LastHitUtc, LastIndexedUtc) ASC LIMIT 1";
                object? chunkId = null, chunkSize = null;
                using (var reader = chunkQuery.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        chunkId = reader.GetInt64(0);
                        chunkSize = reader.GetInt64(1);
                    }
                }

                using var turnQuery = _connection!.CreateCommand();
                turnQuery.CommandText = @"
                    SELECT Id, ByteSize FROM ConversationTurns
                    ORDER BY COALESCE(LastHitUtc, CreatedUtc) ASC LIMIT 1";
                object? turnId = null, turnSize = null;
                using (var reader = turnQuery.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        turnId = reader.GetInt64(0);
                        turnSize = reader.GetInt64(1);
                    }
                }

                if (chunkId == null && turnId == null)
                    return null;
                if (turnId == null)
                    return ("WorkspaceChunks", (long)chunkId!, (long)chunkSize!);
                if (chunkId == null)
                    return ("ConversationTurns", (long)turnId!, (long)turnSize!);

                // Evict whichever table's oldest-touched row is cheaper to drop first is not
                // meaningful here -- just alternate by picking the smaller id space; simplest
                // correct choice is to evict from WorkspaceChunks first (re-derivable by
                // re-ingesting) before ConversationTurns (not re-derivable once gone).
                return ("WorkspaceChunks", (long)chunkId!, (long)chunkSize!);
            }
        }

        private static string Hash(string text)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KestralMemoryStore.Dispose error: {ex.Message}");
            }
        }
    }
}
