using System.Collections.Generic;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ContextBudgetTests
    {
        [Fact]
        public void EnforceBudget_KeepsHighestPriorityBlocksFirst()
        {
            var blocks = new List<ContextBudget.Block>
            {
                new("low", "low priority block content", 10),
                new("high", "high priority block content", 100),
            };

            string result = ContextBudget.EnforceBudget(blocks, maxChars: 1000);

            int highIndex = result.IndexOf("high priority", System.StringComparison.Ordinal);
            int lowIndex = result.IndexOf("low priority", System.StringComparison.Ordinal);
            Assert.True(highIndex >= 0);
            Assert.True(lowIndex >= 0);
            Assert.True(highIndex < lowIndex);
        }

        [Fact]
        public void EnforceBudget_DropsLowestPriorityBlockWhenOverBudget()
        {
            var blocks = new List<ContextBudget.Block>
            {
                new("high", new string('a', 800), 100),
                new("low", new string('b', 800), 10),
            };

            string result = ContextBudget.EnforceBudget(blocks, maxChars: 900);

            Assert.Contains(new string('a', 800), result);
            Assert.DoesNotContain('b', result);
        }

        [Fact]
        public void EnforceBudget_TruncatesLastBlockThatPartiallyFits()
        {
            var blocks = new List<ContextBudget.Block>
            {
                new("only", new string('a', 1000), 100),
            };

            string result = ContextBudget.EnforceBudget(blocks, maxChars: 500);

            Assert.True(result.Length <= 500 + "\n[...truncated to fit context budget...]".Length);
            Assert.Contains("truncated", result);
        }

        [Fact]
        public void EnforceBudget_EmptyBlocks_ReturnsEmptyString()
        {
            string result = ContextBudget.EnforceBudget(new List<ContextBudget.Block>(), maxChars: 1000);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void EnforceBudget_SkipsBlankBlocks()
        {
            var blocks = new List<ContextBudget.Block>
            {
                new("blank", "   ", 100),
                new("real", "actual content", 10),
            };

            string result = ContextBudget.EnforceBudget(blocks, maxChars: 1000);
            Assert.Equal("actual content", result);
        }

        [Fact]
        public void CharBudgetForContextWindow_ScalesWithWindowSize()
        {
            int small = ContextBudget.CharBudgetForContextWindow(9216);
            int large = ContextBudget.CharBudgetForContextWindow(131072);

            Assert.True(small > 0);
            Assert.True(large > small);
        }
    }
}
