namespace AiHarnessDemo.Services;

public static class ModelFamilyClassifier
{
    public static string Classify(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model ID is required.", nameof(model));
        }

        var normalized = model.Trim().ToLowerInvariant();
        if (normalized.StartsWith("claude", StringComparison.Ordinal))
        {
            return "anthropic";
        }
        if (normalized.StartsWith("gpt", StringComparison.Ordinal) ||
            IsOpenAiReasoningModel(normalized))
        {
            return "openai";
        }
        if (normalized.StartsWith("gemini", StringComparison.Ordinal))
        {
            return "google";
        }
        if (normalized.StartsWith("grok", StringComparison.Ordinal))
        {
            return "xai";
        }
        if (normalized.StartsWith("mai", StringComparison.Ordinal))
        {
            return "microsoft";
        }

        var separator = normalized.IndexOfAny(['-', '/', ':']);
        return separator > 0 ? normalized[..separator] : normalized;
    }

    private static bool IsOpenAiReasoningModel(string model) =>
        model.Length > 1 &&
        model[0] == 'o' &&
        char.IsAsciiDigit(model[1]);
}
