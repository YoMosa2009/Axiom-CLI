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

        public static string Root { get; }
        public static string ChatHistory { get; }
        public static string Logs { get; }
        public static string DatabaseFile { get; }
        public static string LocalModels { get; }

        static AppPaths()
        {
            Root = ResolveRoot();
            ChatHistory = Path.Combine(Root, "ChatHistory");
            Logs = Path.Combine(Root, "logs");
            DatabaseFile = Path.Combine(Root, "axiom_cli_data.db");
            LocalModels = Path.Combine(Root, "Models");

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
    }
}
