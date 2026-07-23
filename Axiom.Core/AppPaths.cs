using System;
using System.IO;

namespace Axiom.Core
{
    // Central data locations for the CLI, resolved the same way on Windows/macOS/Linux via
    // Environment.SpecialFolder.LocalApplicationData (maps to %LOCALAPPDATA%, ~/.local/share,
    // and ~/Library/Application Support respectively). Override with AXIOM_CLI_DATA_DIR for an
    // isolated folder (tests, smoke-testing a release build without touching real user data).
    public static class AppPaths
    {
        public const string DataDirEnvironmentVariable = "AXIOM_CLI_DATA_DIR";

        // Separate from DataDirEnvironmentVariable on purpose: that one already controls the
        // whole data root (chat history, main db, secrets) -- conflating them would force anyone
        // relocating a multi-GB kestral memory store to also relocate everything else, and vice
        // versa (an isolated test data dir would otherwise drag the memory store along with it).
        public const string KestralMemoryDirEnvironmentVariable = "AXIOM_CLI_KESTRAL_MEMORY_DIR";

        public static string Root { get; }
        public static string ChatHistory { get; }
        public static string Logs { get; }
        public static string DatabaseFile { get; }
        public static string LocalModels { get; }

        // Cross-platform default only -- any machine-specific override (e.g. pointing this at a
        // dedicated drive) belongs in the user's profile settings or the env var above, never in
        // source, since this is a public repo other people clone on other platforms/hardware.
        public static string KestralMemoryRoot { get; }

        static AppPaths()
        {
            Root = ResolveRoot();
            ChatHistory = Path.Combine(Root, "ChatHistory");
            Logs = Path.Combine(Root, "logs");
            DatabaseFile = Path.Combine(Root, "axiom_cli_data.db");
            LocalModels = Path.Combine(Root, "Models");
            KestralMemoryRoot = ResolveKestralMemoryRoot();

            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(ChatHistory);
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(LocalModels);
        }

        private static string ResolveRoot()
        {
            string? overrideDir = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return Path.GetFullPath(overrideDir.Trim());

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "axiom-cli");
        }

        private static string ResolveKestralMemoryRoot()
        {
            string? overrideDir = Environment.GetEnvironmentVariable(KestralMemoryDirEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return Path.GetFullPath(overrideDir.Trim());

            return Path.Combine(Root, "KestralMemory");
        }
    }
}
