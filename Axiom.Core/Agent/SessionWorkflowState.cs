using System;

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
    }
}
