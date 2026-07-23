using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Axiom.Core.Council
{
    // Deterministic pre-Critic checks ported from WorkplaceView.RunStaticValidation /
    // DetectSandboxErrors / DetectLanguage. Runs before the Critic LLM so findings are
    // injected as PRE-FLAGGED ISSUES rather than relying on the model alone.
    public static class StaticValidation
    {
        private static readonly Regex TypedCodeFenceRegex = new(
            @"```(?<language>[A-Za-z0-9_+#\.-]+)\s*\r?\n(?<code>[\s\S]*?)```",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> SandboxLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            "python", "java"
        };

        public static bool IsSandboxLanguage(string language)
            => SandboxLanguages.Contains(language);

        public static bool LooksLikeCode(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string lang = DetectLanguage(content);
            return lang is not ("markdown" or "json" or "xml" or "css" or "sql");
        }

        public static string DetectLanguage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "markdown";

            string lower = content.ToLowerInvariant();
            Match typedFence = TypedCodeFenceRegex.Match(content);
            string? fencedLanguage = typedFence.Success
                ? NormalizeCodeLanguage(typedFence.Groups["language"].Value)
                : null;
            if (!string.IsNullOrWhiteSpace(fencedLanguage))
                return fencedLanguage;

            if (lower.Contains("<!doctype") || lower.Contains("<html")
                || (lower.Contains("<body") && lower.Contains("</body>")))
                return "html";

            if (lower.Contains("namespace ")
                || (lower.Contains("using ") && Regex.IsMatch(content, @"\b(?:class|record|struct|interface)\s+\w+"))
                || lower.Contains("console.writeline"))
                return "c#";

            if (Regex.IsMatch(content, @"(?m)^\s*(?:def\s+\w+\s*\(|from\s+\S+\s+import\s+|import\s+\S+)|\bif\s+__name__\s*=="))
                return "python";

            if ((lower.Contains("public class ") && lower.Contains("public static void main"))
                || lower.Contains("system.out.println"))
                return "java";

            if (Regex.IsMatch(content, @"\b(?:interface|type)\s+\w+\s*[={]|:\s*(?:string|number|boolean)(?:\[\])?\s*[;,]", RegexOptions.IgnoreCase))
                return "typescript";

            if (Regex.IsMatch(content, @"\b(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*=|\bfunction\s+[A-Za-z_$][\w$]*\s*\(|=>|document\.(?:getElementById|querySelector)", RegexOptions.IgnoreCase))
                return "javascript";

            if (Regex.IsMatch(content, @"(?is)^\s*(?:select\s+.+\s+from\s+|insert\s+into\s+|update\s+\S+\s+set\s+|create\s+(?:table|view|index)\s+)", RegexOptions.IgnoreCase))
                return "sql";

            if (lower.Contains("#include") || lower.Contains("std::"))
                return "cpp";

            if (Regex.IsMatch(content, @"(?m)^\s*(?:fn\s+\w+\s*\(|use\s+\w+::)"))
                return "rust";

            if (Regex.IsMatch(content, @"(?m)^\s*(?:package\s+\w+|func\s+\w+\s*\()"))
                return "go";

            return "markdown";
        }

        public static string? NormalizeCodeLanguage(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string lang = raw.Trim().ToLowerInvariant();
            return lang switch
            {
                "py" or "python3" => "python",
                "cs" or "csharp" or "c#" => "c#",
                "js" or "node" => "javascript",
                "ts" => "typescript",
                "c++" or "cxx" or "cc" => "cpp",
                "sh" or "shell" or "zsh" or "bash" => "bash",
                "ps1" or "pwsh" => "powershell",
                _ => lang
            };
        }

        /// <summary>
        /// Prefer the largest typed fence matching <paramref name="preferredLanguage"/>;
        /// otherwise the largest fence; otherwise the full text.
        /// </summary>
        public static string ExtractCodeBlock(string content, string? preferredLanguage = null)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            MatchCollection matches = TypedCodeFenceRegex.Matches(content);
            if (matches.Count == 0)
                return content.Trim();

            string? preferred = NormalizeCodeLanguage(preferredLanguage);
            Match? best = null;
            int bestLen = -1;

            foreach (Match match in matches)
            {
                string body = match.Groups["code"].Value.Trim();
                if (body.Length == 0)
                    continue;

                string fenceLang = NormalizeCodeLanguage(match.Groups["language"].Value) ?? string.Empty;
                bool langMatch = preferred == null
                    || string.Equals(fenceLang, preferred, StringComparison.OrdinalIgnoreCase);

                int score = body.Length + (langMatch && preferred != null ? 1_000_000 : 0);
                if (score > bestLen)
                {
                    bestLen = score;
                    best = match;
                }
            }

            return best != null ? best.Groups["code"].Value.Trim() : content.Trim();
        }

        public static List<string> Run(string code)
        {
            var findings = new List<string>();
            if (string.IsNullOrWhiteSpace(code))
                return findings;

            // Strip patch envelope labels so brace checks don't fire on meta markup alone.
            string analyzable = StripPatchEnvelopeNoise(code);
            string language = DetectLanguage(analyzable);
            AddStructureBalanceChecks(analyzable, findings);

            switch (language)
            {
                case "python":
                    ValidatePythonCode(analyzable, findings);
                    break;
                case "c#":
                case "java":
                    ValidateCStyleCode(analyzable, findings);
                    break;
                case "javascript":
                case "typescript":
                    ValidateJavaScriptCode(analyzable, findings);
                    break;
                case "html":
                    ValidateHtmlCode(analyzable, findings);
                    break;
                default:
                    // Unknown / markdown: still run call/truncation heuristics when braces or
                    // defs suggest embedded code (e.g. incomplete class snippets).
                    if (LooksLikeCode(analyzable)
                        || analyzable.Contains('{')
                        || analyzable.Contains("def ")
                        || analyzable.Contains("class "))
                        ValidateGenericCalls(analyzable, findings);
                    break;
            }

            return findings.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        }

        // A structurally valid HTML document can still be an unstyled browser-default page.
        // Keep this separate from Run(): plain HTML fragments, emails, and templates are valid
        // in many code tasks. The Council invokes these checks only for an explicit website build.
        public static List<string> RunWebsiteQualityChecks(string html)
        {
            var findings = new List<string>();
            if (string.IsNullOrWhiteSpace(html))
                return findings;

            string lower = html.ToLowerInvariant();
            bool hasEmbeddedCss = Regex.IsMatch(lower, @"<style\b[^>]*>\s*[^<]+", RegexOptions.IgnoreCase);
            bool hasStylesheet = Regex.IsMatch(lower, "<link\\b[^>]*rel\\s*=\\s*['\\\"]?stylesheet", RegexOptions.IgnoreCase);
            if (!hasEmbeddedCss && !hasStylesheet)
            {
                findings.Add("[HIGH — WEBSITE QUALITY] The website has no stylesheet or non-empty <style> block, so it will render with browser-default styling.");
            }

            if (!lower.Contains("name=\"viewport\"") && !lower.Contains("name='viewport'"))
            {
                findings.Add("[MEDIUM — WEBSITE QUALITY] The website is missing a viewport meta tag, so mobile rendering is not configured.");
            }

            return findings;
        }

        public static List<string> DetectSandboxErrors(string sandboxOutput)
        {
            var findings = new List<string>();
            if (string.IsNullOrWhiteSpace(sandboxOutput))
                return findings;

            if (sandboxOutput.StartsWith("[[PYTHON TIMEOUT]]", StringComparison.OrdinalIgnoreCase)
                || sandboxOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add("[CRITICAL — SANDBOX TIMEOUT] The script took too long to execute.");
                return findings;
            }

            string[] errorIndicators =
            [
                "Traceback", "Error:", "Exception:", "SyntaxError", "NameError",
                "TypeError", "ValueError", "IndentationError", "KeyError",
                "IndexError", "AttributeError", "ImportError", "ZeroDivisionError",
                "FileNotFoundError", "RuntimeError", "OverflowError",
                "Compilation errors", "error: ", "Exception in thread"
            ];

            var detectedErrors = new List<string>();
            foreach (string line in sandboxOutput.Split('\n'))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                foreach (string indicator in errorIndicators)
                {
                    if (trimmed.Contains(indicator, StringComparison.Ordinal))
                    {
                        detectedErrors.Add(trimmed);
                        break;
                    }
                }

                if (trimmed.StartsWith("Error", StringComparison.Ordinal)
                    && trimmed.Length > 5
                    && trimmed[5] == ':'
                    && !detectedErrors.Contains(trimmed))
                {
                    detectedErrors.Add(trimmed);
                }
            }

            if (detectedErrors.Count > 0)
            {
                findings.Add("[CRITICAL — RUNTIME ERROR] Code execution produced the following error(s):");
                foreach (string err in detectedErrors.Take(10))
                    findings.Add($"  RUNTIME: {err}");
            }

            return findings;
        }

        private static string StripPatchEnvelopeNoise(string code)
        {
            if (!code.Contains("[[AXIOM_CODEBASE_PATCH]]", StringComparison.Ordinal))
                return code;

            // Analyze SEARCH/REPLACE bodies rather than envelope markers.
            var bodies = new List<string>();
            foreach (Match m in Regex.Matches(code,
                         @"<<<<<<< SEARCH\r?\n([\s\S]*?)=======\r?\n([\s\S]*?)>>>>>>> REPLACE",
                         RegexOptions.Multiline))
            {
                bodies.Add(m.Groups[2].Value); // new code side
            }

            return bodies.Count > 0 ? string.Join("\n\n", bodies) : code;
        }

        private static void AddStructureBalanceChecks(string code, List<string> findings)
        {
            int openBraces = code.Count(c => c == '{');
            int closeBraces = code.Count(c => c == '}');
            if (openBraces != closeBraces)
                findings.Add($"Mismatched braces: {openBraces} opening vs {closeBraces} closing.");

            int openParens = code.Count(c => c == '(');
            int closeParens = code.Count(c => c == ')');
            if (openParens != closeParens)
                findings.Add($"Mismatched parentheses: {openParens} opening vs {closeParens} closing.");

            int openBrackets = code.Count(c => c == '[');
            int closeBrackets = code.Count(c => c == ']');
            // Patch envelopes and markdown use many brackets; only flag large imbalance.
            if (openBrackets != closeBrackets && Math.Abs(openBrackets - closeBrackets) > 2)
                findings.Add($"Mismatched brackets: {openBrackets} opening vs {closeBrackets} closing.");
        }

        private static void ValidatePythonCode(string code, List<string> findings)
        {
            bool hasTabs = false;
            bool hasSpaces = false;
            foreach (string line in code.Split('\n'))
            {
                if (line.Length > 0 && line[0] == '\t') hasTabs = true;
                if (line.Length > 0 && line[0] == ' ') hasSpaces = true;
            }

            if (hasTabs && hasSpaces)
                findings.Add("Mixed indentation: both tabs and spaces used. Python requires consistent indentation.");

            ValidateGenericCalls(code, findings);
        }

        private static void ValidateCStyleCode(string code, List<string> findings)
        {
            ValidateGenericCalls(code, findings);

            if (code.Contains("class ", StringComparison.OrdinalIgnoreCase)
                && !code.Contains("namespace ", StringComparison.OrdinalIgnoreCase)
                && code.Contains("using ", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add("C#-style source appears to miss a namespace declaration.");
            }
        }

        private static void ValidateJavaScriptCode(string code, List<string> findings)
        {
            if (code.Contains("=>") && code.Contains("function ", StringComparison.OrdinalIgnoreCase))
                findings.Add("Mixed JS styles detected (arrow and function declarations); verify consistency.");

            if (code.Contains("var ", StringComparison.Ordinal))
                findings.Add("Found 'var' declarations; prefer 'const' or 'let' for safer scope semantics.");
        }

        private static void ValidateHtmlCode(string code, List<string> findings)
        {
            string lower = code.ToLowerInvariant();
            if (!lower.Contains("<html")) findings.Add("Missing <html> tag.");
            if (!lower.Contains("<head")) findings.Add("Missing <head> tag.");
            if (!lower.Contains("<body")) findings.Add("Missing <body> tag.");
            if (!lower.Contains("</html>")) findings.Add("Missing </html> closing tag.");
        }

        private static void ValidateGenericCalls(string code, List<string> findings)
        {
            var definedNames = new HashSet<string>(StringComparer.Ordinal);
            var calledNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (string line in code.Split('\n'))
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("def "))
                {
                    int nameEnd = trimmed.IndexOf('(', 4);
                    if (nameEnd > 4)
                        definedNames.Add(trimmed[4..nameEnd].Trim());
                }

                if ((trimmed.Contains("void ") || trimmed.Contains("int ") || trimmed.Contains("string ") ||
                     trimmed.Contains("bool ") || trimmed.Contains("float ") || trimmed.Contains("double ") ||
                     trimmed.Contains("static ") || trimmed.Contains("function ")) && trimmed.Contains('('))
                {
                    int parenIdx = trimmed.IndexOf('(');
                    if (parenIdx > 0)
                    {
                        string beforeParen = trimmed[..parenIdx].TrimEnd();
                        int lastSpace = beforeParen.LastIndexOf(' ');
                        if (lastSpace > 0)
                            definedNames.Add(beforeParen[(lastSpace + 1)..].Trim());
                    }
                }

                int searchStart = 0;
                while (searchStart < trimmed.Length)
                {
                    int callParen = trimmed.IndexOf('(', searchStart);
                    if (callParen <= 0) break;

                    int nameStart = callParen - 1;
                    while (nameStart >= 0 && (char.IsLetterOrDigit(trimmed[nameStart]) || trimmed[nameStart] == '_'))
                        nameStart--;
                    nameStart++;

                    if (nameStart < callParen)
                    {
                        string name = trimmed[nameStart..callParen];
                        if (name.Length > 1 && !char.IsDigit(name[0]))
                            calledNames.Add(name);
                    }

                    searchStart = callParen + 1;
                }
            }

            var builtins = new HashSet<string>(StringComparer.Ordinal)
            {
                "print", "input", "len", "range", "int", "float", "str", "list", "dict", "set",
                "open", "type", "isinstance", "enumerate", "zip", "map", "filter", "sorted", "min", "max",
                "abs", "round", "sum", "any", "all", "super", "self", "cls",
                "Console", "Math", "String", "System", "File", "Path", "Directory",
                "if", "for", "while", "return", "class", "new", "var", "await", "async",
                "main", "Main", "__init__", "__name__", "WriteLine", "Write", "ReadLine",
                "println", "printf", "sprintf", "require", "module", "exports"
            };

            // Only flag undefined calls when we saw at least one definition (avoids noise on prose).
            if (definedNames.Count == 0)
                return;

            foreach (string call in calledNames)
            {
                if (!definedNames.Contains(call) && !builtins.Contains(call))
                    findings.Add($"Function '{call}()' is called but not defined in the output.");
            }

            string trimmedCode = code.TrimEnd();
            if (trimmedCode.Length == 0)
                return;

            char lastChar = trimmedCode[^1];
            int openBraces = trimmedCode.Count(c => c == '{');
            int closeBraces = trimmedCode.Count(c => c == '}');
            int openParens = trimmedCode.Count(c => c == '(');
            int closeParens = trimmedCode.Count(c => c == ')');
            if (openBraces > closeBraces || openParens > closeParens)
                findings.Add("Output appears truncated: code ends with unclosed structures.");
            else if (lastChar is ',' or '+' or '-' or '*' or '/' or '=' or '&' or '|')
                findings.Add($"Output may be truncated: ends with '{lastChar}'.");
        }
    }
}
