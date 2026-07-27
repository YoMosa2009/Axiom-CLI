using System;
using System.IO;
using System.Text.Json;
using Axiom.Core.Persistence;
using Xunit;

namespace Axiom.Core.Tests.Persistence
{
    public class UserProfileStoreTests : IDisposable
    {
        private readonly string _dir;

        public UserProfileStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "axiom-cli-profile-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public void Load_NewProfile_HasCouncilAndWebSearchOff()
        {
            var store = new UserProfileStore(_dir);
            UserProfile profile = store.Load("fresh");

            Assert.False(profile.CouncilEnabled);
            Assert.False(profile.WebSearchEnabled);
        }

        [Fact]
        public void Load_PreExistingProfileWithLegacyCouncilOn_IsMigratedToOff()
        {
            // Simulates a profile.json written by any build before the ProfileSchemaVersion
            // field existed, back when CouncilEnabled/WebSearchEnabled defaulted to true. Every
            // real user who ever ran the CLI before that default flipped has a file shaped
            // exactly like this on disk.
            string path = Path.Combine(_dir, "legacy.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Name = "legacy",
                CouncilEnabled = true,
                WebSearchEnabled = true
            }));

            var store = new UserProfileStore(_dir);
            UserProfile profile = store.Load("legacy");

            Assert.False(profile.CouncilEnabled);
            Assert.False(profile.WebSearchEnabled);

            // Migration must persist so it only ever runs once per profile.
            string reloaded = File.ReadAllText(path);
            Assert.Contains("\"ProfileSchemaVersion\": 1", reloaded);
        }

        [Fact]
        public void Load_AlreadyMigratedProfile_RespectsUserReEnabledCouncil()
        {
            // A user who explicitly turned council back on after the migration ran must not be
            // silently reset on the next load.
            string path = Path.Combine(_dir, "reenabled.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Name = "reenabled",
                CouncilEnabled = true,
                WebSearchEnabled = false,
                ProfileSchemaVersion = 1
            }));

            var store = new UserProfileStore(_dir);
            UserProfile profile = store.Load("reenabled");

            Assert.True(profile.CouncilEnabled);
        }
    }
}
