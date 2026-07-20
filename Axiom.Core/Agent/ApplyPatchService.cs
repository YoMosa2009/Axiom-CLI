using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Structured search-replace and multi-hunk patch application.
    /// </summary>
    public static class ApplyPatchService
    {
        public static string StrReplace(string path, string oldText, string newText, bool replaceAll = false)
        {
            if (string.IsNullOrEmpty(path))
                return "Error: path is required.";
            if (oldText is null)
                return "Error: old_string is required.";
            if (!File.Exists(path))
                return $"Error: file not found: {path}";

            string content = File.ReadAllText(path);
            int count = CountOccurrences(content, oldText);
            if (count == 0)
                return "Error: old_string not found in file (exact match required).";
            if (count > 1 && !replaceAll)
                return $"Error: old_string matches {count} times; set replace_all=true or provide a more unique string.";

            string updated = replaceAll
                ? content.Replace(oldText, newText ?? string.Empty, StringComparison.Ordinal)
                : ReplaceFirst(content, oldText, newText ?? string.Empty);

            File.WriteAllText(path, updated);
            int lines = (newText ?? string.Empty).Split('\n').Length;
            return $"Patched {path} ({(replaceAll ? count : 1)} replacement(s), ~{lines} new line(s)).";
        }

        /// <summary>
        /// Applies a simple unified-diff style patch or V4A-like blocks:
        /// *** Begin Patch
        /// *** Update File: path
        /// @@
        /// -old
        /// +new
        /// *** End Patch
        /// </summary>
        public static string ApplyStructuredPatch(string workspaceRoot, string patchText, Func<string, string> resolvePath)
        {
            if (string.IsNullOrWhiteSpace(patchText))
                return "Error: patch text is empty.";

            var results = new StringBuilder();
            int files = 0, ok = 0, fail = 0;

            // Prefer *** Update File: style
            var fileBlocks = Regex.Split(patchText, @"(?=^\*\*\* (?:Update|Add|Delete) File:)", RegexOptions.Multiline);
            if (fileBlocks.Length <= 1)
            {
                // Fallback: treat whole text as a single search-replace instruction is not possible;
                // try classic unified diff --- a/ +++ b/
                return ApplyUnifiedDiff(workspaceRoot, patchText, resolvePath, results);
            }

            foreach (string block in fileBlocks)
            {
                if (string.IsNullOrWhiteSpace(block))
                    continue;
                Match header = Regex.Match(block, @"^\*\*\* (Update|Add|Delete) File:\s*(.+)$", RegexOptions.Multiline);
                if (!header.Success)
                    continue;

                files++;
                string kind = header.Groups[1].Value;
                string rel = header.Groups[2].Value.Trim();
                string path;
                try { path = resolvePath(rel); }
                catch (Exception ex)
                {
                    fail++;
                    results.AppendLine($"fail {rel}: {ex.Message}");
                    continue;
                }

                try
                {
                    if (kind.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                            ok++;
                            results.AppendLine($"deleted {path}");
                        }
                        else
                        {
                            fail++;
                            results.AppendLine($"fail delete (missing): {path}");
                        }
                        continue;
                    }

                    if (kind.Equals("Add", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = ExtractPlusLines(block);
                        string? dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrWhiteSpace(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(path, body);
                        ok++;
                        results.AppendLine($"added {path} ({body.Length} chars)");
                        continue;
                    }

                    // Update: apply consecutive -/+ pairs as search-replace, or full rewrite if only + lines after @@
                    if (!File.Exists(path))
                    {
                        fail++;
                        results.AppendLine($"fail update (missing): {path}");
                        continue;
                    }

                    string content = File.ReadAllText(path);
                    string? applied = ApplyHunks(content, block);
                    if (applied == null)
                    {
                        fail++;
                        results.AppendLine($"fail update (hunks did not match): {path}");
                        continue;
                    }
                    File.WriteAllText(path, applied);
                    ok++;
                    results.AppendLine($"updated {path}");
                }
                catch (Exception ex)
                {
                    fail++;
                    results.AppendLine($"fail {path}: {ex.Message}");
                }
            }

            if (files == 0)
                return ApplyUnifiedDiff(workspaceRoot, patchText, resolvePath, results);

            results.Insert(0, $"apply_patch: files={files} ok={ok} fail={fail}\n");
            return results.ToString().TrimEnd();
        }

        private static string ApplyUnifiedDiff(
            string workspaceRoot,
            string patchText,
            Func<string, string> resolvePath,
            StringBuilder results)
        {
            // Minimal: find +++ b/path and then try -/+ hunk application on that file
            Match m = Regex.Match(patchText, @"^\+\+\+\s+(?:b/)?(.+)$", RegexOptions.Multiline);
            if (!m.Success)
                return "Error: unrecognized patch format. Use str_replace or *** Update File: path with -/+ lines.";

            string rel = m.Groups[1].Value.Trim();
            if (rel == "/dev/null")
                return "Error: unified delete not supported here; use apply_patch *** Delete File.";

            string path = resolvePath(rel);
            if (!File.Exists(path))
                return $"Error: file not found for patch: {path}";

            string content = File.ReadAllText(path);
            string? applied = ApplyHunks(content, patchText);
            if (applied == null)
                return $"Error: patch hunks did not match for {path}";

            File.WriteAllText(path, applied);
            results.AppendLine($"updated {path}");
            return results.ToString().TrimEnd();
        }

        private static string? ApplyHunks(string content, string block)
        {
            // Extract sequences of consecutive - lines followed by + lines as one replacement.
            var lines = block.Replace("\r\n", "\n").Split('\n');
            string current = content;
            var oldBuf = new List<string>();
            var newBuf = new List<string>();

            void Flush()
            {
                if (oldBuf.Count == 0 && newBuf.Count == 0)
                    return;
                string oldText = string.Join("\n", oldBuf);
                string newText = string.Join("\n", newBuf);
                if (oldBuf.Count == 0)
                {
                    // pure add — append if empty file else skip (ambiguous)
                    if (string.IsNullOrEmpty(current))
                        current = newText;
                }
                else if (current.Contains(oldText, StringComparison.Ordinal))
                {
                    current = ReplaceFirst(current, oldText, newText);
                }
                else
                {
                    // try with trailing newline variants
                    string oldAlt = oldText.TrimEnd('\n') + "\n";
                    if (current.Contains(oldAlt, StringComparison.Ordinal))
                        current = ReplaceFirst(current, oldAlt, newText.EndsWith("\n") ? newText : newText + "\n");
                    else
                        throw new InvalidOperationException("hunk mismatch");
                }
                oldBuf.Clear();
                newBuf.Clear();
            }

            try
            {
                foreach (string raw in lines)
                {
                    if (raw.StartsWith("***") || raw.StartsWith("---") || raw.StartsWith("+++") || raw.StartsWith("@@"))
                    {
                        Flush();
                        continue;
                    }
                    if (raw.StartsWith("-") && !raw.StartsWith("---"))
                    {
                        if (newBuf.Count > 0)
                            Flush();
                        oldBuf.Add(raw[1..]);
                    }
                    else if (raw.StartsWith("+") && !raw.StartsWith("+++"))
                    {
                        newBuf.Add(raw[1..]);
                    }
                    else if (raw.StartsWith(" "))
                    {
                        // context — flush pending edit, don't require context match for simplicity
                        Flush();
                    }
                }
                Flush();
                return current;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractPlusLines(string block)
        {
            var sb = new StringBuilder();
            foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.StartsWith("+") && !raw.StartsWith("+++"))
                    sb.AppendLine(raw[1..]);
            }
            return sb.ToString();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (needle.Length == 0)
                return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string ReplaceFirst(string text, string oldValue, string newValue)
        {
            int i = text.IndexOf(oldValue, StringComparison.Ordinal);
            if (i < 0)
                return text;
            return text[..i] + newValue + text[(i + oldValue.Length)..];
        }
    }
}
