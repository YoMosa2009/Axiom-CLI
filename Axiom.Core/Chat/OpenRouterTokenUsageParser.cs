using System;
using System.Globalization;
using System.Text.Json;

namespace Axiom.Core.Chat
{
    public static class OpenRouterTokenUsageParser
    {
        public static OpenRouterTokenUsage? TryParse(JsonElement root)
        {
            JsonElement usage = root;
            if (TryGetPropertyIgnoreCase(root, "usage", out JsonElement nestedUsage))
                usage = nestedUsage;
            if (usage.ValueKind != JsonValueKind.Object)
                return null;

            bool hasPrompt = TryReadTokenCount(usage, out int promptTokens,
                "prompt_tokens", "prompt_token_count", "input_tokens", "prompt_eval_count");
            bool hasCompletion = TryReadTokenCount(usage, out int completionTokens,
                "completion_tokens", "completion_token_count", "output_tokens", "eval_count");
            bool hasTotal = TryReadTokenCount(usage, out int totalTokens,
                "total_tokens", "total_token_count");
            if (!hasPrompt && !hasCompletion && !hasTotal)
                return null;

            if (!hasTotal)
                totalTokens = promptTokens + completionTokens;
            if (promptTokens == 0 && completionTokens == 0 && totalTokens == 0)
                return null;

            return new OpenRouterTokenUsage(promptTokens, completionTokens, totalTokens);
        }

        private static bool TryReadTokenCount(JsonElement usage, out int value, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                if (TryReadTokenCount(usage, propertyName, out value))
                    return true;
            }

            value = 0;
            return false;
        }

        private static bool TryReadTokenCount(JsonElement usage, string propertyName, out int value)
        {
            value = 0;
            if (!TryGetPropertyIgnoreCase(usage, propertyName, out JsonElement element))
                return false;

            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt32(out int intValue))
                {
                    value = Math.Max(0, intValue);
                    return true;
                }

                if (element.TryGetInt64(out long longValue))
                {
                    value = (int)Math.Clamp(longValue, 0, int.MaxValue);
                    return true;
                }
            }

            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                value = Math.Max(0, parsed);
                return true;
            }

            return false;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }
    }
}
