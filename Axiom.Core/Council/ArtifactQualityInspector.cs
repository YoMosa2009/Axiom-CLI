using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Axiom.Core.Council
{
    public sealed record ArtifactDocument(string Path, string Content);

    public sealed record ArtifactQualitySnapshot(
        IReadOnlyList<ArtifactDocument> Documents,
        IReadOnlyList<string> Findings,
        string EvidenceBlock);

    /// <summary>
    /// Shared deterministic inspection for Council and solo-agent writes. It deliberately stays
    /// artifact-agnostic at the orchestration layer; StaticValidation applies file-type checks.
    /// </summary>
    public static class ArtifactQualityInspector
    {
        private const int MaxArtifactBytes = 512_000;

        public static ArtifactQualitySnapshot Inspect(
            IEnumerable<string>? writtenPaths,
            GoalContract? goal = null,
            int evidenceCharacterBudget = 10_000)
        {
            IReadOnlyList<ArtifactDocument> documents = ReadArtifacts(writtenPaths);
            var findings = new List<string>();

            foreach (ArtifactDocument document in documents)
            {
                foreach (string finding in StaticValidation.RunArtifactChecks(document.Content, document.Path))
                    findings.Add($"{document.Path}: {finding}");
            }

            if (goal is { RequiresLiteralPresenceInWrittenArtifacts: true, ExactLiterals.Count: > 0 }
                && documents.Count > 0)
            {
                string combined = string.Join("\n", documents.Select(document => document.Content));
                foreach (string literal in goal.ExactLiterals)
                {
                    if (!combined.Contains(literal, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(
                            $"[HIGH — REQUIREMENT COVERAGE] Exact requested literal is missing from the written artifacts: \"{literal}\".");
                    }
                }
            }

            IReadOnlyList<string> distinct = findings
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();
            return new ArtifactQualitySnapshot(
                documents,
                distinct,
                BuildEvidenceBlock(documents, evidenceCharacterBudget));
        }

        public static IReadOnlyList<ArtifactDocument> ReadArtifacts(IEnumerable<string>? writtenPaths)
        {
            var documents = new List<ArtifactDocument>();
            foreach (string path in writtenPaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !IsTextArtifactExtension(Path.GetExtension(path)))
                    continue;

                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length > MaxArtifactBytes)
                        continue;

                    string content = File.ReadAllText(path);
                    if (!content.Contains('\0'))
                        documents.Add(new ArtifactDocument(info.FullName, content));
                }
                catch
                {
                    // A transient read should not fail the turn; model inspect tools remain available.
                }
            }

            return documents
                .GroupBy(document => document.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
        }

        public static bool IsTextArtifactExtension(string? extension)
            => (extension ?? string.Empty).ToLowerInvariant() is
                ".cs" or ".csx" or ".js" or ".jsx" or ".ts" or ".tsx"
                or ".py" or ".java" or ".go" or ".rs" or ".cpp" or ".c" or ".h" or ".hpp"
                or ".html" or ".htm" or ".css" or ".json" or ".xml" or ".yml" or ".yaml"
                or ".md" or ".txt" or ".sql" or ".sh" or ".ps1";

        private static string BuildEvidenceBlock(
            IReadOnlyList<ArtifactDocument> documents,
            int characterBudget)
        {
            if (documents.Count == 0 || characterBudget <= 0)
                return string.Empty;

            int budget = Math.Clamp(characterBudget, 1_000, 48_000);
            var sb = new StringBuilder();
            sb.AppendLine("[WRITTEN ARTIFACT EVIDENCE — actual files, not Builder prose]");

            foreach (ArtifactDocument document in documents)
            {
                string header = $"--- {document.Path} ---\n";
                if (sb.Length + header.Length >= budget)
                    break;
                sb.Append(header);

                int remaining = budget - sb.Length;
                if (remaining <= 0)
                    break;
                string content = document.Content;
                if (content.Length > remaining)
                {
                    int headLength = Math.Max(0, remaining * 3 / 4);
                    int tailLength = Math.Max(0, remaining - headLength - 40);
                    if (tailLength > 0)
                    {
                        sb.Append(content[..headLength])
                            .Append("\n[...artifact middle truncated...]\n")
                            .Append(content[^tailLength..]);
                    }
                    else
                    {
                        sb.Append(content[..remaining]);
                    }
                    break;
                }

                sb.AppendLine(content);
            }

            if (sb.Length > budget)
                return sb.ToString(0, budget);
            return sb.ToString();
        }
    }
}
