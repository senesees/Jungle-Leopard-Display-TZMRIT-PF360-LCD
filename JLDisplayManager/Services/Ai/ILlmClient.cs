using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JLDisplayManager.Services.Ai;

/// <summary>Raised for anything the LLM endpoint refused or could not do.</summary>
public sealed class LlmException : Exception
{
    public LlmException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Per-call overrides for one completion.
///
/// Everything here is optional and falls back to <see cref="Models.AiSettings"/>.
/// It exists because two callers now share one endpoint while wanting different
/// things from it: a one-line image prompt is short and benefits from a warm
/// temperature, while overlay JSON is long and wants a cold one. Making those
/// per-call rather than per-provider is what lets them share a configured
/// endpoint without either having to know the other exists.
/// </summary>
public sealed record CompletionOptions
{
    /// <summary>Overrides the endpoint's configured model. Null or empty keeps it.</summary>
    public string? Model { get; init; }

    public int? MaxTokens { get; init; }

    public double? Temperature { get; init; }

    /// <summary>Everything from settings, which is what prompt enhancement wants.</summary>
    public static readonly CompletionOptions Default = new();
}

/// <summary>
/// One completion against a language model: a system message, a user message,
/// and the text that comes back.
///
/// Deliberately still narrow — no streaming, no tools, no conversation. Both
/// callers ask one question and read one answer, and a wider surface would be
/// inventing requirements neither has.
/// </summary>
public interface ILlmClient : IDisposable
{
    /// <summary>
    /// Sends one system/user pair and returns the model's text.
    /// Throws <see cref="LlmException"/> on anything the endpoint refused.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, string userMessage,
        CompletionOptions? options, CancellationToken ct);

    /// <summary>
    /// What models this endpoint offers, for the settings dropdown. Endpoints
    /// that cannot say return an empty list rather than throwing — an unknown
    /// model list is a smaller problem than a settings window that will not open.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}

/// <summary>
/// The original prompt-enhancement call, kept as the name the image pipeline
/// uses. It is exactly a completion with settings-default options; the separate
/// name says what the call is *for* at its two call sites.
/// </summary>
public static class LlmClientExtensions
{
    public static Task<string> EnhanceAsync(this ILlmClient client, string systemPrompt,
        string seed, CancellationToken ct)
        => client.CompleteAsync(systemPrompt, seed, CompletionOptions.Default, ct);
}
