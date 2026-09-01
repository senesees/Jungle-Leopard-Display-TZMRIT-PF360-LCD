# AI-assisted overlay creation — implementation plan

Type what you want on the panel and have it appear:

> *"Add a clock at the center"*
> *"Add GPU usage bottom left"*
> *"Create a full stylised overlay with CPU & GPU usage"*

Status: **planned, not started.** Builds on the overlay feature in
[`overlay-plan.md`](overlay-plan.md), phases 0–4 of which are complete.

---

## 1. What already exists, and what this adds

The LLM plumbing is done and in use. `ILlmClient.EnhanceAsync(systemPrompt,
seed, ct)` is *already* a general "system message + user message → text" call —
only its name and its doc comment are narrow. Two clients implement it
(`OpenAiCompatibleClient`, `AnthropicClient`), `AiSettings` holds the provider,
endpoint, key, temperature and token budget, and `Secrets` DPAPI-protects the
key.

So this feature is not "add an LLM". It is three things:

1. **A schema the model can actually hit** — a compact description of a layer
   that expands into the real thing.
2. **A prompt that describes this machine** — the model cannot guess that
   `gpu.temp` exists and `cpu.temp` does not, or that the panel is 960×480.
3. **A pipeline that never trusts the answer** — parse, validate, repair,
   preview, and only then apply.

Nothing about the overlay renderer, the compositor or the sensors changes.

---

## 2. The compact schema

The real `OverlayLayer` model has around forty fields across five types. Handing
that to a language model means forty chances per layer to produce something
wrong, and roughly 250 tokens per layer of output.

Instead the model writes a small, forgiving shape and **the program expands it**:

```jsonc
{
  "intent": "add",                  // or "replace"
  "note": "GPU temperature gauge, bottom left",
  "layers": [
    {
      "kind":   "gauge",            // text | bar | gauge | panel | icon
      "sensor": "gpu.temp",
      "anchor": "bottom-left",      // 9-point, or "center"
      "size":   "medium",           // small | medium | large
      "label":  "GPU",
      "accent": "warm"              // named palette role, not a hex code
    }
  ]
}
```

Every other property — `SweepAngle`, `CentreFontSize`, `Thickness`,
`RoundCaps`, threshold stops, the `{gpu.temp:0}°` template — is filled in by
`LayerFactory` from the kind, the sensor's own unit and range, and the chosen
size. **The expansion is where the taste lives**, and it is ours, not the
model's: a gauge always gets a sensible sweep, a temperature always gets
amber-then-red thresholds, a bar bound to `mem.percent` always spans 0–100.

Consequences worth stating plainly:

- **Roughly 40 tokens per layer instead of 250.** A twelve-layer "full stylised
  overlay" fits comfortably in a normal token budget.
- **Every produced layer is valid by construction.** There is no path where the
  model invents a field name or a malformed colour, because it never writes one.
- **The model cannot reach every property.** A request for a 200° anticlockwise
  gauge with 3px flat caps is not expressible. That is the deliberate trade: the
  editor exists for that, and this feature is for getting to 90% in one line.

### Fields the model may write

| Field | Values | Notes |
|---|---|---|
| `kind` | `text` `bar` `gauge` `panel` `icon` | `panel` is a `ShapeLayer` backing card; `icon` an `ImageLayer` |
| `sensor` | any registered id | Validated against the live registry; unknown → layer dropped, or kept as static text if `text` |
| `anchor` | `top-left` … `bottom-right`, `center` | Maps to `LayerAnchor` |
| `size` | `small` `medium` `large` | Resolved to pixels per kind |
| `label` | free text | Caption on a gauge, prefix on a readout |
| `template` | optional | An explicit `{token}` string when the model wants one; validated |
| `accent` | `neutral` `cool` `warm` `hot` `good` | **Named roles, not hex.** See §3 |
| `group` | optional string | Layers sharing a group are laid out as one cluster |

