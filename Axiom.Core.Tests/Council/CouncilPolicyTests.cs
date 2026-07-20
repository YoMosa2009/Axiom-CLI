using System.Collections.Generic;
using Axiom.Core.Council;
using Xunit;

namespace Axiom.Core.Tests.Council
{
    public class CouncilPolicyTests
    {
        [Fact]
        public void Severity_CriticalOnly_FiltersLowMedium()
        {
            var issues = new List<CriticIssue>
            {
                new() { Severity = "low", Summary = "nit" },
                new() { Severity = "medium", Summary = "meh" },
                new() { Severity = "critical", Summary = "bad" },
            };
            var blocking = CriticSeverity.FilterBlocking(issues, CriticSeverityPolicy.CriticalOnly);
            Assert.Single(blocking);
            Assert.Equal("critical", blocking[0].Severity);
        }

        [Fact]
        public void Severity_HighAndAbove_IncludesHigh()
        {
            var issues = new List<CriticIssue>
            {
                new() { Severity = "low", Summary = "a" },
                new() { Severity = "high", Summary = "b" },
            };
            var blocking = CriticSeverity.FilterBlocking(issues, CriticSeverityPolicy.HighAndAbove);
            Assert.Single(blocking);
        }

        [Fact]
        public void TryParse_SeverityAliases()
        {
            Assert.True(CriticSeverity.TryParse("critical-only", out var p));
            Assert.Equal(CriticSeverityPolicy.CriticalOnly, p);
            Assert.True(CriticSeverity.TryParse("strict", out p));
            Assert.Equal(CriticSeverityPolicy.Strict, p);
        }

        [Fact]
        public void AcceptanceCriteria_MissingRoot_Empty()
        {
            Assert.Equal(string.Empty, AcceptanceCriteria.Load(null));
            Assert.Equal(string.Empty, AcceptanceCriteria.Load(@"C:\this\path\does\not\exist-axiom"));
        }
    }
}
