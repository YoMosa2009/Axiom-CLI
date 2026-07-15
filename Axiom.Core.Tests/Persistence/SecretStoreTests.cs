using System;
using System.IO;
using Axiom.Core.Persistence;
using Xunit;

namespace Axiom.Core.Tests.Persistence
{
    public class SecretStoreTests
    {
        [Fact]
        public void AesFileSecretStore_RoundTripsThroughFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "axiom-cli-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var store = new AesFileSecretStore(Path.Combine(tempDir, ".secret.key"));
                string protectedText = store.Protect("sk-or-v1-super-secret-key");
                Assert.NotEqual("sk-or-v1-super-secret-key", protectedText);

                string roundTripped = store.Unprotect(protectedText);
                Assert.Equal("sk-or-v1-super-secret-key", roundTripped);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Factory_ReturnsWorkingStoreForCurrentPlatform()
        {
            ISecretStore store = SecretStoreFactory.Create();
            string protectedText = store.Protect("round-trip-me");
            Assert.Equal("round-trip-me", store.Unprotect(protectedText));
        }
    }
}
