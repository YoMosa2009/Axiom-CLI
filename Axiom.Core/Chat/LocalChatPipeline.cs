using System;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.Core.Chat
{
    // Seam for local GGUF inference (see plan milestone M4). The WPF app backs this with
    // LLamaSharp + native llama.cpp bindings; wiring that up cross-platform (Windows/macOS/Linux,
    // CPU/GPU backends) is real, separate work that's explicitly deferred — this stub exists so
    // callers can select IChatPipeline by config today and the CLI fails with a clear, actionable
    // message instead of silently behaving like cloud mode or crashing on a missing service.
    public sealed class LocalChatPipeline : IChatPipeline
    {
        public Task<ChatPipelineResult> ExecuteAsync(
            ChatPipelineRequest request,
            Action<string>? onToken,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException(
                "Local model inference is not yet available in the Axiom CLI. Use cloud mode " +
                "(configure an OpenRouter API key with 'axiom config') until local inference ships.");
        }
    }
}
