using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using JLDisplayManager.Services;

namespace JLDisplayManager.Models;

public enum LlmProvider
{
    /// <summary>Send the seed prompt to the image model unchanged.</summary>
    None = 0,

    /// <summary>Anything speaking /v1/chat/completions: Ollama, LM Studio, OpenRouter, OpenAI.</summary>
    OpenAiCompatible = 1,

    Anthropic = 2,
}

public enum PromptOrder
{
    Sequential = 0,
    Random = 1,
}

/// <summary>One line of the user's idea list, before any enhancement.</summary>
public sealed class PromptSeed : INotifyPropertyChanged
{
    private string _text = "";
    private bool _enabled = true;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? "");
    }

    /// <summary>Off keeps the line in the list but out of the rotation.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// One LLM provider's address, model and key.
///
/// Held per provider rather than as one shared set of fields, so switching
/// between a local Ollama and Anthropic to compare results does not make you
/// retype the other one's settings every time.
/// </summary>
public sealed class LlmEndpoint : INotifyPropertyChanged
{
    private string _baseUrl = "";
    private string _model = "";
    private string _apiKeyProtected = "";

    /// <summary>Ollama is the likeliest local runner, and its default port is 11434.</summary>
    public static LlmEndpoint OpenAiDefault() => new()
    {
        BaseUrl = "http://localhost:11434/v1",
        Model = "",
    };

    public static LlmEndpoint AnthropicDefault() => new()
    {
        BaseUrl = "https://api.anthropic.com",
        Model = "claude-sonnet-5",
    };

    /// <summary>
    /// The API root. For OpenAI-compatible servers that is the /v1 directory,
    /// not the /chat/completions leaf.
    /// </summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set => Set(ref _baseUrl, (value ?? "").Trim().TrimEnd('/'));
    }

    public string Model
    {
        get => _model;
        set => Set(ref _model, (value ?? "").Trim());
    }

    /// <summary>DPAPI-encrypted, per user. Never written in the clear.</summary>
    public string ApiKeyProtected
    {
        get => _apiKeyProtected;
        set => Set(ref _apiKeyProtected, value ?? "");
    }

    [JsonIgnore]
    public string ApiKey
    {
        get => Secrets.Unprotect(_apiKeyProtected);
        set
        {
            ApiKeyProtected = Secrets.Protect(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasApiKey)));
        }
    }

    [JsonIgnore]
    public bool HasApiKey => _apiKeyProtected.Length > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Everything the AI pipeline needs, in its own file.
///
/// Deliberately not folded into <see cref="AppSettings"/>: this is the newest
/// and least-trusted config in the app, and a corrupt ai.json must not be able
/// to stop the panel from coming up.
/// </summary>
public sealed class AiSettings : INotifyPropertyChanged
{
    /// <summary>
    /// Tuned for Krea 2, which is where these prompts actually land.
    ///
    /// Two of that model's habits shape the wording. It has an aesthetic of its
    /// own and stops exercising it once the prompt is crowded, so this asks for
    /// few things named exactly rather than a long specification. And it reads
    /// named light, material and medium far better than adjectives about
    /// quality, so the padding tokens are banned outright: they cost words and
    /// buy nothing.
    ///
    /// The rest fights what made the enhancer dull to begin with, which was
    /// settling on the most obvious reading of the seed. Nothing curates what
    /// comes back, so the model has to commit to a reading itself.
    /// </summary>
    public const string DefaultSystemPrompt =
        "You write prompts for Krea 2, a text-to-image model. Given a short idea, you return " +
        "one prompt for a single specific picture.\n" +
        "\n" +
        "Write comma-separated visual phrases, not sentences and not tag soup. Lead with the " +
        "shot and the subject, then the light, then the mood, and add detail only where it " +
        "changes the picture.\n" +
        "Krea 2 has taste of its own and loses it when crowded, so name few things and name " +
        "them exactly. One medium, and stay inside it: risograph, oil impasto, 35mm " +
        "photograph, unglazed ceramic, cel animation. One light you could point at: " +
        "golden-hour backlight, hard noon sun, a single lamp off-frame. Two or three colours " +
        "that carry the frame. A full camera spec sheet fights the model, so one lens or one " +
        "depth cue is plenty.\n" +
        "Vague words are worse than nothing here: artistic, illustrated, beautiful lighting, " +
        "masterpiece, 8k, ultra-detailed, trending on artstation. They blend styles together " +
        "instead of choosing one. Skip them, and skip living artists by name.\n" +
        "\n" +
        "Nothing curates what comes back: it is generated unattended and goes straight to the " +
        "panel. So commit to one reading of the idea, and make it the second or third reading " +
        "rather than the first thing anyone pictures. Invent one concrete detail the idea did " +
        "not mention.\n" +
        "\n" +
        "The panel is small and twice as wide as it is tall. Compose letterbox: one clear " +
        "subject, bold shapes, strong light-to-dark contrast. Intricate detail, crowds and " +
        "small print turn to mush at this size, so leave them out, along with any lettering, " +
        "logo or watermark.\n" +
        "\n" +
        "Reply with the prompt only: 25 to 45 words. No preamble, no explanation, no quotes, " +
        "no markdown.";

