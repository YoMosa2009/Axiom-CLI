using System;
using System.IO;
using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class ArtifactQualityInspectorTests
    {
        [Fact]
        public void Inspect_FlagsMissingExactRequestedLiteralAndIncludesActualEvidence()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-quality-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "index.html");
            string html =
                "<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width\">" +
                "<style>body { display: grid; }</style></head><body><h1>Wrong headline</h1></body></html>";
            File.WriteAllText(path, html);
            try
            {
                GoalContract goal = GoalContract.FromPrompt(
                    "Create the page with the exact headline “The AI Lab for local-first intelligence.”");

                ArtifactQualitySnapshot snapshot = ArtifactQualityInspector.Inspect([path], goal);

                Assert.Contains(snapshot.Findings, finding =>
                    finding.Contains("REQUIREMENT COVERAGE", StringComparison.OrdinalIgnoreCase));
                Assert.Contains("Wrong headline", snapshot.EvidenceBlock);
                Assert.Contains(path, snapshot.EvidenceBlock);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }
    }
}