A named palette rather than hex codes is deliberate. Models produce plausible
but muddy colours, and a profile whose greens all differ slightly looks
accidental. Roles resolve to one coherent palette, which also means a future
"theme" setting can restyle every generated profile at once.

---

## 3. Layout: the model says *where-ish*, the program does the geometry

Language models are poor at pixel arithmetic and will happily overlap two
clusters or place something at `y = 700` on a 480-tall panel.

They are, however, good at *intent*: "bottom left", "next to the CPU one". So
the model emits `anchor` + `size` + optional `group`, and `LayoutEngine` turns
that into coordinates:

- Layers sharing an `anchor` (and `group`) **stack** away from that anchor —
  first at the margin, each subsequent one offset by the previous height plus a
  gutter. This is what makes "GPU usage bottom left" produce a readout with a
  bar under it rather than two things on top of each other.
- A `panel` in a group is **sized to its contents** and pushed to the back, so a
  backing card fits what it backs without the model computing its extents.
- Everything is **clamped into the design surface**, which is
  `OverlayRenderer.DesignSize(rotate)` — so this is correct on a rotated
  mounting for free, 480×960 included.
- On `intent: "add"`, existing layers are obstacles: a new cluster at an
  occupied anchor stacks below what is already there rather than covering it.

The model may still nudge with an optional `offset: [x, y]`, which is applied
after placement and clamped. Rarely needed; available when a prompt says
"a bit higher".

---

## 4. The system prompt describes *this* machine

A prompt hard-coded at build time would tell the model about sensors this
machine does not have. `OverlaySystemPrompt.Build()` composes it at call time
from live state:

- **The sensor list** — id, unit and range, from `SensorRegistry.Descriptors`,
  grouped by category. Only sensors actually reporting; `cpu.temp` is absent
  until LibreHardwareMonitor is running (phase 6 of the overlay plan), so the
  model never writes a token that renders `--`.
- **The design surface** — the current `DesignSize`, so it knows whether it is
  laying out landscape or portrait.
- **The current profile**, summarised — one line per layer — so `add` knows what
  is already there and `replace` knows what it is replacing.
- **The schema and the rules**, with two or three worked examples covering
  exactly the three shapes of request in the brief.

Sensor list injection is the single highest-value part of this feature. It is
the difference between a model guessing plausible-sounding ids and one writing
ids that exist.

**Budget note.** 51 sensors listed compactly is roughly 600 tokens. Worth it,
but it means the request is not tiny; §7 covers trimming when the list grows.

---

## 5. Never trust the answer

```
prompt ─> ILlmClient ─> raw text
                          │
                          ├─ ExtractJson       fences, lead-ins, prose
                          ├─ Parse             tolerant; unknown fields ignored
                          ├─ Validate          kind, sensor, anchor, accent
                          ├─ Expand            compact -> real OverlayLayer
                          ├─ Layout            anchors -> coordinates, clamped
                          └─ Preview           pending; nothing saved or sent
```

Each stage degrades rather than fails:

| Problem | Response |
|---|---|
| Prose around the JSON | `ExtractJson` takes the outermost `{…}`; extends the existing `PromptEnhancer.Clean`, which already strips fences and lead-ins |
| Trailing commas, single quotes | Tolerant parse before a strict one |
| Unknown `kind` | Layer dropped, named in the result note |
| Unknown `sensor` | Dropped for a bar or gauge (a bar with no source is meaningless); kept as static text for `text` |
| Unknown `anchor` / `accent` | Falls back to `top-left` / `neutral` |
| Nothing parseable at all | One retry with a terser "JSON only" instruction, then a plain error |
| Model returns 30 layers | Capped (20), the rest dropped and reported |

The retry matters for small local models, which often answer correctly on the
second attempt after being told the first was unparseable. One retry, not a
loop — a model that cannot produce JSON twice will not produce it on the fifth
try either, and the user is waiting.

---

## 6. Preview, accept, discard

