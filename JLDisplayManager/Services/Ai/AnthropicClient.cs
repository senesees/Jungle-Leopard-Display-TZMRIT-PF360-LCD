using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Ai;

/// <summary>
/// Claude, over the Messages API.
///
/// This is raw HTTP rather than the official SDK on purpose: the release drop
/// is three binaries and no third-party assemblies, and the request here is one
/// POST with three headers. If this file ever grows past prompt enhancement —
/// tools, streaming, thinking — that trade stops making sense and the SDK
/// should replace it.
///
/// Two details of the current API shape are load-bearing:
///   - the system prompt is a top-level field, not a message with role "system";
///   - temperature is rejected outright by models from Opus 4.6 onwards, so it
///     is never sent. The OpenAI-compatible path still honours the setting.
/// </summary>
public sealed class AnthropicClient : ILlmClient
{
    private const string ApiVersion = "2023-06-01";
    private const string DefaultBaseUrl = "https://api.anthropic.com";

    private readonly AiSettings _ai;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public AnthropicClient(AiSettings ai) => _ai = ai;

    public async Task<string> EnhanceAsync(string systemPrompt, string seed, CancellationToken ct)
    {
        var endpoint = _ai.Anthropic;

        if (string.IsNullOrWhiteSpace(endpoint.Model))
            throw new LlmException("no Claude model is set");
        if (!endpoint.HasApiKey)
            throw new LlmException("no Anthropic API key is set");

        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["max_tokens"] = _ai.MaxTokens,
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = seed },
            },
        };

        var response = await SendAsync(HttpMethod.Post, "v1/messages", body, ct).ConfigureAwait(false);

        // A safety classifier can decline with HTTP 200 and stop_reason
        // "refusal". Worth naming, because the useful fix is to reword the
        // seed prompt rather than to go looking for a network fault.
        if (response["stop_reason"]?.GetValue<string>() == "refusal")
            throw new LlmException("Claude declined to enhance that prompt");

        // Content is a list of blocks; only the text ones are wanted, and a
        // thinking-capable model may put others alongside them.
        var text = new StringBuilder();
        if (response["content"] is JsonArray content)
        {
            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() != "text") continue;
                text.Append(block["text"]?.GetValue<string>());
            }
        }

        if (text.Length == 0)
        {
            throw new LlmException(response["stop_reason"]?.GetValue<string>() == "max_tokens"
                ? "Claude hit its token limit before answering; raise Max tokens"
                : "Claude returned an empty response");
        }

        return text.ToString();
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        if (!_ai.Anthropic.HasApiKey) return Array.Empty<string>();

        try
        {
            var response = await SendAsync(HttpMethod.Get, "v1/models?limit=100", null, ct)
                .ConfigureAwait(false);

            var names = new List<string>();
            if (response["data"] is JsonArray data)
            {
                foreach (var entry in data)
                {
                    if (entry?["id"]?.GetValue<string>() is { Length: > 0 } id) names.Add(id);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Storage.Log($"anthropic: could not list models ({ex.Message})");
            return Array.Empty<string>();
        }
    }

    private async Task<JsonObject> SendAsync(
        HttpMethod method, string route, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Url(route));

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        request.Headers.Add("x-api-key", _ai.Anthropic.ApiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_ai.LlmTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"cannot reach the Anthropic API ({ex.Message})", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"Claude did not answer within {_ai.LlmTimeoutSeconds}s");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Errors come back as {"error":{"type":…,"message":…}}. The
                // message is the part worth showing; the status code alone
                // rarely says what to fix.
                string detail = OpenAiCompatibleClient.Brief(text);
                try
                {
                    if (JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>()
                        is { Length: > 0 } message)
                    {
                        detail = message;
                    }
                }
                catch (JsonException)
                {
                    // Keep the raw body; a proxy may have returned HTML.
                }

                throw new LlmException($"Anthropic returned HTTP {(int)response.StatusCode}: {detail}");
            }

            try
            {
                if (JsonNode.Parse(text) is JsonObject json) return json;
            }
            catch (JsonException ex)
            {
                throw new LlmException($"Anthropic returned something that is not JSON ({ex.Message})", ex);
            }

            throw new LlmException("Anthropic returned an unexpected response");
        }
    }

    private string Url(string route)
    {
        string root = _ai.Anthropic.BaseUrl;
        if (string.IsNullOrWhiteSpace(root)) root = DefaultBaseUrl;
        if (!root.Contains("://", StringComparison.Ordinal)) root = "https://" + root;
        return root.TrimEnd('/') + "/" + route;
    }

    public void Dispose() => _http.Dispose();
}
