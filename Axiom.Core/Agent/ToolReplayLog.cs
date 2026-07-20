using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    public sealed record RecordedToolCall(
        string Name,
        string ArgumentsJson,
        DateTime Utc);

    /// <summary>
    /// Records mutating/tool calls from the last turn so the user can /replay without re-prompting.
    /// </summary>
    public sealed class ToolReplayLog
    {
        private readonly object _gate = new();
        private readonly List<RecordedToolCall> _lastTurn = new();
        private List<RecordedToolCall> _committed = new();

        public void BeginTurn()
        {
            lock (_gate) _lastTurn.Clear();
        }

        public void Record(string name, string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            // Skip pure reads to keep replay useful
            string n = name.ToLowerInvariant();
            if (n is "read_file" or "list_dir" or "search_files" or "web_search" or "git_status"
                or "git_diff" or "git_log" or "git_branch" or "worktree_list")
                return;

            lock (_gate)
            {
                _lastTurn.Add(new RecordedToolCall(name, argumentsJson ?? "{}", DateTime.UtcNow));
            }
        }

        public void CommitTurn()
        {
            lock (_gate)
            {
                if (_lastTurn.Count > 0)
                    _committed = _lastTurn.ToList();
                _lastTurn.Clear();
            }
        }

        public IReadOnlyList<RecordedToolCall> LastCommitted
        {
            get { lock (_gate) return _committed.ToList(); }
        }

        public bool HasReplay
        {
            get { lock (_gate) return _committed.Count > 0; }
        }

        public string Describe()
        {
            lock (_gate)
            {
                if (_committed.Count == 0)
                    return "No tool plan recorded yet.";
                var sb = new StringBuilder();
                sb.AppendLine($"Last tool plan ({_committed.Count} call(s)):");
                int i = 1;
                foreach (var c in _committed)
                {
                    string args = c.ArgumentsJson.Length > 80 ? c.ArgumentsJson[..77] + "…" : c.ArgumentsJson;
                    sb.AppendLine($"  {i++}. {c.Name}  {args}");
                }
                return sb.ToString().TrimEnd();
            }
        }

        public async Task<string> ReplayAsync(AgentToolExecutor executor, CancellationToken token)
        {
            List<RecordedToolCall> plan;
            lock (_gate)
                plan = _committed.ToList();

            if (plan.Count == 0)
                return "Nothing to replay.";

            var sb = new StringBuilder();
            sb.AppendLine($"Replaying {plan.Count} tool call(s)…");
            int ok = 0, fail = 0;
            foreach (var call in plan)
            {
                token.ThrowIfCancellationRequested();
                sb.AppendLine($"→ {call.Name}");
                string result = await executor.ExecuteAsync(call.Name, call.ArgumentsJson, token);
                bool err = (result ?? "").StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                    || (result ?? "").StartsWith("Denied", StringComparison.OrdinalIgnoreCase);
                if (err) fail++; else ok++;
                string preview = (result ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (preview.Length > 120)
                    preview = preview[..117] + "…";
                sb.AppendLine($"  {preview}");
            }
            sb.AppendLine($"Done. ok={ok} fail={fail}");
            return sb.ToString().TrimEnd();
        }
    }
}
