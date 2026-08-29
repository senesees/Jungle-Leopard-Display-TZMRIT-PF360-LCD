using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Ai;

/// <summary>What one seed turned into, and whether the LLM actually did it.</summary>
public sealed class EnhancedPrompt
{
    public string Seed { get; init; } = "";
    public string Text { get; init; } = "";

    /// <summary>False when this is the seed passed through untouched.</summary>
    public bool WasEnhanced { get; init; }

    /// <summary>Why enhancement was skipped or failed, when it was.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Picks the next seed and runs it past the language model.
///
/// The interesting behaviour is what happens when that fails: unless the user
/// has asked for the opposite, the seed goes through unchanged. A dead LLM
/// should cost picture quality, not leave the panel stuck on one image for
/// however long the endpoint stays down.
/// </summary>
public sealed class PromptEnhancer : IDisposable
{
    private readonly AiSettings _ai;
    private readonly Random _random = new();

    private ILlmClient? _client;
    private LlmProvider _clientProvider = LlmProvider.None;

    public PromptEnhancer(AiSettings ai) => _ai = ai;

    /// <summary>
    /// Picks the next seed to use, honouring sequential or random order and
    /// skipping disabled lines. Null when there is nothing enabled to pick.
    /// </summary>
    public string? NextSeed()
    {
        var enabled = _ai.Prompts
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Text))
            .ToList();

        if (enabled.Count == 0) return null;

        if (_ai.Order == PromptOrder.Random)
            return enabled[_random.Next(enabled.Count)].Text.Trim();

        // Sequential. The stored index counts across the whole list including
        // disabled lines, so it stays meaningful when one is toggled back on.
        int index = Math.Abs(_ai.NextPromptIndex) % enabled.Count;
        _ai.NextPromptIndex = index + 1;
        return enabled[index].Text.Trim();
    }

    /// <summary>
    /// Runs one seed through the model. Never throws for an endpoint failure
    /// unless RequireEnhancement is set — see the class remarks.
    /// </summary>
    public async Task<EnhancedPrompt> EnhanceAsync(string seed, CancellationToken ct)
    {
        if (_ai.Provider == LlmProvider.None)
            return new EnhancedPrompt { Seed = seed, Text = seed, WasEnhanced = false };

        try
        {
            var client = GetClient();
            string raw = await client.EnhanceAsync(_ai.SystemPrompt, seed, ct).ConfigureAwait(false);
            string cleaned = Clean(raw);

            if (string.IsNullOrWhiteSpace(cleaned))
                throw new LlmException("the model returned nothing usable");

            return new EnhancedPrompt { Seed = seed, Text = cleaned, WasEnhanced = true };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_ai.RequireEnhancement)
                throw new LlmException($"prompt enhancement failed: {ex.Message}", ex);

            Storage.Log($"ai: enhancement failed, using the seed as written ({ex.Message})");

            return new EnhancedPrompt
            {
                Seed = seed,
                Text = seed,
                WasEnhanced = false,
                Note = ex.Message,
            };
        }
    }

    /// <summary>
    /// Strips the packaging models put around an answer even when told not to:
    /// a lead-in line, code fences, surrounding quotes, a "Prompt:" label.
    /// What should reach the image model is the prompt and nothing else.
    /// </summary>
    internal static string Clean(string raw)
    {
        string text = raw.Trim();

        // Fenced block: take what is inside the outermost fence.
        var fence = Regex.Match(text, @"^```[^\n]*\n(?<body>.*?)\n?```$", RegexOptions.Singleline);
        if (fence.Success) text = fence.Groups["body"].Value.Trim();

        // A chatty model puts its lead-in on its own line before the prompt.
        // Only drop it when a blank line separates the two, so a prompt that
        // simply happens to contain a colon survives intact.
        var leadIn = Regex.Match(
            text,
            @"^[^\n]{0,120}:\s*\n\s*\n(?<body>.+)$",
            RegexOptions.Singleline);
        if (leadIn.Success) text = leadIn.Groups["body"].Value.Trim();

        text = Regex.Replace(text, @"^(?:enhanced\s+)?prompt\s*:\s*", "", RegexOptions.IgnoreCase);

        // Matched wrapping quotes, straight or curly.
        if (text.Length >= 2)
        {
            char first = text[0], last = text[^1];
            bool quoted = (first == '"' && last == '"')
                || (first == '\'' && last == '\'')
                || (first == '“' && last == '”');
            if (quoted) text = text[1..^1].Trim();
        }

        // Collapse to a single line: SwarmUI takes one prompt string, and a
        // stray newline in the middle of it reads badly in the library tooltip.
        text = Regex.Replace(text, @"\s*\n\s*", " ").Trim();

        return text;
    }

    /// <summary>
    /// The client for the configured provider, rebuilt when that changes so a
    /// switch in the settings window takes effect without a restart.
    /// </summary>
    public ILlmClient GetClient()
    {
        if (_client is not null && _clientProvider == _ai.Provider) return _client;

        _client?.Dispose();
        _clientProvider = _ai.Provider;
        _client = _ai.Provider switch
        {
            LlmProvider.Anthropic => new AnthropicClient(_ai),
            _ => new OpenAiCompatibleClient(_ai),
        };

        return _client;
    }

    /// <summary>Drops the cached client, so edited settings are picked up next call.</summary>
    public void Invalidate()
    {
        _client?.Dispose();
        _client = null;
        _clientProvider = LlmProvider.None;
    }

    public void Dispose() => Invalidate();
}
