using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Axiom.Core.Persistence
{
    // Cross-platform fallback for macOS/Linux, where DPAPI is unavailable. A random 256-bit AES
    // key is generated on first use and stored beside the app data with owner-only permissions.
    // This is meaningfully better than plaintext (protects against casual browsing, accidental
    // backups, or committing app-data to a repo) but is not equivalent to a real OS keychain —
    // anyone with read access to the user's own account can still recover the key. Swapping in
    // the macOS Keychain / Linux Secret Service is a natural v-next improvement, tracked as a
    // known limitation rather than solved here.
    public sealed class AesFileSecretStore : ISecretStore
    {
        private const int KeySizeBytes = 32;
        private const int NonceSizeBytes = 12;
        private const int TagSizeBytes = 16;

        private readonly string _keyPath;

        public AesFileSecretStore(string? keyPath = null)
        {
            _keyPath = keyPath ?? Path.Combine(AppPaths.Root, ".secret.key");
        }

        public string Protect(string plaintext)
        {
            byte[] key = LoadOrCreateKey();
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            byte[] cipherBytes = new byte[plainBytes.Length];
            byte[] tag = new byte[TagSizeBytes];

            using (var aes = new AesGcm(key, TagSizeBytes))
                aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

            byte[] payload = new byte[NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, NonceSizeBytes);
            Buffer.BlockCopy(tag, 0, payload, NonceSizeBytes, TagSizeBytes);
            Buffer.BlockCopy(cipherBytes, 0, payload, NonceSizeBytes + TagSizeBytes, cipherBytes.Length);
            return Convert.ToBase64String(payload);
        }

        public string Unprotect(string protectedText)
        {
            byte[] key = LoadOrCreateKey();
            byte[] payload = Convert.FromBase64String(protectedText);
            if (payload.Length < NonceSizeBytes + TagSizeBytes)
                throw new CryptographicException("Encrypted payload is too short.");

            byte[] nonce = payload[..NonceSizeBytes];
            byte[] tag = payload[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
            byte[] cipherBytes = payload[(NonceSizeBytes + TagSizeBytes)..];
            byte[] plainBytes = new byte[cipherBytes.Length];

            using (var aes = new AesGcm(key, TagSizeBytes))
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }

        private byte[] LoadOrCreateKey()
        {
            if (File.Exists(_keyPath))
                return Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());

            byte[] key = RandomNumberGenerator.GetBytes(KeySizeBytes);
            string? directory = Path.GetDirectoryName(_keyPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_keyPath, Convert.ToBase64String(key));
            TryRestrictToOwnerOnly(_keyPath);
            return key;
        }

        private static void TryRestrictToOwnerOnly(string path)
        {
            if (OperatingSystem.IsWindows())
                return;

            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort — the file still exists and works even if the chmod fails.
            }
        }
    }
}
