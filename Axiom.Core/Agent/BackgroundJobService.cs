using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Agent
{
    public enum BackgroundJobState
    {
        Running,
        Completed,
        Failed,
        Cancelled
    }

    public sealed class BackgroundJob
    {
        public string Id { get; set; } = "";
        public string Command { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public BackgroundJobState State { get; set; } = BackgroundJobState.Running;
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public int? ExitCode { get; set; }
        public string Output { get; set; } = "";
    }

    /// <summary>
    /// Fire long shell jobs without blocking the chat TUI; poll with /jobs.
    /// </summary>
    public sealed class BackgroundJobService
    {
        private readonly ConcurrentDictionary<string, BackgroundJob> _jobs = new();
        private readonly ConcurrentQueue<string> _notifications = new();
        private const int MaxJobs = 20;
        private const int MaxOutputChars = 48_000;

        public IReadOnlyList<BackgroundJob> List()
            => _jobs.Values.OrderByDescending(j => j.StartedUtc).Take(MaxJobs).ToList();

        public string Start(string command, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "Error: command required.";

            string id = Guid.NewGuid().ToString("N")[..8];
            var job = new BackgroundJob
            {
                Id = id,
                Command = command.Trim(),
                WorkingDirectory = workingDirectory,
                State = BackgroundJobState.Running,
                StartedUtc = DateTime.UtcNow
            };
            _jobs[id] = job;

            _ = Task.Run(async () =>
            {
                try
                {
                    string shell, args;
                    if (OperatingSystem.IsWindows())
                    {
                        shell = "powershell.exe";
                        args = $"-NoProfile -Command {QuotePs(command)}";
                    }
                    else
                    {
                        shell = "/bin/bash";
                        args = $"-lc {QuoteSh(command)}";
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = shell,
                        Arguments = args,
                        WorkingDirectory = workingDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var p = new Process { StartInfo = psi };
                    var sb = new StringBuilder();
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) Append(sb, e.Data); };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) Append(sb, e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    await p.WaitForExitAsync();
                    job.ExitCode = p.ExitCode;
                    job.Output = Trim(sb.ToString());
                    job.State = p.ExitCode == 0 ? BackgroundJobState.Completed : BackgroundJobState.Failed;
                    job.FinishedUtc = DateTime.UtcNow;
                    _notifications.Enqueue($"Job {id} {(job.State == BackgroundJobState.Completed ? "finished" : "failed")} (exit {p.ExitCode}): {Truncate(command, 50)}");
                }
                catch (Exception ex)
                {
                    job.State = BackgroundJobState.Failed;
                    job.Output = ex.Message;
                    job.FinishedUtc = DateTime.UtcNow;
                    _notifications.Enqueue($"Job {id} failed: {ex.Message}");
                }
            });

            return $"Started background job {id}: {command}";
        }

        public string Status(string? id = null)
        {
            if (!string.IsNullOrWhiteSpace(id) && _jobs.TryGetValue(id.Trim(), out var one))
                return FormatJob(one, fullOutput: true);

            var list = List();
            if (list.Count == 0)
                return "No background jobs.";
            var sb = new StringBuilder();
            foreach (var j in list.Take(12))
                sb.AppendLine(FormatJob(j, fullOutput: false));
            return sb.ToString().TrimEnd();
        }

        public IReadOnlyList<string> DrainNotifications()
        {
            var list = new List<string>();
            while (_notifications.TryDequeue(out string? n) && n != null)
                list.Add(n);
            return list;
        }

        private static string FormatJob(BackgroundJob j, bool fullOutput)
        {
            string dur = j.FinishedUtc is DateTime end
                ? (end - j.StartedUtc).TotalSeconds.ToString("0.0") + "s"
                : (DateTime.UtcNow - j.StartedUtc).TotalSeconds.ToString("0.0") + "s…";
            string head = $"[{j.Id}] {j.State}  {dur}  $ {j.Command}";
            if (!fullOutput)
                return head;
            return head + "\n" + (string.IsNullOrWhiteSpace(j.Output) ? "(no output)" : j.Output);
        }

        private static void Append(StringBuilder sb, string line)
        {
            if (sb.Length > MaxOutputChars)
                return;
            sb.AppendLine(line);
        }

        private static string Trim(string s)
            => s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n...[truncated]";

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "…";

        private static string QuotePs(string s) => "'" + s.Replace("'", "''") + "'";
        private static string QuoteSh(string s) => "'" + s.Replace("'", "'\\''") + "'";
    }
}
