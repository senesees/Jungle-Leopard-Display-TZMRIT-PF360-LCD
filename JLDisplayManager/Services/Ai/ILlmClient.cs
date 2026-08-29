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
/// The one thing this app asks of a language model: turn a short idea into a
/// longer image prompt. Deliberately narrow — two providers implement it, and
/// anything wider would be inventing requirements neither of them needs.
/// </summary>
public interface ILlmClient : IDisposable
{
    /// <summary>Rewrites one seed prompt. Throws <see cref="LlmException"/> on failure.</summary>
    Task<string> EnhanceAsync(string systemPrompt, string seed, CancellationToken ct);

    /// <summary>
    /// What models this endpoint offers, for the settings dropdown. Endpoints
    /// that cannot say return an empty list rather than throwing — an unknown
    /// model list is a smaller problem than a settings window that will not open.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
