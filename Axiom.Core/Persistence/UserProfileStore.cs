using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Core.Agent;

namespace Axiom.Core.Persistence
{
    public sealed class UserProfile
    {
        public string Name { get; set; } = "default";
        public string? DefaultModelId { get; set; }
        public string? DefaultModelLabel { get; set; }
        public string ApprovalMode { get; set; } = "auto";
        public bool CouncilEnabled { get; set; } = true;
        public bool WebSearchEnabled { get; set; } = true;
        public bool SandboxEnabled { get; set; }
        public bool CalculatorEnabled { get; set; } = true;
        public List<string> WorkspaceRoots { get; set; } = new();
        public bool WorkspaceExclusive { get; set; }
        public bool OnboardingComplete { get; set; }
        public HashSet<string> OnboardingStepsDone { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class UserProfileStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _dir;

        public UserProfileStore(string? directory = null)
        {
            _dir = directory ?? Path.Combine(AppPaths.Root, "Profiles");
            Directory.CreateDirectory(_dir);
        }

        public string DirectoryPath => _dir;

        public static string? ActiveProfileName
        {
            get => Environment.GetEnvironmentVariable("AXIOM_CLI_PROFILE");
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    Environment.SetEnvironmentVariable("AXIOM_CLI_PROFILE", null);
                else
                    Environment.SetEnvironmentVariable("AXIOM_CLI_PROFILE", value.Trim());
            }
        }

        public string ResolveActiveName(string? cliOverride = null)
        {
            if (!string.IsNullOrWhiteSpace(cliOverride))
                return Sanitize(cliOverride);
            string? env = ActiveProfileName;
            if (!string.IsNullOrWhiteSpace(env))
                return Sanitize(env);
            return "default";
        }

        public UserProfile Load(string? name = null)
        {
            string n = ResolveActiveName(name);
            string path = Path.Combine(_dir, n + ".json");
            if (!File.Exists(path))
            {
                var fresh = new UserProfile { Name = n };
                Save(fresh);
                return fresh;
            }

            try
            {
                var p = JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(path), JsonOptions)
                    ?? new UserProfile { Name = n };
                p.Name = n;
                p.OnboardingStepsDone ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return p;
            }
            catch
            {
                return new UserProfile { Name = n };
            }
        }

        public void Save(UserProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            profile.Name = Sanitize(profile.Name);
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, profile.Name + ".json");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(profile, JsonOptions));
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }

        public IReadOnlyList<string> ListNames()
        {
            try
            {
                return Directory.EnumerateFiles(_dir, "*.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public static ApprovalMode ParseApproval(string? raw) => (raw ?? "auto").Trim().ToLowerInvariant() switch
        {
            "ask" or "confirm" or "safe" => ApprovalMode.Ask,
            "plan" or "readonly" or "read-only" or "dry" or "dry-run" => ApprovalMode.Plan,
            _ => ApprovalMode.Auto
        };

        public static string FormatApproval(ApprovalMode mode) => mode switch
        {
            ApprovalMode.Ask => "ask",
            ApprovalMode.Plan => "plan",
            _ => "auto"
        };

        private static string Sanitize(string name)
        {
            string n = (name ?? "default").Trim().ToLowerInvariant();
            if (n.Length == 0)
                n = "default";
            foreach (char c in Path.GetInvalidFileNameChars())
                n = n.Replace(c, '_');
            return n;
        }
    }
}
