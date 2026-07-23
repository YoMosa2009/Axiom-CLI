using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

            if (goal?.RequiresWrittenArtifacts == true)
            {
                if (documents.Count == 0)
                {
                    findings.Add("[HIGH - COMPLETION EVIDENCE] The task requires a written deliverable, but no readable text artifact was produced.");
                }
                else
                {
                    if (goal.RequiredArtifactExtensions.Count > 0)
                    {
                        foreach (string extension in goal.RequiredArtifactExtensions)
                        {
                            if (!documents.Any(document => string.Equals(
                                    Path.GetExtension(document.Path), extension, StringComparison.OrdinalIgnoreCase)))
                            {
                                findings.Add($"[HIGH - COMPLETION EVIDENCE] The requested {extension} artifact was not written.");
                            }
                        }
                    }

                    if (documents.All(document => !HasSubstantiveContent(document)))
                    {
                        findings.Add("[HIGH - COMPLETION EVIDENCE] Written files contain no substantive implementation; a scaffold, empty file, or metadata-only output is not a completed deliverable.");
                    }

                    if (goal.RequiresInteractiveBehavior && HasHtmlArtifact(documents)
                        && !HasExecutableClientBehavior(documents))
                    {
                        findings.Add("[HIGH - COMPLETION EVIDENCE] The request requires interactive behavior, but the written browser artifact contains no executable client behavior.");
                    }
                }
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

        public static bool HasBlockingFindings(IEnumerable<string>? findings)
            => (findings ?? Array.Empty<string>()).Any(f =>
                f.Contains("[HIGH", StringComparison.OrdinalIgnoreCase)
                || f.Contains("[CRITICAL", StringComparison.OrdinalIgnoreCase));

        private static bool HasSubstantiveContent(ArtifactDocument document)
        {
            string content = document.Content;
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string withoutComments = Regex.Replace(
                content,
                @"<!--.*?-->|/\*.*?\*/",
                string.Empty,
                RegexOptions.Singleline);
            withoutComments = Regex.Replace(
                withoutComments,
                @"^\s*(?://|#).*$",
                string.Empty,
                RegexOptions.Multiline).Trim();
            if (withoutComments.Length < 16)
                return false;

            string extension = Path.GetExtension(document.Path).ToLowerInvariant();
            if (extension is not ".html" and not ".htm")
                return true;

            Match body = Regex.Match(withoutComments, @"<body\b[^>]*>(?<content>.*?)</body\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!body.Success)
                return true;

            string bodyContent = body.Groups["content"].Value.Trim();
            return bodyContent.Length >= 16
                && (Regex.IsMatch(bodyContent, @"<(?:canvas|svg|main|section|article|div|button|input|form|script)\b", RegexOptions.IgnoreCase)
                    || Regex.Replace(bodyContent, @"<[^>]+>", string.Empty).Trim().Length > 0);
        }

        private static bool HasHtmlArtifact(IEnumerable<ArtifactDocument> documents)
            => documents.Any(document => Path.GetExtension(document.Path).Equals(".html", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(document.Path).Equals(".htm", StringComparison.OrdinalIgnoreCase));

        private static bool HasExecutableClientBehavior(IEnumerable<ArtifactDocument> documents)
        {
            foreach (ArtifactDocument document in documents)
            {
                string extension = Path.GetExtension(document.Path).ToLowerInvariant();
                if (extension is ".js" or ".jsx" or ".ts" or ".tsx")
                {
                    if (HasSubstantiveContent(document))
                        return true;
                    continue;
                }

                if (extension is not ".html" and not ".htm")
                    continue;

                if (Regex.IsMatch(document.Content, @"\bon[a-z]+\s*=\s*['""][^'""]+", RegexOptions.IgnoreCase))
                    return true;

                foreach (Match script in Regex.Matches(
                    document.Content,
                    @"<script\b(?<attributes>[^>]*)>(?<code>.*?)</script\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    string attributes = script.Groups["attributes"].Value;
                    if (Regex.IsMatch(attributes, @"\bsrc\s*=\s*['""][^'""]+", RegexOptions.IgnoreCase))
                        return true;

                    string code = Regex.Replace(script.Groups["code"].Value, @"/\*.*?\*/|//.*$", string.Empty,
                        RegexOptions.Singleline | RegexOptions.Multiline).Trim();
                    if (code.Length >= 12)
                        return true;
                }
            }

            return false;
        }

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
