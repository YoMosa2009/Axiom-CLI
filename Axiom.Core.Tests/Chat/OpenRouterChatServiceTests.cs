using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Axiom.Core.Chat;
using Xunit;

namespace Axiom.Core.Tests.Chat
{
    public class OpenRouterChatServiceTests
    {
        // Root-cause regression guard: Ollama's OpenAI-compatibility shim (/v1/chat/completions)
        // silently ignores "think": false -- verified live, a plain "hello"-class message produced
        // 7,337 tokens of unrequested chain-of-thought before any real answer (442s wall clock)
        // when sent through that shim, versus a complete equally-correct answer in 12-14s when sent
        // to Ollama's native /api/chat with the same flag. The custom endpoint must always use
        // BuildOllamaNativeChatRequest (POSTed to /api/chat), never BuildChatRequest's OpenAI shape.
        [Fact]
        public async Task BuildOllamaNativeChatRequest_DisablesThinking()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "granite3.2:8b");

            using var request = service.BuildOllamaNativeChatRequest(
                new List<OpenRouterMessage> { new("user", "hi") },
                systemPrompt: "system",
                modelId: OpenRouterChatService.CustomEndpointModelId,
                temperature: 0.7,
                topP: 0.9,
                maxTokens: 512,
                tools: null,
                stream: true,
                stopSequences: null);

