using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Ai;

/// <summary>Raised for anything the server refused or could not do.</summary>
public sealed class SwarmException : Exception
{
    public SwarmException(string message, string? errorId = null) : base(message) => ErrorId = errorId;

    /// <summary>The server error_id, when it gave one. "invalid_session_id" is the interesting one.</summary>
    public string? ErrorId { get; }
}

/// <summary>What a probe of the server turned up, for populating the dropdowns.</summary>
public sealed class SwarmProbe
{
    public string Version { get; init; } = "";
    public string ServerId { get; init; } = "";
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Samplers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Schedulers { get; init; } = Array.Empty<string>();
    public int BackendCount { get; init; }
}

/// <summary>One finished image, still in memory.</summary>
public sealed class SwarmImage
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string Extension { get; init; } = ".png";
    public long Seed { get; init; }
    public string Model { get; init; } = "";
}

/// <summary>
/// Talks to SwarmUI over its documented HTTP API.
///
/// The shape of that API drives most of what is here: everything is a POST of
/// JSON to /API/(route), everything but GetNewSession needs a session_id in the
/// body, and errors come back as HTTP 200 with an "error" field rather than a
/// status code. So there is one funnel — <see cref="PostAsync"/> — that adds the
/// session, checks for that field, and re-sessions once if the server says the
/// old one has expired.
/// </summary>
public sealed class SwarmClient : IDisposable
{
    private readonly AiSettings _ai;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Random _random = new();

    private string _sessionId = "";

    public SwarmClient(AiSettings ai)
    {
        _ai = ai;

        // No BaseAddress: the server URL is a live setting, and rebuilding the
        // client every time someone edits it would throw away the connection
        // pool. No Timeout either — generation can legitimately take minutes,
        // so the per-call CancellationToken is the only clock that matters.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Drops the cached session, so the next call gets a fresh one.</summary>
    public void ResetSession() => _sessionId = "";

    // -----------------------------------------------------------------------
    // Probing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Asks the server what it has: version, checkpoints, samplers, schedulers,
    /// and how many backends are registered. Everything the AI window needs to
    /// offer real choices rather than a hardcoded list.
    /// </summary>
    public async Task<SwarmProbe> ProbeAsync(CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct).ConfigureAwait(false);

        var models = new List<string>();
        var samplers = new List<string>();
        var schedulers = new List<string>();

        // ListT2IParams carries the sampler and scheduler value lists, and a
        // models map as well. Taken first because it is one call for three
        // answers; ListModels below is the authority on checkpoints.
        var t2i = await PostAsync("ListT2IParams", new JsonObject(), ct).ConfigureAwait(false);

        if (t2i["list"] is JsonArray list)
        {
            samplers.AddRange(ValuesOfParam(list, "sampler"));
            schedulers.AddRange(ValuesOfParam(list, "scheduler"));
        }

        if (t2i["models"] is JsonObject modelMap
            && modelMap["Stable-Diffusion"] is JsonArray fromParams)
            models.AddRange(fromParams.Select(Str).Where(n => !string.IsNullOrEmpty(n))!);

        try
        {
            var listed = await ListModelsAsync(ct).ConfigureAwait(false);
            if (listed.Count > 0)
            {
                models.Clear();
                models.AddRange(listed);
            }
        }
        catch (SwarmException ex)
        {
            // Not fatal: the ListT2IParams map above is usually enough to pick
            // a checkpoint, and a permissions-restricted account can hit this.
            Storage.Log($"swarm: ListModels failed, falling back to ListT2IParams ({ex.Message})");
        }

        int backends = 0;
        try
        {
            var b = await PostAsync("ListBackends", new JsonObject { ["full_data"] = false }, ct)
                .ConfigureAwait(false);
            backends = b.Count(kv => kv.Key != "error" && kv.Value is JsonObject);
        }
        catch (SwarmException)
        {
            // Requires a permission the account may not have. Only cosmetic.
        }

        return new SwarmProbe
        {
            Version = session.Version,
            ServerId = session.ServerId,
            Models = models.Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
            Samplers = samplers,
            Schedulers = schedulers,
            BackendCount = backends,
        };
    }

