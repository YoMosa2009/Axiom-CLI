using System;
using System.IO;
using Axiom.Core.Persistence;
using Xunit;

namespace Axiom.Core.Tests.Persistence
{
    public class DatabaseServiceTests : IDisposable
    {
        private readonly string _dataDir;

        public DatabaseServiceTests()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "axiom-cli-db-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_dataDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
        }

        private DatabaseService CreateIsolatedDatabase() => new(
            new AesFileSecretStore(Path.Combine(_dataDir, ".secret.key")),
            Path.Combine(_dataDir, "test.db"));

        [Fact]
        public void SaveAndLoadOpenRouterApiKey_RoundTrips()
        {
            using var db = CreateIsolatedDatabase();

            Assert.True(db.IsReady);
            db.SaveOpenRouterApiKey("sk-or-v1-test-key");
            Assert.Equal("sk-or-v1-test-key", db.LoadOpenRouterApiKey());
        }

        [Fact]
        public void SaveAndGetSetting_RoundTrips()
        {
            using var db = CreateIsolatedDatabase();

            db.SaveSetting("theme", "dark");
            Assert.Equal("dark", db.GetSetting("theme"));
        }
    }
}
