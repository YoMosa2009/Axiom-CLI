using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Axiom.Core.Agent
{
    public enum PlanStepStatus
    {
        Pending,
        Doing,
        Done,
        Skipped
    }

    public sealed class PlanStep
    {
        public int Index { get; set; }
        public string Text { get; set; } = "";
        public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
    }

    /// <summary>
    /// User-visible multi-step plan that Builder can check off across a turn/session.
    /// </summary>
    public sealed class PlanBoard
    {
        private readonly List<PlanStep> _steps = new();
        private readonly object _gate = new();

        public IReadOnlyList<PlanStep> Steps
        {
            get { lock (_gate) return _steps.Select(Clone).ToList(); }
        }

        public bool HasSteps
        {
            get { lock (_gate) return _steps.Count > 0; }
        }

        public void Clear()
        {
            lock (_gate) _steps.Clear();
        }

        public void SetFromArchitectPlan(string planText)
        {
            lock (_gate)
            {
                _steps.Clear();
                if (string.IsNullOrWhiteSpace(planText))
                    return;

                int i = 1;
                foreach (string raw in planText.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length < 3)
                        continue;
                    Match m = Regex.Match(line, @"^(\d+)[\.\)]\s+(.*)$");
                    if (!m.Success)
                        continue;
                    string text = m.Groups[2].Value.Trim();
                    if (text.Length == 0)
                        continue;
                    _steps.Add(new PlanStep { Index = i++, Text = text, Status = PlanStepStatus.Pending });
                    if (_steps.Count >= 12)
                        break;
                }
            }
        }

        public bool TrySetStatus(int index1Based, PlanStepStatus status)
        {
            lock (_gate)
            {
                PlanStep? step = _steps.FirstOrDefault(s => s.Index == index1Based);
                if (step == null)
                    return false;
                step.Status = status;
                return true;
            }
        }

        public bool TryMarkDone(int index1Based) => TrySetStatus(index1Based, PlanStepStatus.Done);

        public string ToDisplayBlock()
        {
            lock (_gate)
            {
                if (_steps.Count == 0)
                    return "(no plan board — Architect has not produced numbered steps yet)";

                var sb = new StringBuilder();
                sb.AppendLine("Plan board:");
                foreach (PlanStep s in _steps)
                {
                    string mark = s.Status switch
                    {
                        PlanStepStatus.Done => "[x]",
                        PlanStepStatus.Doing => "[~]",
                        PlanStepStatus.Skipped => "[-]",
                        _ => "[ ]"
                    };
                    sb.AppendLine($"  {mark} {s.Index}. {s.Text}");
                }
                int done = _steps.Count(s => s.Status == PlanStepStatus.Done);
                sb.AppendLine($"  progress: {done}/{_steps.Count}");
                return sb.ToString().TrimEnd();
            }
        }

        public string ToPromptBlock()
        {
            lock (_gate)
            {
                if (_steps.Count == 0)
                    return string.Empty;
                var sb = new StringBuilder();
                sb.AppendLine("[[PLAN BOARD — check off steps as you complete them]]");
                foreach (PlanStep s in _steps)
                {
                    string st = s.Status.ToString().ToLowerInvariant();
                    sb.AppendLine($"  {s.Index}. [{st}] {s.Text}");
                }
                sb.AppendLine("When a step is finished, call plan_board with action=done and step=<n>.");
                sb.AppendLine("[[END PLAN BOARD]]");
                return sb.ToString();
            }
        }

        private static PlanStep Clone(PlanStep s) => new()
        {
            Index = s.Index,
            Text = s.Text,
            Status = s.Status
        };
    }
}
