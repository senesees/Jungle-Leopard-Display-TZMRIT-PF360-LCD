using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using JLDisplayManager.Models;
using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Ai;
using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Services.Overlay.Ai;

/// <summary>Whether a generation adds to the current profile or rebuilds it.</summary>
public enum OverlayIntent { Add, Replace }

/// <summary>What one prompt produced.</summary>
public sealed class GenerationResult
{
    public bool Success { get; init; }

    /// <summary>Set only when nothing usable came back; shown to the user as written.</summary>
    public string? Error { get; init; }

    public OverlayIntent Intent { get; init; } = OverlayIntent.Add;

    /// <summary>The model's one-line description of what it made.</summary>
    public string? Note { get; init; }

    public List<OverlayLayer> Layers { get; init; } = new();

    /// <summary>
    /// What the model actually said, kept so the same answer can be re-applied
    /// the other way round — see <see cref="OverlayGenerator.Assemble"/>'s
    /// intent override. Null on failure.
    /// </summary>
    public OverlayPlan? Plan { get; init; }

    /// <summary>
    /// The theme the model asked for, or null if it did not ask or the intent
    /// was <c>add</c>. The editor applies it and names it in the banner, so a
    /// wholesale restyle is never silent.
    /// </summary>
    public string? Theme { get; init; }

    /// <summary>
    /// Anything dropped or corrected on the way through. Surfaced with the
    /// result: a request that quietly did less than it said is worse than one
    /// that says so.
    /// </summary>
    public List<string> Notes { get; init; } = new();

