using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Chat;

namespace Axiom.Core.Tests.Council
{
    // Scripted IChatPipeline: returns responses in call order, and records every
    // (systemPrompt, userInput) pair so tests can assert on what the orchestrator actually sent.
    public sealed class FakeChatPipeline : IChatPipeline
    {
        private readonly Queue<string> _responses;
        public List<(string SystemPrompt, string UserInput)> Calls { get; } = new();

        public FakeChatPipeline(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public Task<ChatPipelineResult> ExecuteAsync(
            ChatPipelineRequest request,
            Action<string>? onToken,
            CancellationToken cancellationToken)
        {
            Calls.Add((request.SystemPrompt, request.UserMessage));
            string response = _responses.Count > 0 ? _responses.Dequeue() : "{\"status\":\"ok\",\"issues\":[]}";
            return Task.FromResult(new ChatPipelineResult(response, string.Empty));
        }
    }
}
