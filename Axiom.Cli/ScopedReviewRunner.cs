using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Axiom.Core;
using Axiom.Core.OpenCode;
using Axiom.Core.Persistence;

namespace Axiom.Cli;

internal static class ScopedReviewRunner
{
    internal static async Task<int> RunAsync(string[] args)
    {
        bool planOnly = args.Contains("--plan");
        args = args.Where(a => a != "--plan").ToArray();
        string task = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(task)) task = "Find correctness bugs and explain this source's responsibilities.";
        if (task.Length > 4000) throw new ArgumentException("Review task must be at most 4000 characters.");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            string root = (await GitAsync(["rev-parse", "--show-toplevel"], cancellation.Token)).TrimEnd('\r', '\n');
            string files = await GitAsync(["-C", root, "ls-files", "-z", "--cached"], cancellation.Token);
            ReviewPlan plan = ScopedReview.Plan(root, files.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            Console.Error.WriteLine($"Scoped review: {plan.Passes.Select(p => p.Path).Distinct().Count()} files, {plan.Passes.Count} passes, {plan.Omitted.Count} omitted. Tracked working-tree files only.");
            if (planOnly)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    root,
                    passes = plan.Passes.Select(p => new { p.Path, p.StartLine, p.EndLine, characters = p.Content.Length }),
                    plan.Omitted
                }, new JsonSerializerOptions { WriteIndented = true }));
                return plan.Passes.Count == 0 ? 1 : 0;
            }
            if (plan.Passes.Count == 0) return 1;
            using var db = new DatabaseService();
            string baseUrl = db.GetSetting(DatabaseService.CustomEndpointBaseUrlSettingKey);
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = KestrelOpenCodeConfiguration.DefaultBaseUrl;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https")
                throw new InvalidOperationException("Review requires an HTTPS Kestrel endpoint.");
            string key = Environment.GetEnvironmentVariable("AXIOM_CLI_KESTREL_API_KEY") ?? db.LoadCustomEndpointApiKey() ?? "";
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No readable Kestrel key. Run 'axiom connect'.");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var current = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models/current");
            current.Headers.Authorization = new("Bearer", key);
            using var active = await http.SendAsync(current, cancellation.Token);
            active.EnsureSuccessStatusCode();
            using var profile = JsonDocument.Parse(await active.Content.ReadAsStringAsync(cancellation.Token));
            string model = profile.RootElement.GetProperty("model").GetString()
                ?? throw new InvalidDataException("Server did not identify its current model.");
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidDataException("Server returned an empty model ID.");

            string directory = Path.Combine(AppPaths.Root, "reviews", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string report = Path.Combine(directory, "review.json");
            var results = new List<object>();
            void Save() => File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                root, model, task, plannedPasses = plan.Passes.Count,
                attemptedPasses = results.Count,
                unattempted = plan.Passes.Skip(results.Count).Select(p => new { p.Path, p.StartLine, p.EndLine }),
                coverageMeaning = "Source supplied to the model, not proof of comprehension. Independent excerpts; cross-file behavior is not validated.",
                plan.Omitted, results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Save();
            Console.Error.WriteLine($"Report: {report}");
            int failures = 0;
            foreach (var pass in plan.Passes)
            {
                if (cancellation.IsCancellationRequested) break;
                Console.Error.WriteLine($"Pass {results.Count + 1}/{plan.Passes.Count}: {pass.Path}:{pass.StartLine}-{pass.EndLine}");
                string? answer = null;
                string? error = null;
                try { answer = await ScopedReview.RunPassAsync(http, baseUrl, key, model, task, pass, cancellation.Token); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                {
                    // Do not persist response bodies, which may contain server internals.
                    error = ex is HttpRequestException h ? $"HTTP request failed ({h.StatusCode})" : ex.GetType().Name;
                    failures++;
                }
                results.Add(new { pass.Path, pass.StartLine, pass.EndLine,
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pass.Content))), answer, error });
                Save();
                // A failing endpoint must not turn a large inventory into hundreds of retries.
                if (error != null) break;
            }
            Console.WriteLine(report);
            return failures == 0 && results.Count == plan.Passes.Count ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<string> GitAsync(string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        string result = await output;
        string detail = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git inventory failed: " + detail);
        return result;
    }
}
