using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Ai;

/// <summary>
/// Anything speaking the OpenAI chat-completions dialect: Ollama, LM Studio,
/// llama.cpp, OpenRouter, OpenAI itself.
///
/// That dialect is the reason this class exists rather than a second Anthropic
/// client — it is the closest thing to a lingua franca among local runners, and
/// covers offline use, which the Anthropic path by definition cannot.
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private readonly AiSettings _ai;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public OpenAiCompatibleClient(AiSettings ai) => _ai = ai;

    public async Task<string> EnhanceAsync(string systemPrompt, string seed, CancellationToken ct)
    {
        var endpoint = _ai.OpenAi;

        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            throw new LlmException("no LLM address is set");
        if (string.IsNullOrWhiteSpace(endpoint.Model))
            throw new LlmException("no LLM model is set");

        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = seed },
            },
            ["max_tokens"] = _ai.MaxTokens,
            ["temperature"] = _ai.Temperature,
            ["stream"] = false,
        };

        var response = await PostAsync("chat/completions", body, ct).ConfigureAwait(false);

        string? text = response["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            // A reasoning model that spends its whole budget thinking lands
            // here with an empty content string, which is worth saying plainly
            // rather than reporting as a parse failure.
            string? reason = response["choices"]?[0]?["finish_reason"]?.GetValue<string>();
            throw new LlmException(reason == "length"
                ? "the model hit its token limit before answering; raise Max tokens"
                : "the model returned an empty response");
        }

        return text!;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_ai.OpenAi.BaseUrl)) return Array.Empty<string>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url("models"));
            AddAuth(request);

            using var cts = Linked(ct);
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();

            string text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (JsonNode.Parse(text) is not JsonObject json) return Array.Empty<string>();

            var names = new List<string>();
            if (json["data"] is JsonArray data)
            {
                foreach (var entry in data)
                {
                    if (entry?["id"]?.GetValue<string>() is { Length: > 0 } id) names.Add(id);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Listing is a convenience. A server that will not enumerate its
            // models can still generate with one typed in by hand.
            Storage.Log($"llm: could not list models ({ex.Message})");
            return Array.Empty<string>();
        }
    }

    private async Task<JsonObject> PostAsync(string route, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(route))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        AddAuth(request);

        using var cts = Linked(ct);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"cannot reach the LLM at {_ai.OpenAi.BaseUrl} ({ex.Message})", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"the LLM did not answer within {_ai.LlmTimeoutSeconds}s");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new LlmException($"the LLM returned HTTP {(int)response.StatusCode}: {Brief(text)}");

            try
            {
                if (JsonNode.Parse(text) is JsonObject json) return json;
            }
            catch (JsonException ex)
            {
                throw new LlmException($"the LLM returned something that is not JSON ({ex.Message})", ex);
            }

            throw new LlmException("the LLM returned an unexpected response");
        }
    }

    private void AddAuth(HttpRequestMessage request)
    {
        // Local runners typically want no key at all, so an empty one is normal
        // rather than a misconfiguration.
        string key = _ai.OpenAi.ApiKey;
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private CancellationTokenSource Linked(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_ai.LlmTimeoutSeconds));
        return cts;
    }

    private string Url(string route)
    {
        string root = _ai.OpenAi.BaseUrl;
        if (!root.Contains("://", StringComparison.Ordinal)) root = "http://" + root;
        return root.TrimEnd('/') + "/" + route;
    }

    /// <summary>Keeps an error page out of the status bar and the log.</summary>
    internal static string Brief(string text)
    {
        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    public void Dispose() => _http.Dispose();
}