    /// <summary>Every checkpoint the server can see, as full paths ready to pass back.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["path"] = "",
            // Deep enough for the folder layouts people actually use, shallow
            // enough not to walk a large model library forever.
            ["depth"] = 4,
            ["subtype"] = "Stable-Diffusion",
            ["sortBy"] = "Name",
            ["allowRemote"] = true,
            ["dataImages"] = false,
        };

        var response = await PostAsync("ListModels", body, ct).ConfigureAwait(false);

        var names = new List<string>();
        if (response["files"] is JsonArray files)
        {
            foreach (var file in files)
            {
                // A file entry is normally an object with a name; some builds
                // hand back the bare name instead.
                string? name = Field(file, "name") ?? Str(file);
                if (!string.IsNullOrEmpty(name)) names.Add(name!);
            }
        }

        return names;
    }

    // -----------------------------------------------------------------------
    // Generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generates one image and returns its bytes.
    ///
    /// The seed is chosen here rather than left to the server, so the value can
    /// be recorded against the item that comes out. A picture worth keeping is
    /// worth being able to reproduce.
    /// </summary>
    public async Task<SwarmImage> GenerateAsync(string prompt, CancellationToken ct)
    {
        long seed = _random.NextInt64(0, int.MaxValue);

        var body = new JsonObject
        {
            ["images"] = 1,
            ["prompt"] = prompt,
            ["width"] = _ai.Width,
            ["height"] = _ai.Height,
            ["steps"] = _ai.Steps,
            ["cfgscale"] = _ai.CfgScale,
            ["seed"] = seed,
        };

        if (!string.IsNullOrWhiteSpace(_ai.Model)) body["model"] = _ai.Model;
        if (!string.IsNullOrWhiteSpace(_ai.NegativePrompt)) body["negativeprompt"] = _ai.NegativePrompt;
        if (!string.IsNullOrWhiteSpace(_ai.Sampler)) body["sampler"] = _ai.Sampler;
        if (!string.IsNullOrWhiteSpace(_ai.Scheduler)) body["scheduler"] = _ai.Scheduler;
        if (_ai.DoNotSaveOnServer) body["donotsave"] = true;

        MergeExtraParams(body);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_ai.GenerateTimeoutSeconds));

        JsonObject response;
        try
        {
            response = await PostAsync("GenerateText2Image", body, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SwarmException(
                $"the server did not finish within {_ai.GenerateTimeoutSeconds}s");
        }

        string? path = (response["images"] as JsonArray)?
            .Select(ImagePath)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        if (string.IsNullOrWhiteSpace(path))
        {
            // Include what did come back: an empty images array almost always
            // means the backend refused the parameters, and the reason is in
            // there somewhere.
            throw new SwarmException(
                "the server returned no image — " + OpenAiCompatibleClient.Brief(response.ToJsonString()));
        }

        var (bytes, extension) = await FetchImageAsync(path!, ct).ConfigureAwait(false);

        return new SwarmImage
        {
            Bytes = bytes,
            Extension = extension,
            Seed = seed,
            Model = _ai.Model,
        };
    }

    /// <summary>
    /// Turns what GenerateText2Image handed back into bytes. That is either a
    /// path to GET, or — when the server was told not to save — a data URL
    /// carrying the whole image inline.
    /// </summary>
    private async Task<(byte[] Bytes, string Extension)> FetchImageAsync(string path, CancellationToken ct)
    {
        if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = path.IndexOf(',');
            if (comma < 0) throw new SwarmException("the server returned a malformed data URL");

            string header = path[..comma];
            string extension = header.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            return (Convert.FromBase64String(path[(comma + 1)..]), extension);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, AbsoluteUrl(path));
        AddToken(request);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SwarmException($"could not fetch the image: HTTP {(int)response.StatusCode}");

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) throw new SwarmException("the server returned an empty image");

        string ext = System.IO.Path.GetExtension(path.Split('?')[0]);
        return (bytes, string.IsNullOrEmpty(ext) ? ".png" : ext);
    }

    /// <summary>
    /// Folds the raw-JSON escape hatch into the request. Bad JSON there is the
    /// user experimenting, not a reason to stop generating, so it is logged and
    /// dropped.
    /// </summary>
    private void MergeExtraParams(JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(_ai.ExtraParamsJson)) return;

        try
        {
            if (JsonNode.Parse(_ai.ExtraParamsJson) is not JsonObject extra)
            {
                Storage.Log("swarm: extra parameters must be a JSON object; ignored");
                return;
            }

            foreach (var pair in extra.ToList())
            {
                // Detached from its old parent first: a JsonNode may only be
                // attached to one document at a time.
                extra.Remove(pair.Key);
                body[pair.Key] = pair.Value;
            }
        }
        catch (JsonException ex)
        {
            Storage.Log($"swarm: extra parameters are not valid JSON, ignored ({ex.Message})");
        }
    }

    // -----------------------------------------------------------------------
    // Session and transport
    // -----------------------------------------------------------------------

    private sealed record Session(string Id, string Version, string ServerId);

    /// <summary>
    /// Gets a session, creating one if needed. Gated so that a burst of calls
    /// on a cold client makes one GetNewSession rather than several.
    /// </summary>
    private async Task<Session> EnsureSessionAsync(CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await SendAsync("GetNewSession", new JsonObject(), ct).ConfigureAwait(false);

            _sessionId = Str(response["session_id"])
                ?? throw new SwarmException("the server did not return a session id");

            return new Session(
                _sessionId,
                Str(response["version"]) ?? "?",
                Str(response["server_id"]) ?? "?");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// The one funnel every route goes through: adds the session, and renews it
    /// once if the server says it has expired. A Swarm restart invalidates every
    /// session, and a slideshow that has been running for days will meet that.
    /// </summary>
    private async Task<JsonObject> PostAsync(string route, JsonObject body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionId)) await EnsureSessionAsync(ct).ConfigureAwait(false);

        body["session_id"] = _sessionId;

        try
        {
            return await SendAsync(route, body, ct).ConfigureAwait(false);
        }
        catch (SwarmException ex) when (ex.ErrorId == "invalid_session_id")
        {
            Storage.Log("swarm: session expired, renewing");
            await EnsureSessionAsync(ct).ConfigureAwait(false);
            body["session_id"] = _sessionId;
            return await SendAsync(route, body, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject> SendAsync(string route, JsonObject body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_ai.SwarmUrl))
            throw new SwarmException("no SwarmUI address is set");

        using var request = new HttpRequestMessage(HttpMethod.Post, AbsoluteUrl("API/" + route))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        AddToken(request);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Nearly always "Swarm is not running". Say that, rather than
            // relaying a socket error nobody can act on.
            throw new SwarmException($"cannot reach SwarmUI at {_ai.SwarmUrl} ({ex.Message})");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new SwarmException($"{route} failed: HTTP {(int)response.StatusCode}");

            JsonObject? json;
            try
            {
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException ex)
            {
                throw new SwarmException($"{route} returned something that is not JSON ({ex.Message})");
            }

            if (json is null) throw new SwarmException($"{route} returned an unexpected response");

            // Swarm reports failure in the body, with HTTP 200 over the top.
            // The field is a plain string on most builds and an object with a
            // message on others.
            if (json["error"] is JsonNode errorNode)
            {
                string message = Str(errorNode)
                    ?? Field(errorNode, "message")
                    ?? OpenAiCompatibleClient.Brief(errorNode.ToJsonString());

                if (message.Length > 0)
                    throw new SwarmException(message, Str(json["error_id"]));
            }

            return json;
        }
    }

    private void AddToken(HttpRequestMessage request)
    {
        string token = _ai.SwarmToken;
        if (token.Length > 0) request.Headers.Add("Cookie", "swarm_token=" + token);
    }

    private string AbsoluteUrl(string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUrl;
        }

        string root = _ai.SwarmUrl;
        if (!root.Contains("://", StringComparison.Ordinal)) root = "http://" + root;

        return root.TrimEnd('/') + "/" + pathOrUrl.TrimStart('/');
    }

    /// <summary>
    /// A string from a node that may not hold one.
    ///
    /// Swarm's responses are not uniformly typed: the same field arrives as a
    /// bare string from one version or backend and as an object from another,
    /// and JsonNode.GetValue&lt;string&gt;() throws "the node must be of type
    /// JsonValue" on anything else. Everything read out of a response goes
    /// through here so a shape we did not expect degrades to null instead of
    /// taking down the request.
    /// </summary>
    private static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        if (value.TryGetValue(out string? text)) return text;

        // A number or bool where a string was expected is still usable as one.
        return value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? value.ToJsonString()
            : null;
    }

    /// <summary>Reads a field, tolerating a node that cannot be indexed at all.</summary>
    private static string? Field(JsonNode? node, string name) =>
        node is JsonObject obj ? Str(obj[name]) : null;

    /// <summary>
    /// The image path out of one entry of the "images" array. That entry is a
    /// bare path string on most builds, and an object carrying the path beside
    /// its metadata on others.
    /// </summary>
    private static string? ImagePath(JsonNode? entry)
    {
        if (Str(entry) is { Length: > 0 } direct) return direct;

        if (entry is not JsonObject obj) return null;

        foreach (string key in new[] { "image", "src", "path", "url" })
        {
            if (Str(obj[key]) is { Length: > 0 } value) return value;
        }

        return null;
    }

    /// <summary>Pulls the allowed values of one parameter out of a ListT2IParams list.</summary>
    private static IEnumerable<string> ValuesOfParam(JsonArray list, string id)
    {
        foreach (var entry in list)
        {
            if (entry is not JsonObject obj) continue;
            if (Str(obj["id"]) != id) continue;
            if (obj["values"] is not JsonArray values) continue;

            foreach (var value in values)
            {
                if (Str(value) is { Length: > 0 } text) yield return text;
            }

            yield break;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _sessionGate.Dispose();
    }
}