    /// <summary>
    /// Defaults shipped by earlier versions, newest last. A stored prompt that
    /// still matches one of these was never edited, so it can be upgraded on
    /// load instead of leaving an existing install on wording we have since
    /// decided produces dull pictures.
    /// </summary>
    private static readonly string[] LegacySystemPrompts =
    {
        "You turn a short image idea into one vivid prompt for a text-to-image model.\n" +
        "Reply with the prompt only: no preamble, no explanation, no quotes, no markdown.\n" +
        "Favour concrete visual nouns, lighting, materials, colour and composition over " +
        "abstractions. Do not invent text, logos or watermarks.\n" +
        "The result is shown on a wide 2:1 panel, so compose for a letterbox landscape " +
        "frame with the subject readable at small size.\n" +
        "Keep it under 60 words.",

        "You write prompts for a text-to-image model. Given a short idea, you return one " +
        "prompt describing a single specific picture.\n" +
        "\n" +
        "Commit to one interpretation and make it particular. The obvious reading is the one " +
        "the image model already defaults to, so take a later one: an odd vantage point, an " +
        "unexpected moment, an extreme of scale, an hour or a weather nobody pictures first. " +
        "Invent one concrete detail the idea did not mention.\n" +
        "Say what the picture is made of and how it is lit. Pick one medium and stay inside " +
        "it: a photograph on a named film stock or lens, oil on canvas, woodblock, risograph, " +
        "gouache, technical illustration, 3D render. Name the two or three colours that carry " +
        "the frame. Describe what is there, never how impressive it is.\n" +
        "\n" +
        "The panel is small and twice as wide as it is tall. Compose letterbox: one clear " +
        "subject, bold shapes, strong light-to-dark contrast. Crowds, fine texture and small " +
        "print turn to mush at this size, so leave them out, along with any lettering, logo " +
        "or watermark.\n" +
        "No quality padding (masterpiece, 8k, ultra-detailed, award-winning, trending on " +
        "artstation) and no living artists by name.\n" +
        "\n" +
        "Reply with the prompt only: 40 to 70 words, one paragraph of plain sentences. " +
        "No preamble, no explanation, no quotes, no markdown.",
    };

