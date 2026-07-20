using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Axiom.Core.Agent
{
    /// <summary>
    /// Per-chat workflow state: sticky task, plan board, replay, turn changes, jobs, watch.
    /// </summary>
    public sealed class SessionWorkflowState
    {
        public PlanBoard Plan { get; } = new();
        public ToolReplayLog Replay { get; } = new();
        public TurnChangeTracker Changes { get; } = new();
        public CheckpointStore Checkpoints { get; } = new();
        public BackgroundJobService Jobs { get; } = new();
        public WorkspaceWatchService Watch { get; } = new();

        public string? StickyTask { get; set; }
        public int StickyTurnsRemaining { get; set; }

        /// <summary>When true (or auto with large write), require approval for big mutations.</summary>
        public int BigDiffLineThreshold { get; set; } = 120;
        public int BigDiffFileThreshold { get; set; } = 5;

        /// <summary>Failed test filters/names observed this session (regression guard).</summary>
        private readonly List<string> _failedTests = new();
        private readonly object _failGate = new();

        public bool AutoDiagnosticsAfterWrite { get; set; } = true;
        public bool DualPassQa { get; set; } = true;

        public void SetSticky(string task, int turns)
        {
            StickyTask = string.IsNullOrWhiteSpace(task) ? null : task.Trim();
            StickyTurnsRemaining = string.IsNullOrWhiteSpace(task) ? 0 : Math.Clamp(turns, 1, 50);
        }

        public void ClearSticky()
        {
            StickyTask = null;
            StickyTurnsRemaining = 0;
        }

        public string? ConsumeStickyPrefix()
        {
            if (string.IsNullOrWhiteSpace(StickyTask) || StickyTurnsRemaining <= 0)
                return null;
            StickyTurnsRemaining--;
            string task = StickyTask;
            if (StickyTurnsRemaining <= 0)
                ClearSticky();
            return $"[STICKY TASK — stay focused on this goal for this turn]\n{task}\n[END STICKY TASK]\n\n";
        }

        public string StickyStatus()
        {
            if (string.IsNullOrWhiteSpace(StickyTask))
                return "No sticky task. Set with /sticky <goal> [turns]";
            return $"Sticky ({StickyTurnsRemaining} turn(s) left): {StickyTask}";
        }

        public void NoteFailedTest(string nameOrFilter)
        {
            if (string.IsNullOrWhiteSpace(nameOrFilter))
                return;
            string n = nameOrFilter.Trim();
            if (n.Length > 120)
                n = n[..117] + "…";
            lock (_failGate)
            {
                if (!_failedTests.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)))
                    _failedTests.Add(n);
                while (_failedTests.Count > 20)
                    _failedTests.RemoveAt(0);
            }
        }

        public void NoteTestsPassedClear(string? filter = null)
        {
            lock (_failGate)
            {
                if (string.IsNullOrWhiteSpace(filter))
                    _failedTests.Clear();
                else
                    _failedTests.RemoveAll(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<string> FailedTests
        {
            get { lock (_failGate) return _failedTests.ToList(); }
        }

        public string? RegressionGuardBlock()
        {
            List<string> fails;
            lock (_failGate) fails = _failedTests.ToList();
            if (fails.Count == 0)
                return null;
            var sb = new StringBuilder();
            sb.AppendLine("[[REGRESSION GUARD]]");
            sb.AppendLine("These tests/filters failed earlier in the session — re-run before finishing:");
            foreach (string f in fails.Take(8))
                sb.AppendLine("  - " + f);
            sb.AppendLine("Use run_tests with filter when possible.");
            sb.AppendLine("[[END REGRESSION GUARD]]");
            return sb.ToString();
        }
    }
}
