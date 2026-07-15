using System;

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
        public static ISecretStore Create() => OperatingSystem.IsWindows()
            ? new WindowsDpapiSecretStore()
            : new AesFileSecretStore();
    }
}