    public static GenerationResult Failed(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Turns a sentence into overlay layers.
///
/// The pipeline is deliberately paranoid, because its input is written by a
/// language model:
///
///   ask -> extract JSON -> parse -> validate -> expand -> lay out -> preview
///
/// Every stage degrades rather than throwing. An unknown sensor loses one layer,
/// not the batch; prose around the JSON is stripped; a reply that will not parse
/// at all is retried once with a firmer instruction, and then given up on with a
/// plain message. A model that cannot produce JSON twice will not produce it on
/// the fifth attempt either, and the user is waiting.
/// </summary>
public sealed class OverlayGenerator
{
    private readonly AiSettings _ai;
    private readonly SensorRegistry _sensors;

    public OverlayGenerator(AiSettings ai, SensorRegistry sensors)
    {
        _ai = ai;
        _sensors = sensors;
    }

    /// <summary>
    /// Structured output is a different job from writing an image prompt, and
    /// wants a colder temperature and a much larger budget: a full overlay is
    /// twenty layers of JSON, not one line of prose.
    /// </summary>
    private CompletionOptions Options() => new()
    {
        Model = _ai.OverlayModel,
        MaxTokens = _ai.OverlayMaxTokens,
        Temperature = _ai.OverlayTemperature,
    };

    /// <summary>
    /// Generates layers for <paramref name="prompt"/> against the current
    /// profile. Never throws for anything the endpoint or the model did; those
    /// come back as <see cref="GenerationResult.Error"/>.
    /// </summary>
    public async Task<GenerationResult> GenerateAsync(string prompt, OverlayProfile? current,
        double surfaceWidth, double surfaceHeight, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return GenerationResult.Failed("Type what you want on the panel first.");

        if (!LlmClientFactory.IsConfigured(_ai))
            return GenerationResult.Failed(
                "No language model is set up yet. Open AI settings and give it an address "
                + "or an API key.");

        SensorSnapshot sensors = _sensors.Snapshot();
        string system = OverlaySystemPrompt.Build(sensors, current, surfaceWidth, surfaceHeight);

        using ILlmClient client = LlmClientFactory.Create(_ai, LlmClientFactory.Resolve(_ai));

        OverlayPlan? plan = null;
        string? lastRaw = null;

        // Two attempts at most. The second says plainly that the first was not
        // JSON, which is often all a small model needs.
        for (int attempt = 0; attempt < 2 && plan == null; attempt++)
        {
            string instructions = attempt == 0
                ? system
                : system + OverlaySystemPrompt.RetrySuffix;

            try
            {
                lastRaw = await client
                    .CompleteAsync(instructions, prompt.Trim(), Options(), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // An endpoint failure will not be fixed by asking again.
                return GenerationResult.Failed(ex.Message);
            }

            plan = TryParse(lastRaw);
        }

        if (plan == null)
        {
            Storage.Log("overlay ai: could not parse a plan from: "
                        + Trim(lastRaw ?? "", 400));

            return GenerationResult.Failed(
                "The model did not answer with usable JSON. A larger or more capable model "
                + "usually fixes this.");
        }

        return Assemble(plan, current, sensors, surfaceWidth, surfaceHeight);
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Expands a parsed plan into real layers. Split out from the request so it
    /// can be exercised against canned model replies with no endpoint at all.
    ///
    /// <paramref name="intentOverride"/> lets the user apply a result the other
    /// way round without asking the model again — and it genuinely has to come
    /// back through here rather than just moving layers about, because add and
    /// replace lay out differently: one treats what is already on the panel as
    /// obstacles and the other has a clear surface.
    /// </summary>
    public GenerationResult Assemble(OverlayPlan plan, OverlayProfile? current,
        SensorSnapshot sensors, double surfaceWidth, double surfaceHeight,
        OverlayIntent? intentOverride = null)
    {
        var notes = new List<string>();

        List<LayerSpec> specs = plan.Layers ?? new List<LayerSpec>();

        if (specs.Count > OverlaySystemPrompt.MaxLayers)
        {
            notes.Add($"the model asked for {specs.Count} layers; kept the first "
                      + $"{OverlaySystemPrompt.MaxLayers}");
            specs = specs.Take(OverlaySystemPrompt.MaxLayers).ToList();
        }

        OverlayIntent intent = intentOverride ?? ParseIntent(plan.Intent);

        ExpansionResult expanded = LayerFactory.Expand(specs, sensors);
        notes.AddRange(expanded.Notes);

        if (expanded.Layers.Count == 0)
        {
            return GenerationResult.Failed(notes.Count > 0
                ? "Nothing could be made from that: " + string.Join("; ", notes)
                : "The model returned no layers.");
        }

        // On replace there is nothing to avoid; on add, everything already on
        // the panel is an obstacle.
        IReadOnlyList<OverlayLayer> obstacles =
            intent == OverlayIntent.Replace || current == null
                ? Array.Empty<OverlayLayer>()
                : current.Layers;

        // Specs and layers can differ in length once drops are applied, and the
        // layout reads groups from the spec at the matching index — so realign
        // them before placing.
        List<LayerSpec> kept = AlignSpecs(specs, expanded.Layers, sensors);

        // A theme only comes in on a replace. Applying one while adding a single
        // layer would restyle the whole panel over a request that never
        // mentioned it — surprising, and not what "add a clock" asked for.
        string? theme = null;

        if (intent == OverlayIntent.Replace && !string.IsNullOrWhiteSpace(plan.Theme))
        {
            OverlayTheme resolved = OverlayTheme.ByName(plan.Theme);

            // ByName falls back to minimal, so an invented name would silently
            // become the default. Say so instead.
            if (!string.Equals(resolved.Name, plan.Theme.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                notes.Add($"there is no \"{plan.Theme.Trim()}\" theme; used {resolved.Name}");

            theme = resolved.Name;
        }

        // A theme's density scales every margin and gutter, so a dense look packs
        // tightly and an airy one breathes without the layout gaining a second
        // set of constants. Use the incoming theme when there is one.
        double density = OverlayTheme.ByName(theme ?? current?.Theme).Density;

        LayoutEngine.Place(expanded.Layers, obstacles, kept, surfaceWidth, surfaceHeight, density);
        LayoutEngine.ApplyOffsets(expanded.Layers, kept, surfaceWidth, surfaceHeight, density);

        return new GenerationResult
        {
            Success = true,
            Intent = intent,
            Note = string.IsNullOrWhiteSpace(plan.Note) ? null : plan.Note.Trim(),
            Layers = expanded.Layers,
            Notes = notes,
            Plan = plan,
            Theme = theme,
        };
    }

    /// <summary>
    /// Rebuilds the spec list so index N still describes layer N after drops.
    ///
    /// <see cref="LayerFactory"/> returns only what survived, but the layout
    /// reads each layer's group from the spec at its index. Without this a
    /// single dropped layer shifts every group afterwards, and clusters that
    /// belong together silently come apart — a bug that would look like bad
    /// layout rather than bad bookkeeping.
    /// </summary>
    private static List<LayerSpec> AlignSpecs(List<LayerSpec> specs,
        List<OverlayLayer> layers, SensorSnapshot sensors)
    {
        var aligned = new List<LayerSpec>(layers.Count);
        int next = 0;

        foreach (OverlayLayer layer in layers)
        {
            // Walk forward to the spec that produced this layer: its anchor
            // always matches, since that is the one field the factory copies
            // through unchanged.
            while (next < specs.Count
                   && LayerFactory.ParseAnchor(specs[next].Anchor) != layer.Anchor)
                next++;

            aligned.Add(next < specs.Count ? specs[next] : new LayerSpec());
            next++;
        }

        return aligned;
    }

    private static OverlayIntent ParseIntent(string? intent)
    {
        string i = intent?.Trim().ToLowerInvariant() ?? "";

        // Anything unrecognised is an add. Adding to a profile is recoverable
        // by deleting a few layers; replacing one is not, so the ambiguous case
        // takes the safer road.
        return i is "replace" or "new" or "rebuild" or "reset"
            ? OverlayIntent.Replace
            : OverlayIntent.Add;
    }

    // -----------------------------------------------------------------------
    // Parsing
    // -----------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Pulls a plan out of whatever the model said. Null when there is nothing
    /// usable in it.
    ///
    /// Public for the same reason as <see cref="Assemble"/>: together they are
    /// the whole pipeline minus the network, so the awkward replies a model
    /// actually produces can be exercised without an endpoint.
    /// </summary>
    public static OverlayPlan? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string? json = ExtractJson(raw);
        if (json == null) return null;

        // A bare array is a common shape when a model forgets the wrapper. It
        // is unambiguous, so accept it rather than spending a retry on it.
        if (json.StartsWith('['))
        {
            try
            {
                var layers = JsonSerializer.Deserialize<List<LayerSpec>>(json, Json);
                return layers is { Count: > 0 } ? new OverlayPlan { Layers = layers } : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        try
        {
            var plan = JsonSerializer.Deserialize<OverlayPlan>(json, Json);
            return plan is { Layers.Count: > 0 } ? plan : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the JSON in a reply that may also contain a code fence, a lead-in
    /// sentence, or a closing remark.
    ///
    /// Brace matching rather than a regex, because a template like
    /// <c>"{cpu.load:0}%"</c> puts braces inside strings — and a regex that
    /// ignores that will cut the object short at the first one.
    /// </summary>
    public static string? ExtractJson(string raw)
    {
        string text = raw.Trim();

        // Strip a fence first so its language tag cannot be mistaken for prose
        // containing a brace.
        int fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            int start = text.IndexOf('\n', fence);
            int end = text.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start >= 0 && end > start) text = text[(start + 1)..end].Trim();
        }

        int open = text.IndexOfAny(new[] { '{', '[' });
        if (open < 0) return null;

        char opener = text[open];
        char closer = opener == '{' ? '}' : ']';

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == opener) depth++;
            else if (c == closer && --depth == 0) return text[open..(i + 1)];
        }

        return null;   // never closed
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