Generated layers are applied to the **editor canvas immediately** — so they are
seen against the live panel backdrop, at the right size, with real sensor values
— but held as a *pending change*. Nothing is written to `overlays.json` and
nothing reaches the glass until Accept.

```
┌─ Overlay editor ────────────────────────────────────────────┐
│ [prompt box                                    ] [Generate] │
│                                                             │
│   ✨ Added 3 layers — GPU gauge, load bar, label            │
│      intent: add   ·   [Accept]  [Discard]  [Try again]     │
└─────────────────────────────────────────────────────────────┘
```

- **Accept** commits: layers become ordinary layers, saved and pushed.
- **Discard** restores the snapshot taken before generation.
- **Try again** re-prompts with the same text at a higher temperature, replacing
  the pending result — the natural gesture when a layout is nearly right.
- The **intent** is shown and overridable, so a prompt the model read as
  `replace` can be applied as `add` without retyping it.

Implementation is a deep copy of the profile before generation, exactly the
JSON round-trip `OnDuplicateProfile` already uses. That is also why this stops
short of a full undo stack: snapshotting one profile is cheap and local to this
feature, while a general undo history is a feature in its own right.

---

## 7. Settings

Shares the endpoint, splits the job:

| Setting | Source |
|---|---|
| Provider, base URL, API key | **Shared** with the image pipeline — no second key to paste |
| Model | **Own optional override**, empty = the shared model |
| System prompt | **Own**, composed per call (§4) |
| Max tokens | **Own**, default 2000 — a full overlay is far longer than an image prompt |
| Temperature | **Own**, default 0.4 — lower than image prompts want; this is a structured task |

One behaviour worth calling out: overlay generation must work when the image
pipeline's provider is `None`. `LlmProvider.None` currently means "do not
enhance prompts", and that must not disable a feature the user has separately
configured. `ILlmClient` construction moves behind a small factory so both
callers share it without either owning it.

---

## 8. Where it appears

**In the editor** — a prompt box across the top of the layer list, with the
result banner above the canvas. This is the primary home: generation is an
editing action, and the result wants inspecting.

**In the tray** — *Overlay → Generate…*, opening a small dialog with the same
box, for when the window is closed. Applies through the same preview flow, with
the editor opened to show it.

Both share one `OverlayGenerator`; neither owns it.

---

## 9. Files

```
Services/Ai/
  ILlmClient.cs              EnhanceAsync -> CompleteAsync; EnhanceAsync kept as a shim
  LlmClientFactory.cs        NEW  shared construction, independent of the pipeline

Services/Overlay/Ai/
  OverlayGenerator.cs        NEW  the pipeline of §5
  OverlaySystemPrompt.cs     NEW  composes the prompt from live state
  LayerSpec.cs               NEW  the compact schema
  LayerFactory.cs            NEW  spec -> real OverlayLayer, all defaults
  LayoutEngine.cs            NEW  anchors + groups -> coordinates
  AccentPalette.cs           NEW  named roles -> the one coherent palette

Views/
  OverlayEditorWindow        prompt box, result banner, accept/discard
  OverlayPromptWindow.cs     NEW  the tray dialog

Models/
  AiSettings.cs              overlay model override, token budget, temperature
```

---

## 10. Phases

| # | Scope | Done when |
|---|---|---|
| **1** | ✅ **Done.** `CompleteAsync` + `CompletionOptions` + `LlmClientFactory`; `EnhanceAsync` kept as an extension shim. | ✅ Builds clean with no call site changed — the image pipeline is untouched. |
| **2** | ✅ **Done.** `LayerSpec`, `LayerFactory`, `AccentPalette`. No LLM — hand-written specs expanded and rendered. | ✅ All five kinds expand correctly; garbage input degrades with notes. See §12. |
| **3** | ✅ **Done.** `LayoutEngine`: anchors, stacking, groups, panel sizing, width budgets, dial rails, clamping, obstacle avoidance. | ✅ Five layouts, landscape and portrait, with no overlaps and nothing off-panel. See §12. |
| **4** | ✅ **Done.** `OverlaySystemPrompt` + `OverlayGenerator`: extract, parse, validate, repair, retry; overlay tuning in `AiSettings`. | ✅ Fed a canned reply with prose, a fence and two bad layers, it produced the right ten and reported both drops. See §12. |
| **5** | ✅ **Done.** Editor prompt box, result banner, accept / discard / try again / flip intent, snapshot. | ✅ All three example prompts work end to end against a real local model. See §12. |
| **6** | Settings, tray *Generate…*, docs, `AGENTS.md`. | — |

