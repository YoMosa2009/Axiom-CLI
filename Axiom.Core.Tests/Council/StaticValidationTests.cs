using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class StaticValidationTests
    {
        [Fact]
        public void DetectLanguage_PythonDef_ReturnsPython()
        {
            string code = "def add(a, b):\n    return a + b\n\nprint(add(1, 2))\n";
            Assert.Equal("python", StaticValidation.DetectLanguage(code));
        }

        [Fact]
        public void DetectLanguage_FencedJava_ReturnsJava()
        {
            string code = "```java\npublic class Main {\n  public static void main(String[] args) {}\n}\n```";
            Assert.Equal("java", StaticValidation.DetectLanguage(code));
        }

        [Fact]
        public void Run_MismatchedBraces_FlagsFinding()
        {
            string code = "public class Broken {\n  void m() {\n    int x = 1;\n";
            var findings = StaticValidation.Run(code);
            Assert.Contains(findings, f => f.Contains("Mismatched braces", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Run_PythonMixedIndent_FlagsFinding()
        {
            string code = "def f():\n\treturn 1\n def g():\n  return 2\n";
            // First line body uses tab; second def line uses space indent at column 0 for def itself —
            // ensure at least one line starts with tab and one with space among non-empty body lines.
            code = "def f():\n\tx = 1\ndef g():\n y = 2\n";
            var findings = StaticValidation.Run(code);
            Assert.Contains(findings, f => f.Contains("Mixed indentation", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void DetectSandboxErrors_Traceback_Critical()
        {
            string output = "Traceback (most recent call last):\n  File \"x.py\", line 1\nNameError: name 'foo' is not defined\n";
            var findings = StaticValidation.DetectSandboxErrors(output);
            Assert.NotEmpty(findings);
            Assert.Contains(findings, f => f.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase)
                || f.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ExtractCodeBlock_PrefersMatchingFence()
        {
            string content = "Here is prose.\n```python\nprint(1)\n```\n```java\nclass X {}\n```\n";
            string extracted = StaticValidation.ExtractCodeBlock(content, "python");
            Assert.Contains("print(1)", extracted);
            Assert.DoesNotContain("class X", extracted);
        }

        [Fact]
        public void LooksLikeCode_MarkdownProse_False()
        {
            Assert.False(StaticValidation.LooksLikeCode("The capital of France is Paris."));
        }

        [Fact]
        public void RunArtifactChecks_UnstyledFullHtml_FlagsBrowserDefaultOutput()
        {
            string html = "<!doctype html><html><head><title>Demo</title></head><body><h1>Demo</h1></body></html>";

            var findings = StaticValidation.RunArtifactChecks(html);

            Assert.Contains(findings, f => f.Contains("no stylesheet", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(findings, f => f.Contains("viewport", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void RunArtifactChecks_StyledResponsiveHtml_Passes()
        {
            string html = "<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width\"><style>body { color: #eee; }</style></head><body></body></html>";

            Assert.Empty(StaticValidation.RunArtifactChecks(html));
        }

        [Fact]
        public void RunArtifactChecks_CSharpArtifact_UsesGeneralStructuralValidation()
        {
            string code = "public class Broken { void Run() {";

            var findings = StaticValidation.RunArtifactChecks(code);

            Assert.Contains(findings, f => f.Contains("Mismatched braces", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void RunArtifactChecks_InvalidJson_FlagsConfigurationArtifact()
        {
            var findings = StaticValidation.RunArtifactChecks("{\"enabled\":}", "settings.json");

            Assert.Contains(findings, f => f.Contains("Invalid JSON", StringComparison.OrdinalIgnoreCase));
        }
    }
}
