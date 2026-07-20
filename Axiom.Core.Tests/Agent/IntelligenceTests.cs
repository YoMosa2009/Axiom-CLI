using System;
using System.Collections.Generic;
using System.IO;
using Axiom.Core.Agent;
using Axiom.Core.Chat;
using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class IntelligenceTests
    {
        [Fact]
        public void RepoMap_IncludesRootAndMarkers()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-map-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "README.md"), "# hi");
            File.WriteAllText(Path.Combine(dir, "Program.cs"), "class Program { static void Main() {} }\n");
            try
            {
                string map = RepoMapService.Build(dir);
                Assert.Contains("REPO MAP", map);
                Assert.Contains("README.md", map);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void RepoRetrieval_FindsKeywordChunk()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-ret-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "auth.cs"), "namespace App { class LoginService { public void Authenticate() {} } }\n");
            try
            {
                string hit = RepoRetrievalService.Retrieve(dir, "Authenticate login");
                Assert.Contains("REPO RETRIEVAL", hit);
                Assert.Contains("auth.cs", hit, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ArchitectValidation_RejectsEmptyAndVague()
        {
            Assert.NotEmpty(IntelligenceHelpers.ArchitectValidationError("", CouncilTaskKind.Coding, true));
            Assert.NotEmpty(IntelligenceHelpers.ArchitectValidationError("do stuff", CouncilTaskKind.Coding, true));
            string ok = "1. Edit Program.cs\n";
            Assert.Empty(IntelligenceHelpers.ArchitectValidationError(ok, CouncilTaskKind.Coding, true));
            string multi = "1. Edit src/App.cs to add validation\n2. Add unit test in tests/AppTests.cs\n";
            Assert.Empty(IntelligenceHelpers.ArchitectValidationError(multi, CouncilTaskKind.Coding, true));
        }

        [Fact]
        public void DetectSpecialty_DebugAndReview()
        {
            Assert.Equal(TaskSpecialty.Debug, IntelligenceHelpers.DetectSpecialty("fix the crash in login"));
            Assert.Equal(TaskSpecialty.Review, IntelligenceHelpers.DetectSpecialty("please code review this PR"));
            Assert.Equal(TaskSpecialty.Greenfield, IntelligenceHelpers.DetectSpecialty("scaffold a new project from scratch"));
        }

        [Fact]
        public void ConversationCompactor_TrimsWhenHuge()
        {
            var history = new List<OpenRouterMessage>();
            for (int i = 0; i < 20; i++)
            {
                history.Add(new OpenRouterMessage("user", "question " + i + " " + new string('x', 200)));
                history.Add(new OpenRouterMessage("assistant", "answer " + i + " " + new string('y', 2000)));
            }
            var result = ConversationCompactor.Compact(history, estimatedTokens: 60_000, contextWindowTokens: 128_000);
            Assert.True(result.Compacted);
            Assert.True(result.Messages.Count < history.Count);
            Assert.Contains(result.Messages, m => (m.Text ?? "").Contains("COMPACTED"));
        }

        [Fact]
        public void SpecFromChat_BuildsMarkdown()
        {
            var history = new List<OpenRouterMessage>
            {
                new("user", "Add rate limiting to the API"),
                new("assistant", "I will update Middleware/RateLimit.cs and tests."),
            };
            string spec = IntelligenceHelpers.BuildSpecMarkdown(history, "API limits");
            Assert.Contains("# API limits", spec);
            Assert.Contains("rate limiting", spec, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Acceptance criteria", spec);
        }

        [Fact]
        public void RegressionGuard_TracksFailures()
        {
            var wf = new SessionWorkflowState();
            wf.NoteFailedTest("LoginTests.Fails");
            Assert.Single(wf.FailedTests);
            Assert.Contains("REGRESSION GUARD", wf.RegressionGuardBlock());
            wf.NoteTestsPassedClear("LoginTests");
            Assert.Empty(wf.FailedTests);
        }

        [Fact]
        public void CriticEvidence_MarksMissingEvidence()
        {
            var report = new CriticReport
            {
                Status = "issues",
                Issues = new List<CriticIssue>
                {
                    new() { Summary = "bug", Evidence = "", Severity = "high" }
                }
            };
            var enforced = IntelligenceHelpers.EnforceCriticEvidence(report, codingTask: true, hadSandboxOrStatic: false);
            Assert.Contains("missing file:line", enforced.Issues![0].Evidence, StringComparison.OrdinalIgnoreCase);
        }
    }
}
