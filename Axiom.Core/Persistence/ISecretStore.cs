using System;
using System.Collections.Generic;

namespace Axiom.Core.Persistence
{
    // Encrypts a single opaque string (e.g. an API key) for storage inside the SQLite Settings
    // table. The WPF app hardcodes Windows DPAPI here, which throws PlatformNotSupportedException
    // on Linux/macOS — this seam picks a platform-appropriate implementation instead.
    public interface ISecretStore
    {
        string Protect(string plaintext);
        string Unprotect(string protectedText);
    }

    public static class SecretStoreFactory
    {
        // Every available scheme is registered for reading, not just the platform-preferred one:
        // a data directory can outlive the machine that wrote it, and a secret that cannot be read
        // back is indistinguishable from no secret at all. See TaggedSecretStore.
        public static ISecretStore Create()
        {
            var stores = new Dictionary<string, ISecretStore>(StringComparer.Ordinal)
            {
                [TaggedSecretStore.AesFileScheme] = new AesFileSecretStore()
            };

            if (!OperatingSystem.IsWindows())
                return new TaggedSecretStore(TaggedSecretStore.AesFileScheme, stores);

            stores[TaggedSecretStore.DpapiScheme] = new WindowsDpapiSecretStore();
            return new TaggedSecretStore(TaggedSecretStore.DpapiScheme, stores);
        }
    }
}