Phases 2 and 3 carry the quality of the whole feature and need no endpoint to
develop against, which makes them the cheap place to get it right. Phase 4 is
where the failure modes live.

---

## 11. Risks

**A small local model may not manage even this.** Mitigated by the compact
schema, the one retry, and — the honest fallback — a clear error naming the
model as the problem, since the endpoint is the user's to change. Worth testing
against something small on Ollama, not only against Claude.

**Layout is the real quality bar, not the JSON.** A profile where every layer is
valid but three of them overlap looks broken. This is why the program owns
geometry (§3) and why phase 3 is separate and testable without an endpoint.

**"Stylised" is subjective.** The named palette and the size ladder mean
generated overlays will share a house style — coherent, but recognisably the
same house every time. A later theme setting is the escape hatch; per-request
freeform colour is not, because that is exactly what produces muddy output.

**Prompt injection is not a concern here** — the model's output never executes,
it becomes drawing instructions that are validated first. The worst a hostile
response achieves is a silly overlay the user discards. Worth stating so nobody
later "hardens" the parser under the impression it is a security boundary.

**Scope held deliberately**: no editing existing layers by prompt ("make the
clock bigger"), no conversational refinement, no image generation for `icon`
layers. Each is a reasonable follow-up; none is needed for the three examples in
the brief.

---

## 12. Results so far

### Phases 1–3

Verified with hand-written specs — exactly what the model is expected to emit
for the three example prompts — expanded and rendered through the real
`OverlayRenderer`, with no LLM involved. This is the half of the feature that
decides whether the output looks any good, and it needs no endpoint to test.

Checks are geometric and automatic: nothing outside the surface, no two content
layers overlapping (ignoring backing panels, which are meant to sit under
things). Five layouts pass, landscape and portrait, plus a garbage-tolerance
pass.

**Rendering it caught three real bugs that the geometry checks alone would not
have:**

1. **Opposing clusters collided on a narrow surface.** A 220-wide readout is
   comfortable on a 960 panel and half the width of a 480 one; anchors alone
   never notice. Fixed with **width budgets** — the nine anchors form three rows
   of three columns, each row's width is split between the columns actually
   used, and a cluster over its share is scaled down proportionally (font size
   and all, floored at 0.62 so it stays readable). This was caught by the
   portrait case and would have shipped as "the AI makes a mess on rotated
   panels".

2. **An explicit accent was silently ignored.** `ApplyRamp` overwrote the
   requested colour for any load or temperature — which is almost every
   interesting sensor — so asking for a blue GPU gauge produced a green one. The
   ramp is now the default *only when no accent was named*; an explicit one
   wins.

3. **Clusters read backwards at the bottom edge.** Offsets run inward from the
   anchor, so at a bottom anchor a bar was drawn above the label it belonged to.
   Layers are now assigned offsets in reading order, reversed at the bottom and
   right edges. Same fix covers dial rails at a right anchor.

**One improvement from looking rather than measuring:** two gauges at one anchor
stacked vertically, which passed every check and still looked wrong — a row of
dials is how dials are presented. Gauge-only clusters now lay out along a
**rail**, wrapping to a new one when they run out of width.

Garbage tolerance, all passing: a sensor this machine lacks (`cpu.temp`, absent
until the overlay plan's phase 6) is dropped with a note rather than drawn
reading `--`; an invented sensor is dropped; an unknown kind that names a sensor
becomes a readout; `{gpu.temp:0}` written where an id belongs is understood;
and the good layers survive the bad ones in the same batch.

### Phase 4

22 checks, all passing, driven by canned replies — the shapes a model actually
returns, including the badly behaved ones. No endpoint involved: `TryParse` and
`Assemble` are public precisely because together they are the whole pipeline
minus the network.

**Extraction** handles a plain object, a fenced one, prose either side, a
trailing comma, and a bare array (a common shape when the wrapper is forgotten).
It rejects a refusal, an unterminated object, and an empty layer list.

The brace matcher earns its place on one case in particular: a template like
`"CPU {cpu.load:0}% / {gpu.load:0}%"` puts braces *inside a string*, and a regex
that ignores string context cuts the object short at the first one. Matching
braces while tracking strings and escapes is why that test passes.

**Assembly**, on a reply carrying prose, a fence, a `cpu.temp` gauge this machine
cannot supply and an invented `sparkline` kind: ten layers out, both problems
reported, no overlaps, nothing off-panel. The sparkline recovered as a readout
because it named a real sensor; the gauge was dropped because a dial with no
source is meaningless.

That test also covers `AlignSpecs`, which exists for a subtle reason worth
recording. `LayerFactory` returns only the layers that survived, but the layout
reads each layer's group from the spec at its index — so a single dropped layer
would shift every group after it, and clusters that belong together would
silently come apart. It would have looked like bad layout rather than bad
bookkeeping. The check that the GPU cluster survives a drop occurring *before*
it is what pins that down.

Other behaviours confirmed: an unrecognised intent falls back to **add**, not
replace, because adding is recoverable by deleting a few layers and replacing is
not; the layer cap holds and is reported; and a plan where everything is bad
fails with a readable sentence rather than an exception.

**The prompt** comes out at ~5.5 KB, roughly 1400 tokens, and is verified to
contain the live sensor ids with units and ranges, the surface size, and — the
check that matters — *not* to offer `cpu.temp`, which nothing on this machine
supplies.

### Phase 5 — the first live calls

**All three example prompts worked first try**, against a local 35B GGUF served
over the OpenAI-compatible dialect. That is the case the plan called the main
risk, so it is the one worth reporting:

| Prompt | Time | Intent | Layers | Drops | Overlaps |
|---|---|---|---|---|---|
| "Add a clock at the center" | 1.3 s | Add | 1 | 0 | 0 |
| "Add GPU usage bottom left" | 1.0 s | Add | 3 | 0 | 0 |
| "Create a full stylised overlay with CPU & GPU usage" | 2.2 s | **Replace** | 9 | 0 | 0 |

Run in sequence against a growing profile, so the second had something to avoid.
Intents were read correctly without being told — "add" for the first two,
"replace" for the third — and the third produced a genuinely symmetric layout:
CPU and GPU cards at the top corners with a dial under each, clock centred
below. No validation dropped anything; the model used real sensor ids
throughout.

**The preview contract holds**, verified through the UI rather than in code: a
result applies to the canvas and the panel immediately, the layer list grows,
and **`overlays.json` is not written at all**. Discard took 9 layers back to 7
with the file still absent. Accept persisted exactly the 12 layers on screen.

**One prompt fix from watching it.** Asked for "a big clock bottom right" when a
clock was *already* at bottom right, the model added a second one — the existing
profile was described to it, and it duplicated the clock anyway. The rule is now
explicit: do not repeat something already there, and if the request names an
existing thing, use `replace` and rebuild rather than adding a copy.

**A limitation worth stating plainly**, seen in the same run: asked for "CPU
temperature", the model substituted `gpu.temp` — correctly, since the prompt
forbids inventing ids and `cpu.temp` genuinely is not available until the
overlay plan's phase 6. It is the right behaviour under the constraint, but the
answer is not what was asked for. Until LibreHardwareMonitor or HWiNFO support
lands, CPU temperature cannot be generated because it cannot be measured.