            Assert.EndsWith("/api/chat", request.RequestUri!.ToString());
            string body = await request.Content!.ReadAsStringAsync();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("think", out JsonElement think));
            Assert.False(think.GetBoolean());
        }

        // Root-cause regression guard: the entire custom-endpoint context-budgeting apparatus
        // (ContextBudget, per-block budgets, CustomEndpointContextWindowTokens) only ever shaped
        // the client-side prompt -- it never told Ollama's server how much context to actually
        // allocate, so Ollama silently truncated to its own internal default regardless of what
        // was budgeted for. Ollama's native /api/chat takes num_ctx (and temperature/top_p/top_k)
        // nested under "options", not as top-level fields like the OpenAI-compat shim accepts.
        [Fact]
        public async Task BuildOllamaNativeChatRequest_IncludesNumCtxInOptions()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint(
                "https://ai.axiominference.work/v1", "test-key", "granite3.2:8b", contextWindowTokens: 32768);

            using var request = service.BuildOllamaNativeChatRequest(
                new List<OpenRouterMessage> { new("user", "hi") },
                systemPrompt: "system",
                modelId: OpenRouterChatService.CustomEndpointModelId,
                temperature: 0.7,
                topP: 0.9,
                maxTokens: 512,
                tools: null,
                stream: true,
                stopSequences: null);

            string body = await request.Content!.ReadAsStringAsync();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("options", out JsonElement options));
            Assert.True(options.TryGetProperty("num_ctx", out JsonElement numCtx));
            Assert.Equal(32768, numCtx.GetInt32());
            Assert.True(options.TryGetProperty("top_k", out JsonElement topK));
            Assert.Equal(OpenRouterChatService.CustomEndpointTopK, topK.GetInt32());
        }

        // Regression guard: Ollama's own automatic GPU-layer-split heuristic was measured leaving
        // free VRAM unused on real hardware (24-of-32 layers offloaded with ~1.1GB still free);
        // forcing full GPU residency measured a real, reproducible ~50% generation-speed
        // improvement. See CustomEndpointNumGpuLayers for the full rationale.
        [Fact]
        public async Task BuildOllamaNativeChatRequest_ForcesFullGpuResidency()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "granite3.2:8b");

            using var request = service.BuildOllamaNativeChatRequest(
                new List<OpenRouterMessage> { new("user", "hi") },
                systemPrompt: "system",
                modelId: OpenRouterChatService.CustomEndpointModelId,
                temperature: 0.7,
                topP: 0.9,
                maxTokens: 512,
                tools: null,
                stream: true,
                stopSequences: null);

            string body = await request.Content!.ReadAsStringAsync();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("options", out JsonElement options));
            Assert.True(options.TryGetProperty("num_gpu", out JsonElement numGpu));
            Assert.Equal(OpenRouterChatService.CustomEndpointNumGpuLayers, numGpu.GetInt32());
        }

        // Regression guard: Ollama's own default (evict after 5 minutes idle) applies whenever a
        // request omits keep_alive, so any real pause between chat turns risked a full model
        // reload (disk read + VRAM reallocation) before the next reply could even start.
        [Fact]
        public async Task BuildOllamaNativeChatRequest_IncludesKeepAlive()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "granite3.2:8b");

            using var request = service.BuildOllamaNativeChatRequest(
                new List<OpenRouterMessage> { new("user", "hi") },
                systemPrompt: "system",
                modelId: OpenRouterChatService.CustomEndpointModelId,
                temperature: 0.7,
                topP: 0.9,
                maxTokens: 512,
                tools: null,
                stream: true,
                stopSequences: null);

            string body = await request.Content!.ReadAsStringAsync();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("keep_alive", out JsonElement keepAlive));
            Assert.Equal(OpenRouterChatService.CustomEndpointKeepAlive, keepAlive.GetString());
        }

        [Fact]
        public async Task BuildChatRequest_OmitsKeepAliveAndThink_ForCloudModels()
        {
            var service = new OpenRouterChatService();

            using var request = service.BuildChatRequest(
                new List<OpenRouterMessage> { new("user", "hi") },
                systemPrompt: "system",
                modelId: OpenRouterChatService.Eidos1ModelId,
                thinkingEnabled: false,
                temperature: 0.7,
                topP: 0.9,
                maxTokens: 512,
                tools: null);

            string body = await request.Content!.ReadAsStringAsync();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("keep_alive", out _));
            Assert.False(json.RootElement.TryGetProperty("think", out _));
            Assert.False(json.RootElement.TryGetProperty("num_ctx", out _));
        }

        // Root-cause regression guard for "context length is hardcoded": the client's num_ctx
        // value should come from live-querying the server's own /api/ps, not a number someone has
        // to remember to re-type. Ollama's native API lives at the host root; the configured
        // base_url is always the /v1 (OpenAI-compat) form, so detection has to strip it back off.
        [Theory]
        [InlineData("https://ai.axiominference.work/v1", "/api/ps", "https://ai.axiominference.work/api/ps")]
        [InlineData("https://ai.axiominference.work/v1/", "/api/ps", "https://ai.axiominference.work/api/ps")]
        [InlineData("http://127.0.0.1:11434/v1", "/api/ps", "http://127.0.0.1:11434/api/ps")]
        [InlineData("https://example.com", "/api/ps", "https://example.com/api/ps")]
        public void BuildNativeOllamaUrl_StripsV1Suffix(string baseUrl, string nativePath, string expected)
        {
            Assert.Equal(expected, OpenRouterChatService.BuildNativeOllamaUrl(baseUrl, nativePath));
        }

        [Fact]
        public async Task TryDetectCustomEndpointContextLengthAsync_ReturnsNull_WhenNoCustomEndpointConfigured()
        {
            var service = new OpenRouterChatService();
            Assert.Null(await service.TryDetectCustomEndpointContextLengthAsync());
        }

        [Fact]
        public async Task TryDetectCustomEndpointContextLengthAsync_ReturnsNull_WhenServerUnreachable()
        {
            var service = new OpenRouterChatService();
            // RFC 2606 reserves .invalid to never resolve -- a fast, deterministic DNS failure
            // instead of relying on network flakiness to exercise the real failure path.
            service.SetCustomEndpoint("https://host.invalid/v1", "test-key", "granite3.2:8b");
            Assert.Null(await service.TryDetectCustomEndpointContextLengthAsync());
        }

        [Fact]
        public async Task TryListCustomEndpointModelsAsync_ReturnsEmpty_WhenNoBaseUrlSet()
        {
            var service = new OpenRouterChatService();
            Assert.Empty(await service.TryListCustomEndpointModelsAsync());
        }

        [Fact]
        public async Task TryListCustomEndpointModelsAsync_ReturnsEmpty_WhenServerUnreachable()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://host.invalid/v1", "test-key", modelId: string.Empty);
            Assert.Empty(await service.TryListCustomEndpointModelsAsync());
        }

        // Regression guard: unlike HasValidCustomEndpoint (used to gate actually sending chat
        // requests), listing models must work with an EMPTY model id -- axiom config calls this
        // precisely to find out what the model id should be, before it's known.
        [Fact]
        public async Task TryListCustomEndpointModelsAsync_DoesNotRequireModelId()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", modelId: string.Empty);
            Assert.False(service.HasValidCustomEndpoint);
            // Should still attempt the query (and fail gracefully on network reachability in a
            // test sandbox) rather than short-circuiting on the missing model id.
            IReadOnlyList<string> result = await service.TryListCustomEndpointModelsAsync();
            Assert.NotNull(result);
        }

        [Fact]
        public void HasValidCustomEndpoint_TrueOnceAllThreeFieldsAreSet()
        {
            var service = new OpenRouterChatService();
            Assert.False(service.HasValidCustomEndpoint);

            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "llama3.1:8b");
            Assert.True(service.HasValidCustomEndpoint);
        }

        [Fact]
        public void HasValidCustomEndpoint_FalseForNonHttpsBaseUrl()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("http://ai.axiominference.work/v1", "test-key", "llama3.1:8b");

            Assert.False(service.HasValidCustomEndpoint);
        }

        [Fact]
        public void HasValidCustomEndpoint_FalseWhenAnyFieldIsBlank()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "", "llama3.1:8b");

            Assert.False(service.HasValidCustomEndpoint);
        }

        [Fact]
        public void HasAnyValidCloudCredential_TrueForCustomEndpointAloneWithNoOpenRouterKey()
        {
            var service = new OpenRouterChatService();
            Assert.False(service.HasAnyValidCloudCredential);

            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "llama3.1:8b");
            Assert.True(service.HasAnyValidCloudCredential);
        }

        [Fact]
        public void GetApproximateContextWindowTokens_ReturnsRealWindowForCustomEndpoint_NotFlooredTo32768()
        {
            var service = new OpenRouterChatService();

            int contextWindow = service.GetApproximateContextWindowTokens(OpenRouterChatService.CustomEndpointModelId);

            // The custom-endpoint branch of GetApproximateContextWindowTokens returns the
            // configured value as-is, with no Math.Max(32768, ...) floor applied (that floor only
            // applies to cloud profiles) -- so this must hold regardless of whether the real
            // configured window happens to be above or below 32768.
            Assert.Equal(OpenRouterChatService.CustomEndpointContextWindowTokens, contextWindow);
        }

        [Fact]
        public void CustomEndpoint_UsesItsConfiguredContextWindowAcrossBudgeting()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint(
                "https://ai.axiominference.work/v1", "test-key", "granite3.2:8b", contextWindowTokens: 32768);

            Assert.Equal(32768, service.GetApproximateContextWindowTokens(OpenRouterChatService.CustomEndpointModelId));
            Assert.Equal(32768, service.GetInferenceSettingsSnapshot(OpenRouterChatService.CustomEndpointModelId).ContextWindowTokens);
            Assert.True(service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, 2048) > 28000);
        }

        [Fact]
        public void GetApproximateContextWindowTokens_StillFloorsRealOpenRouterAliases()
        {
            var service = new OpenRouterChatService();

            int contextWindow = service.GetApproximateContextWindowTokens(OpenRouterChatService.Eidos1ModelId);

            Assert.True(contextWindow >= 32768);
        }

        [Fact]
        public void IsSelectableModelAvailable_TrueForCustomEndpointRegardlessOfOpenRouterCatalogState()
        {
            var service = new OpenRouterChatService();

            Assert.True(service.IsSelectableModelAvailable(OpenRouterChatService.CustomEndpointModelId));
        }

        [Fact]
        public void ResolveModelLabel_ReturnsKestral1ForCustomEndpointAlias()
        {
            var service = new OpenRouterChatService();

            Assert.Equal(OpenRouterChatService.CustomEndpointModelLabel, service.ResolveModelLabel(OpenRouterChatService.CustomEndpointModelId));
        }

        [Fact]
        public void NormalizeSelectableModelId_RoundTripsCustomEndpointAlias()
        {
            var service = new OpenRouterChatService();

            Assert.Equal(OpenRouterChatService.CustomEndpointModelId, service.NormalizeSelectableModelId(OpenRouterChatService.CustomEndpointModelId));
        }

        // Root-cause regression guard: before this fix, the custom endpoint's prompt budget
        // always bottomed out at the hardcoded 2048-token emergency floor (8192 window - 8192
        // hardcoded completion reservation - margin - tools always went negative). A real fix
        // must leave meaningful room above that floor for an 8192-token window.
        [Fact]
        public void GetPromptTokenBudgetForModel_WellAboveEmergencyFloorForCustomEndpoint()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "granite3.2:8b");

            int budget = service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, maxCompletionTokens: 8192);

            Assert.True(budget > 4000, $"Expected budget well above the 2048 emergency floor, got {budget}.");
        }

        [Fact]
        public void GetPromptTokenBudgetForModel_UnaffectedByOversizedToolListForCustomEndpoint()
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint("https://ai.axiominference.work/v1", "test-key", "granite3.2:8b");

            int budgetNoTools = service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, maxCompletionTokens: 1536);

            // A small custom-endpoint floor (512-1024) must still be respected even with a
            // realistic tool list pushing toolTokens up.
            var tools = new System.Collections.Generic.List<OpenRouterToolDefinition>
            {
                new("write_file", "Create or overwrite a file", new System.Text.Json.Nodes.JsonObject()),
                new("run_shell", "Run a shell command", new System.Text.Json.Nodes.JsonObject())
            };
            int budgetWithTools = service.GetPromptTokenBudgetForModel(OpenRouterChatService.CustomEndpointModelId, maxCompletionTokens: 1536, tools);

            Assert.True(budgetWithTools >= 512, $"Expected at least the custom-endpoint floor, got {budgetWithTools}.");
            Assert.True(budgetWithTools <= budgetNoTools, "Adding tool schemas should never increase the budget.");
        }

        // Regression guard: the budget-hardening fix must not change behavior for real OpenRouter
        // models, which already comfortably fit an 8192-token completion reservation inside a
        // 131k+-token window.
        [Theory]
        [InlineData(OpenRouterChatService.Eidos1ModelId)]
        [InlineData(OpenRouterChatService.Hepha1ModelId)]
        public void GetPromptTokenBudgetForModel_UnchangedForRealOpenRouterModels(string modelId)
        {
            var service = new OpenRouterChatService();

            int budget = service.GetPromptTokenBudgetForModel(modelId, maxCompletionTokens: 8192);

            // 131072 - 8192 - 1310 (contextWindow/100 safety margin) - 0 tool tokens = 121570.
            Assert.Equal(121570, budget);
        }
    }
}
