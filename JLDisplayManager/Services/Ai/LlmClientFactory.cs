using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Ai;

/// <summary>
/// Builds LLM clients, for whoever needs one.
///
/// This exists because two features now share one configured endpoint — image
/// prompt enhancement and overlay generation — and neither should own the
/// other's access to it. Previously construction lived inside
/// <see cref="PromptEnhancer"/>, which made "can I talk to the model" mean
/// "is the image pipeline running".
/// </summary>
public static class LlmClientFactory
{
    /// <summary>
    /// A client for <paramref name="provider"/>. The caller owns it and must
    /// dispose it; callers that ask repeatedly should cache by provider, as
    /// <see cref="PromptEnhancer.GetClient"/> does.
    /// </summary>
    public static ILlmClient Create(AiSettings ai, LlmProvider provider) => provider switch
    {
        LlmProvider.Anthropic => new AnthropicClient(ai),
        _ => new OpenAiCompatibleClient(ai),
    };

    /// <summary>
    /// Which provider a feature other than the image pipeline should use.
    ///
    /// <see cref="LlmProvider.None"/> means "do not rewrite image prompts" — it
    /// is a decision about that pipeline, not a statement that no endpoint is
    /// configured. Reading it as the latter would let turning off prompt
    /// enhancement silently disable overlay generation, which the user set up
    /// separately and never asked to switch off.
    ///
    /// So None falls back to the OpenAI-compatible endpoint, which is the one
    /// that defaults to a local address and needs no key.
    /// </summary>
    public static LlmProvider Resolve(AiSettings ai) =>
        ai.Provider == LlmProvider.None ? LlmProvider.OpenAiCompatible : ai.Provider;

    /// <summary>
    /// Whether a completion has any chance of working: an address for the local
    /// dialect, or a key and a model for Claude. Lets a caller say "set up an
    /// endpoint first" instead of failing mid-request.
    /// </summary>
    public static bool IsConfigured(AiSettings ai)
    {
        return Resolve(ai) == LlmProvider.Anthropic
            ? ai.Anthropic.HasApiKey && !string.IsNullOrWhiteSpace(ai.Anthropic.Model)
            : !string.IsNullOrWhiteSpace(ai.OpenAi.BaseUrl);
    }
}
