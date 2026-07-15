using System.Collections.Generic;

namespace Axiom.Core
{
    // Seam for local-model semantic reranking (workspace file selection, persona-memory
    // relevance). The WPF app backs this with an on-device LLamaSharp embedding model; the CLI
    // is cloud-only for v1 (see plan M4), so this stub keeps every call site that already checks
    // `IsAvailable` working unchanged — callers transparently fall back to lexical-only ranking
    // until a real local-inference backend is wired in here.
    public sealed class LocalSemanticEmbeddingService
    {
        public static LocalSemanticEmbeddingService Shared { get; } = new();

        public bool IsAvailable => false;

        public bool TryGetSimilarity(string query, string candidate, out double score)
        {
            score = 0;
            return false;
        }

        public void PrewarmInBackground(IReadOnlyList<string> texts)
        {
            // No-op until a local embedding backend is wired in (IsAvailable stays false).
        }
    }
}
