using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class ToolCallProgressTrackerTests
    {
        [Fact]
        public void Evaluate_BlocksSameReadAtSameWorkspaceState_EvenWhenJsonPropertyOrderDiffers()
        {
            var tracker = new ToolCallProgressTracker();

            ToolActionDecision first = tracker.Evaluate("read_file", "{\"path\":\"src/app.cs\",\"limit\":80}");
            ToolActionDecision repeated = tracker.Evaluate("READ_FILE", "{\"limit\":80,\"path\":\"src/app.cs\"}");

            Assert.True(first.ShouldExecute);
            Assert.False(repeated.ShouldExecute);
            Assert.Contains("already attempted", repeated.Feedback!);
        }

        [Fact]
        public void RecordExecution_AllowsRereadAfterSuccessfulWorkspaceChange()
        {
            var tracker = new ToolCallProgressTracker();
            Assert.True(tracker.Evaluate("read_file", "{\"path\":\"src/app.cs\"}").ShouldExecute);

            tracker.RecordExecution("write_file", "{\"path\":\"src/app.cs\",\"content\":\"new\"}", "Wrote src/app.cs");

            Assert.True(tracker.Evaluate("read_file", "{\"path\":\"src/app.cs\"}").ShouldExecute);
        }

        [Fact]
        public void RecordExecution_DoesNotAdvanceStateForFailedMutation()
        {
            var tracker = new ToolCallProgressTracker();
            Assert.True(tracker.Evaluate("write_file", "{\"path\":\"src/app.cs\",\"content\":\"x\"}").ShouldExecute);

            tracker.RecordExecution("write_file", "{\"path\":\"src/app.cs\",\"content\":\"x\"}", "Tool error (write_file): access denied");

            Assert.False(tracker.Evaluate("write_file", "{\"content\":\"x\",\"path\":\"src/app.cs\"}").ShouldExecute);
        }

        [Fact]
        public void RecordExecution_BlocksAnIdenticalSuccessfulMutation()
        {
            var tracker = new ToolCallProgressTracker();
            const string write = "{\"path\":\"src/app.cs\",\"content\":\"new\"}";
            Assert.True(tracker.Evaluate("write_file", write).ShouldExecute);

            tracker.RecordExecution("write_file", write, "Wrote src/app.cs");

            Assert.False(tracker.Evaluate("write_file", "{\"content\":\"new\",\"path\":\"src/app.cs\"}").ShouldExecute);
        }
    }
}
