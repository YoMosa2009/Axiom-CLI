using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Axiom.Core.Persistence;
using Xunit;

namespace Axiom.Core.Tests.Persistence;

public sealed class TaggedSecretStoreTests
{
    private const string Plaintext = "sk-test-abc123";

    // Stands in for a scheme that is not available on the current platform (e.g. reading a DPAPI
    // value on Linux) without depending on the real OS-specific store.
    private sealed class FakeStore : ISecretStore
    {
        private readonly string _marker;
        public FakeStore(string marker) => _marker = marker;

        public string Protect(string plaintext) => _marker + "|" + plaintext;

        public string Unprotect(string protectedText) =>
            protectedText.StartsWith(_marker + "|", StringComparison.Ordinal)
                ? protectedText[(_marker.Length + 1)..]
                : throw new CryptographicException("Wrong store for this payload.");
    }

    private static TaggedSecretStore Create(string primary, params (string Scheme, ISecretStore Store)[] stores)
    {
        var map = new Dictionary<string, ISecretStore>(StringComparer.Ordinal);
        foreach ((string scheme, ISecretStore store) in stores)
            map[scheme] = store;
        return new TaggedSecretStore(primary, map);
    }

    [Fact]
    public void Protect_TagsTheValueWithTheSchemeThatWroteIt()
    {
        TaggedSecretStore store = Create("a.v1", ("a.v1", new FakeStore("A")));

        string protectedValue = store.Protect(Plaintext);

        Assert.StartsWith("a.v1:", protectedValue, StringComparison.Ordinal);
        Assert.Equal(Plaintext, store.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_RoutesToTheTaggedStoreEvenWhenItIsNotThePrimary()
    {
        // The value was written by the secondary scheme. Guessing from the platform -- the old
        // behaviour -- would try the primary and fail.
        TaggedSecretStore writer = Create("b.v1", ("b.v1", new FakeStore("B")));
        string written = writer.Protect(Plaintext);

        TaggedSecretStore reader = Create(
            "a.v1",
            ("a.v1", new FakeStore("A")),
            ("b.v1", new FakeStore("B")));

        Assert.Equal(Plaintext, reader.Unprotect(written));
    }

    [Fact]
    public void Unprotect_ReadsUntaggedLegacyValuesWrittenByAnyAvailableStore()
    {
        // Written before scheme tagging existed, by what is now the secondary store.
        string legacy = new FakeStore("B").Protect(Plaintext);

        TaggedSecretStore reader = Create(
            "a.v1",
            ("a.v1", new FakeStore("A")),
            ("b.v1", new FakeStore("B")));

        Assert.Equal(Plaintext, reader.Unprotect(legacy));
    }

    [Fact]
    public void Unprotect_ExplainsItselfWhenTheTaggedSchemeIsUnavailableHere()
    {
        TaggedSecretStore writer = Create("b.v1", ("b.v1", new FakeStore("B")));
        string written = writer.Protect(Plaintext);

        TaggedSecretStore reader = Create("a.v1", ("a.v1", new FakeStore("A")));

        var ex = Assert.Throws<CryptographicException>(() => reader.Unprotect(written));
        Assert.Contains("b.v1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Re-enter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unprotect_ThrowsWhenNoAvailableStoreCanReadAnUntaggedValue()
    {
        TaggedSecretStore reader = Create("a.v1", ("a.v1", new FakeStore("A")));

        Assert.Throws<CryptographicException>(() => reader.Unprotect("C|nonsense"));
    }

    [Fact]
    public void AesFileSecretStore_ReadingWithoutKeyMaterialDoesNotMintAKey()
    {
        // Minting a key on read turned "the key file did not come with the data directory" into a
        // tag mismatch that reads like corruption, and left a key file behind that made the next
        // launch look correctly configured.
        string directory = Path.Combine(Path.GetTempPath(), "axiom-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string keyPath = Path.Combine(directory, ".secret.key");

        try
        {
            string payload = new AesFileSecretStore(keyPath).Protect(Plaintext);
            Assert.True(File.Exists(keyPath));

            File.Delete(keyPath);
            var store = new AesFileSecretStore(keyPath);

            var ex = Assert.Throws<CryptographicException>(() => store.Unprotect(payload));
            Assert.Contains("No local key material", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(keyPath));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