    /// <summary>
    /// True when this prompt is one the app shipped rather than one the user
    /// wrote. Line endings are normalised first: the settings box hands back
    /// CRLF, so a default that has merely been through Save still counts.
    /// </summary>
    public static bool IsSupersededSystemPrompt(string? prompt)
    {
        string text = (prompt ?? "").Replace("\r\n", "\n").Trim();

        foreach (string shipped in LegacySystemPrompts)
        {
            if (string.Equals(text, shipped, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // SwarmUI
    // -----------------------------------------------------------------------

    private string _swarmUrl = "http://localhost:7801";
    private string _model = "";
    private int _width = 1024;
    private int _height = 512;
    private int _steps = 20;
    private double _cfgScale = 7.0;
    private string _sampler = "";
    private string _scheduler = "";
    private string _negativePrompt = "";
    private string _extraParamsJson = "";
    private int _generateTimeoutSeconds = 300;
    private bool _doNotSaveOnServer;
    private string _swarmTokenProtected = "";

    public string SwarmUrl
    {
        get => _swarmUrl;
        set => Set(ref _swarmUrl, (value ?? "").Trim().TrimEnd('/'));
    }

    /// <summary>Full filepath as ListModels reports it; empty means the server default.</summary>
    public string Model
    {
        get => _model;
        set => Set(ref _model, value ?? "");
    }

    /// <summary>
    /// 1024x512 by default: exactly the panel 2:1, and close enough to the
    /// pixel budget SDXL and Flux were trained at to stay coherent.
    /// </summary>
    public int Width
    {
        get => _width;
        set => Set(ref _width, Snap(value));
    }

    public int Height
    {
        get => _height;
        set => Set(ref _height, Snap(value));
    }

    public int Steps
    {
        get => _steps;
        set => Set(ref _steps, Math.Clamp(value, 1, 150));
    }

    public double CfgScale
    {
        get => _cfgScale;
        set => Set(ref _cfgScale, Math.Clamp(value, 0.0, 30.0));
    }

    /// <summary>Empty means whatever the server already defaults to.</summary>
    public string Sampler
    {
        get => _sampler;
        set => Set(ref _sampler, value ?? "");
    }

    public string Scheduler
    {
        get => _scheduler;
        set => Set(ref _scheduler, value ?? "");
    }

    public string NegativePrompt
    {
        get => _negativePrompt;
        set => Set(ref _negativePrompt, value ?? "");
    }

    /// <summary>
    /// Raw JSON object merged into the generate request, for any SwarmUI
    /// parameter this UI does not surface. An escape hatch, not a main road:
    /// invalid JSON is logged and ignored rather than failing the generation.
    /// </summary>
    public string ExtraParamsJson
    {
        get => _extraParamsJson;
        set => Set(ref _extraParamsJson, value ?? "");
    }

    public int GenerateTimeoutSeconds
    {
        get => _generateTimeoutSeconds;
        set => Set(ref _generateTimeoutSeconds, Math.Clamp(value, 10, 3600));
    }

    /// <summary>
    /// Ask SwarmUI not to keep its own copy. Off by default — its history is
    /// useful, and this app keeps only what fits inside the retention cap — but
    /// a slideshow left running for weeks otherwise writes every image twice.
    /// </summary>
    public bool DoNotSaveOnServer
    {
        get => _doNotSaveOnServer;
        set => Set(ref _doNotSaveOnServer, value);
    }

    /// <summary>Only needed when the Swarm instance enforces accounts.</summary>
    public string SwarmTokenProtected
    {
        get => _swarmTokenProtected;
        set => Set(ref _swarmTokenProtected, value ?? "");
    }

    [JsonIgnore]
    public string SwarmToken
    {
        get => Secrets.Unprotect(_swarmTokenProtected);
        set => SwarmTokenProtected = Secrets.Protect(value);
    }

    // -----------------------------------------------------------------------
    // Prompt enhancement
    // -----------------------------------------------------------------------

    private LlmProvider _provider = LlmProvider.OpenAiCompatible;
    private string _systemPrompt = DefaultSystemPrompt;
    private double _temperature = 0.9;
    private int _maxTokens = 400;
    private int _llmTimeoutSeconds = 120;
    private bool _requireEnhancement;

    public LlmProvider Provider
    {
        get => _provider;
        set => Set(ref _provider, value);
    }

    /// <summary>
    /// Each provider keeps its own address, model and key, so switching between
    /// them to compare results does not throw away the other one's setup.
    /// </summary>
    public LlmEndpoint OpenAi { get; set; } = LlmEndpoint.OpenAiDefault();

    public LlmEndpoint Anthropic { get; set; } = LlmEndpoint.AnthropicDefault();

    /// <summary>The endpoint the current provider will actually use.</summary>
    [JsonIgnore]
    public LlmEndpoint CurrentEndpoint =>
        Provider == LlmProvider.Anthropic ? Anthropic : OpenAi;

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => Set(ref _systemPrompt, string.IsNullOrWhiteSpace(value) ? DefaultSystemPrompt : value);
    }

    /// <summary>
    /// OpenAI-compatible endpoints only. Claude models from Opus 4.6 onwards
    /// reject temperature outright with a 400, so the Anthropic path does not
    /// send it at all.
    /// </summary>
    public double Temperature
    {
        get => _temperature;
        set => Set(ref _temperature, Math.Clamp(value, 0.0, 2.0));
    }

    public int MaxTokens
    {
        get => _maxTokens;
        set => Set(ref _maxTokens, Math.Clamp(value, 32, 8192));
    }

    public int LlmTimeoutSeconds
    {
        get => _llmTimeoutSeconds;
        set => Set(ref _llmTimeoutSeconds, Math.Clamp(value, 5, 600));
    }

    /// <summary>
    /// Off — the default — means a failed enhancement falls back to the seed
    /// prompt unchanged. A dead LLM should cost picture quality, not leave the
    /// panel stuck on one image.
    /// </summary>
    public bool RequireEnhancement
    {
        get => _requireEnhancement;
        set => Set(ref _requireEnhancement, value);
    }

    // -----------------------------------------------------------------------
    // Pipeline
    // -----------------------------------------------------------------------

    private int _bufferSize = 3;
    private int _generateEverySeconds;
    private int _dwellSeconds = 60;
    private PromptOrder _order = PromptOrder.Random;
    private int _retentionCount = 100;
    private bool _pruneByAge;
    private int _retentionDays = 14;
    private bool _startWithApp;

    /// <summary>How many finished images to keep ahead of the panel.</summary>
    public int BufferSize
    {
        get => _bufferSize;
        set => Set(ref _bufferSize, Math.Clamp(value, 1, 50));
    }

    /// <summary>
    /// A minimum interval between generations, measured start to start.
    ///
    /// Off by default. The buffer target already stops runaway generation — the
    /// producer sleeps once the queue is full — so this exists only for someone
    /// who wants a hard cap on how often the backend is asked to work at all.
    /// Setting it longer than the dwell means the panel will hold images past
    /// their turn, which is honoured rather than worked around.
    /// </summary>
    public int GenerateEverySeconds
    {
        get => _generateEverySeconds;
        set => Set(ref _generateEverySeconds, Math.Clamp(value, 0, 86400));
    }

    /// <summary>How long each generated image holds the panel.</summary>
    public int DwellSeconds
    {
        get => _dwellSeconds;
        set => Set(ref _dwellSeconds, Math.Clamp(value, 5, 86400));
    }

    public PromptOrder Order
    {
        get => _order;
        set => Set(ref _order, value);
    }

    /// <summary>Generated images to keep before the oldest unpinned ones go.</summary>
    public int RetentionCount
    {
        get => _retentionCount;
        set => Set(ref _retentionCount, Math.Clamp(value, 1, 100000));
    }

    public bool PruneByAge
    {
        get => _pruneByAge;
        set => Set(ref _pruneByAge, value);
    }

    public int RetentionDays
    {
        get => _retentionDays;
        set => Set(ref _retentionDays, Math.Clamp(value, 1, 3650));
    }

    /// <summary>Begin the slideshow as soon as the app starts.</summary>
    public bool StartWithApp
    {
        get => _startWithApp;
        set => Set(ref _startWithApp, value);
    }

    // -----------------------------------------------------------------------

    public List<PromptSeed> Prompts { get; set; } = new();

    /// <summary>Where sequential order left off, so a restart does not reset it.</summary>
    public int NextPromptIndex { get; set; }

    /// <summary>
    /// Diffusion models work in a latent space eight pixels to the unit, so a
    /// dimension off a multiple of 8 gets rounded by the backend or rejected.
    /// Rounded here instead, where it can be seen.
    ///
    /// Deliberately 8 rather than 64. Sixty-four is a rule of thumb for best
    /// results, but enforcing it puts the panel's own 960x480 out of reach —
    /// 480 is not a multiple of 64 — and that is the one size someone is most
    /// likely to ask for.
    /// </summary>
    private static int Snap(int value)
    {
        int clamped = Math.Clamp(value, 64, 4096);
        return ((clamped + 4) / 8) * 8;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
