using System.Net;
using System.Text;
using System.Text.Json;
using Axiom.Core.OpenCode;

namespace Axiom.Core.Tests.OpenCode;

public sealed class ScopedReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "axiom-review-test-" + Guid.NewGuid().ToString("N"));
    public ScopedReviewTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void LargeFileIsCoveredExactlyOnceIncludingLongLines()
    {
        string source = string.Concat(Enumerable.Repeat("public void Work() {}\n", 21401)) + new string('x', 25000);
        File.WriteAllText(Path.Combine(root, "Workplace.xaml.cs"), source);
        var plan = ScopedReview.Plan(root, ["Workplace.xaml.cs", "Workplace.xaml.cs"]);
        Assert.Empty(plan.Omitted);
        Assert.Equal(source, string.Concat(plan.Passes.Select(p => p.Content)));
        Assert.All(plan.Passes, p => Assert.InRange(p.Content.Length, 1, ScopedReview.PassCharacters));
        Assert.Equal(1, plan.Passes[0].StartLine);
        Assert.Equal(21402, plan.Passes[^1].EndLine);
    }

    [Fact]
    public void OmissionsAreExplicitAndPathsCannotEscapeRoot()
    {
        File.WriteAllText(Path.Combine(root, "binary.cs"), "a\0b");
        File.WriteAllBytes(Path.Combine(root, "invalid.cs"), [0xff, 0xff]);
        File.WriteAllText(Path.Combine(root, "empty.cs"), "");
        var plan = ScopedReview.Plan(root, ["../outside.cs", ".env", "binary.cs", "missing.cs", "invalid.cs", "empty.cs"]);
        Assert.Empty(plan.Passes);
        Assert.Equal(6, plan.Omitted.Count);
    }

    [Fact]
    public void OrderingAndLineRangesAreDeterministic()
    {
        File.WriteAllText(Path.Combine(root, "a.cs"), "a\nb\n");
        File.WriteAllText(Path.Combine(root, "b.cs"), "c");
        var plan = ScopedReview.Plan(root, ["b.cs", "a.cs"]);
        Assert.Equal("a.cs", plan.Passes[0].Path);
        Assert.Equal(2, plan.Passes[0].EndLine);
        Assert.Equal(1, plan.Passes[1].EndLine);
    }

    [Theory]
    [InlineData("length", "partial")]
    [InlineData("stop", "")]
    [InlineData("tool_calls", "")]
    public async Task IncompleteResponsesFail(string finish, string content)
    {
        using var http = new HttpClient(new ReplyHandler(finish, content));
        await Assert.ThrowsAsync<InvalidDataException>(() => ScopedReview.RunPassAsync(http, "https://example.test/v1",
            "test-key", "gemma4:12b", "review", new("a.cs", 1, 1, "source"), default));
    }

    [Fact]
    public async Task RequestUsesActiveModelSamplingAndSuppliedSourceWithoutTools()
    {
        var handler = new ReplyHandler("stop", "No finding.");
        using var http = new HttpClient(handler);
        string result = await ScopedReview.RunPassAsync(http, "https://example.test/v1", "test-key",
            "gemma4:12b", "review", new("a.cs", 10, 11, "source"), default);
        Assert.Equal("No finding.", result);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(0.6, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("gemma4:12b", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.Contains("source", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    private sealed class ReplyHandler(string finish, string content) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = finish, message = new { content } } } }), Encoding.UTF8, "application/json") };
        }
    }
}
