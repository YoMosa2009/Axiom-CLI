using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Axiom.Core.Persistence
{
    // Stored secrets used to be bare base64 with no record of which scheme produced them, so the
    // only way to read one back was to guess from the current OS. That guess is wrong whenever the
    // data directory outlives the machine that wrote it -- a database copied between machines, a
    // Windows profile restored onto a fresh install (DPAPI is user+machine scoped, so its blobs do
    // not survive either), or a value written by the cross-platform AES store on Linux/macOS and
    // then read on Windows. The mismatch surfaced as AuthenticationTagMismatchException on every
    // single launch, silently, for weeks.
    //
    // Writes are now tagged with the scheme that produced them, so reads route to the right
    // implementation instead of guessing. Untagged values predate this and are still supported:
    // they are tried against the platform-preferred store first, then the remaining ones.
    public sealed class TaggedSecretStore : ISecretStore
    {
        public const string DpapiScheme = "dpapi.v1";
        public const string AesFileScheme = "aesf.v1";

        // Base64 never contains ':', so a prefix can never be mistaken for payload.
        private const char SchemeSeparator = ':';

        private readonly string _primaryScheme;
        private readonly IReadOnlyDictionary<string, ISecretStore> _stores;

        public TaggedSecretStore(string primaryScheme, IReadOnlyDictionary<string, ISecretStore> stores)
        {
            if (!stores.ContainsKey(primaryScheme))
                throw new ArgumentException($"No store registered for the primary scheme '{primaryScheme}'.", nameof(primaryScheme));

            _primaryScheme = primaryScheme;
            _stores = stores;
        }

        public string Protect(string plaintext) =>
            _primaryScheme + SchemeSeparator + _stores[_primaryScheme].Protect(plaintext);

        public string Unprotect(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText))
                throw new CryptographicException("Encrypted payload is empty.");

            if (TrySplitScheme(protectedText, out string scheme, out string payload))
            {
                if (!_stores.TryGetValue(scheme, out ISecretStore? tagged))
                    throw new CryptographicException(
                        $"This secret was written by the '{scheme}' store, which is not available on this platform. " +
                        "Re-enter the key to store it in a format this machine can read.");

                return tagged.Unprotect(payload);
            }

            // Untagged legacy value: the scheme is unknown, so try the platform-preferred store
            // first and fall back to the others before giving up.
            var failures = new List<Exception>();
            foreach (string candidate in OrderedSchemes())
            {
                try
                {
                    return _stores[candidate].Unprotect(protectedText);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    failures.Add(ex);
                }
            }

            throw new CryptographicException(
                "This secret could not be decrypted by any available store. It was most likely written on a " +
                "different machine or user profile. Re-enter the key to store it in a readable format.",
                failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }

        private IEnumerable<string> OrderedSchemes() =>
            new[] { _primaryScheme }.Concat(_stores.Keys.Where(k => k != _primaryScheme));

        private static bool TrySplitScheme(string value, out string scheme, out string payload)
        {
            int separator = value.IndexOf(SchemeSeparator);
            if (separator > 0)
            {
                scheme = value[..separator];
                payload = value[(separator + 1)..];
                return true;
            }

            scheme = string.Empty;
            payload = value;
            return false;
        }
    }
}
