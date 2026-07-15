using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
// System.Security.Cryptography.ProtectedData package brings in ProtectedData/DataProtectionScope.

namespace Axiom.Core.Persistence
{
    [SupportedOSPlatform("windows")]
    public sealed class WindowsDpapiSecretStore : ISecretStore
    {
        public string Protect(string plaintext)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
            byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public string Unprotect(string protectedText)
        {
            byte[] protectedBytes = Convert.FromBase64String(protectedText);
            byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }
}
