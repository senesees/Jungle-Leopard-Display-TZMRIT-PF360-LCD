# Richer drawing — implementation plan

Give the overlay a wider visual vocabulary, and give the AI the words to reach
it, so *"create a stylised overlay"* produces something that looks designed
rather than assembled.

Status: **planned, not started.** Builds on
[`overlay-plan.md`](overlay-plan.md) (phases 0–5 complete, phase 6 partly) and
[`overlay-ai-plan.md`](overlay-ai-plan.md) (phases 1–5 complete).

---

## 1. The finding that shapes this

Before adding anything, it is worth being precise about what is missing:

| | |
|---|---|
| Fields the AI can set today | **8** |
| Properties the layer model actually has | **96** |

Gradients, segmented bars, gauge ticks, text outlines, shadows, background
pills, per-layer fonts, letter spacing, rotation, opacity, threshold colour
stops, bar direction, corner radii, stroke colours, ellipses and lines are
**all already implemented and rendering correctly**. The AI simply cannot ask
for any of them, because `LayerSpec` has no field that says so.

So the single largest improvement available is not new rendering code. It is
letting a prompt reach the renderer that already exists. That is §3, it needs no
new drawing at all, and it comes first for that reason.

New primitives (§4–§7) then build on a vocabulary that is already wide.

---

## 2. What "stylised" actually needs

Four things, in rough order of how much they change the result:

1. **Coherence.** One palette, one font, one corner radius across every layer.
   A profile whose four greens differ slightly looks accidental. This is the
   theme (§5) and it matters more than any individual primitive.
2. **Iconography.** A thermometer beside a temperature is the difference
   between a dashboard and a list of numbers. Cheapest big win (§4).
3. **Ornament.** Rules, brackets, rings — the marks that say a layout was
   composed rather than stacked (§6).
4. **Depth.** Gradients and glow, so a panel reads as a surface (§7).

Plus history graphs (§8), which are less about style and more about being a
dashboard at all.

---

## 3. Unlock what exists

### 3.1 The style object

One optional block per layer, holding only what is worth varying per layer.
Everything absent falls through to the theme, then to `LayerFactory`'s defaults —
so a spec that says nothing still produces exactly what it does today.

```jsonc
{
  "kind": "bar",
  "sensor": "cpu.load",
  "anchor": "top-left",
  "style": {
    "fill":      "warm",        // accent role, or "warm->hot" for a gradient
    "segments":  16,            // 0 = continuous
    "radius":    2,             // corner radius
    "outline":   1.5,           // text only
    "font":      "condensed",   // named role, not a font name
    "weight":    "bold",
    "opacity":   0.9,
    "rotate":    -90,
    "ticks":     7,             // gauge only
    "sweep":     220            // gauge only
  }
}
```

Roughly **ten fields**, chosen because each changes the look materially and none
of them can be inferred. Deliberately *not* exposed: exact pixel geometry (the
layout engine owns that, §3 of the AI plan), hex colours (§5), and the long tail
of properties the editor exists for.

`fill` accepts `"warm"` or `"warm->hot"`. The arrow form is how a gradient is
requested without the model ever writing a colour — it names two roles and the
palette resolves both.

### 3.2 Named font roles

`"font": "condensed"` rather than `"font": "Bahnschrift Condensed"`, for the same
reason accents are roles: a model asked for a font name will invent one that is
not installed, and the failure is silent.

| Role | Resolves to | For |
|---|---|---|
| `default` | Segoe UI | Everything, unless told otherwise |
| `condensed` | Bahnschrift | Dense readouts, tight clusters |
| `mono` | Cascadia Mono | Values that must not jitter as digits change |
| `display` | Segoe UI Variable Display | Big clocks and headline numbers |

All four confirmed present on this machine. Each falls back to Segoe UI if
absent, so a machine without Bahnschrift degrades rather than failing.

`mono` earns its place: a proportional font makes a changing number shuffle
sideways, which on a panel refreshed 30 times a second is genuinely distracting.

---

## 4. Icons

The biggest stylistic return for the least work, because the font is already
there: **Segoe Fluent Icons, 2033 glyphs**, present on this machine, no bundled
asset and no image file.

### 4.1 A new layer type

`GlyphLayer` — draws one icon at a size, in a colour, like any other layer.

The schema's `icon` kind currently maps to `ImageLayer` (a file the user picks),
which the AI can never usefully fill in because it cannot know what images
exist. So:

- `"kind": "icon"` now means **a glyph** — reachable, useful, no setup.
- `"kind": "image"` keeps the file-backed `ImageLayer` for the editor.

That is a small breaking change to the AI schema and worth making now, while
the only profiles using `icon` are ones we generated in testing.

### 4.2 Named icons, not codepoints

```
"kind": "icon", "icon": "thermometer", "anchor": "top-left", "group": "cpu"
```

A curated `IconNames` map — roughly forty names covering what a hardware panel
wants: `cpu`, `gpu`, `chip`, `memory`, `disk`, `network`, `download`, `upload`,
`thermometer`, `fan`, `pump`, `power`, `battery`, `clock`, `calendar`,
`warning`, `speed`, `wifi`, `bluetooth`, `volume`, `play`, `pause`, `heart`,
`lightning`, `gauge`, `chart`, `folder`, `settings`.

Names rather than codepoints for the same reason as everything else: `U+E9CA`
is unguessable and unverifiable, `thermometer` is neither.

### 4.3 Windows 10

`Segoe Fluent Icons` is Windows 11. Windows 10 has `Segoe MDL2 Assets`, which
shares most codepoints. The layer tries Fluent, falls back to MDL2, and draws
nothing if neither resolves — checked once at startup, not per frame.

`IconNames` stores both codepoints where they differ, so a name resolves
correctly on either OS rather than rendering a wrong glyph.

---

## 5. Themes

A profile carries a theme; layers inherit it unless their style block overrides.
This is what makes a generated overlay coherent, and it is the reason the AI can
produce a *look* from one word rather than styling twenty layers individually.

```csharp
public sealed class OverlayTheme
{
    public string Name { get; set; }          // "minimal", "hud", "terminal", "neon"

    public string Background { get; set; }    // panel fill
    public double PanelOpacity { get; set; }
    public double CornerRadius { get; set; }

    public string Font { get; set; }          // a role from §3.2
    public string Text { get; set; }          // primary ink
    public string TextDim { get; set; }       // captions

    public string Good { get; set; }          // the accent roles, per theme
    public string Cool { get; set; }
    public string Warm { get; set; }
    public string Hot { get; set; }

    public double Density { get; set; }       // gutter and margin multiplier
}
```

Four shipped themes, and the AI names one per plan:

| Theme | Reads as |
|---|---|
| `minimal` | Today's look: dark cards, Segoe UI, soft corners, green-amber-red |
| `hud` | Thin strokes, sharp corners, cyan and amber, corner brackets, condensed |
| `terminal` | Mono throughout, green on near-black, square, hairline rules |
| `neon` | Saturated magenta and cyan, glow on text, deep translucent panels |

`AccentPalette` becomes theme-aware: `Resolve("warm")` returns the *theme's*
warm rather than a constant. Everything already written keeps working, because
`minimal` holds exactly the current values.

**Density** is worth calling out. It scales margins and gutters, so `terminal`
can pack tightly and `neon` can breathe, from one number rather than the layout
engine gaining a second set of constants.

---

## 6. Decorative shapes

Ornament that carries no data, which is most of what makes a layout look
deliberate. All reuse geometry that already exists — the gauge's arc builder and
`ShapeLayer`'s rect/ellipse/line — so this is the cheapest of the new primitives.

`ShapeKind` gains:

| Kind | What |
|---|---|
| `Ring` | An unfilled circle. Behind a gauge, or as a bare marker |
| `Arc` | An arc with no value bound to it — a decorative sweep |
| `Bracket` | HUD corner marks; four of them frame a cluster |
| `Rule` | A hairline divider, horizontal or vertical, with optional fade |
| `Chevron` | A direction mark, for flanking a readout |

Each is a handful of lines in `OverlayRenderer`. The AI reaches them through the
existing `panel` kind plus `"style": { "shape": "bracket" }`, rather than five
new kinds cluttering the vocabulary.

---

## 7. Gradients and glow

### 7.1 What they cost — measured, and mostly wrong

This section originally reasoned from first principles: JPEG spends bits on
high-frequency detail, so gradients and glow are cheap and outlines and
segmented bars are dear. It even claimed to be describing "measured behaviour".
It was not. Nothing had been measured, and the ordering was wrong.

Phase 5 measured it. Each feature was composited over three backdrops and
encoded at the compositor's starting quality of 0.75, against the 80 KB cap.
The number that matters is the delta over the bare backdrop — the backdrop's own
cost is not the overlay's fault.

| feature | over black | over a dark frame | over a busy frame |
|---|---|---|---|
| text, plain | +5.0 KB | +4.6 KB | +3.7 KB |
| text + pill | +5.0 KB | +5.5 KB | +4.8 KB |
| text + outline 2 | **+3.5 KB** | **+4.8 KB** | +5.4 KB |
| text + glow 6 | +5.4 KB | +4.9 KB | +3.7 KB |
| text + glow 12 | +6.7 KB | +5.9 KB | +4.4 KB |
| card, flat | +0.0 KB | +0.5 KB | **−1.2 KB** |
| card, gradient | +3.2 KB | +2.5 KB | +1.3 KB |
| bar, continuous | +2.1 KB | +1.9 KB | +1.2 KB |
| bar, 12 segments | +2.3 KB | +2.1 KB | +1.3 KB |
| **10-layer HUD** | **+31.9 KB** | **+26.9 KB** | **+15.2 KB** |

What this actually says:

- **The effect you pick is nearly irrelevant.** Every single-feature delta lands
  between −1.2 and +6.7 KB, against an 80 KB cap. The differences the old §7.1
  organised its guidance around are 1–2 KB — noise.
- **Outline was not the expensive option.** It is *cheaper* than a background
  pill on two of the three backdrops. The prompt was telling the model
  "costly — prefer pill" on the strength of an assumption that is false.
- **Segmented bars were not dear either.** Twelve segments cost +0.2 KB over a
  continuous bar. The hard-edge reasoning was sound and the magnitude was not.
- **Gradients are not free**, which is the one place the old text erred in the
  optimistic direction: a shaded card costs 2–3 KB more than a flat one, because
  a flat fill is a DC coefficient and a ramp is not.
- **An opaque panel over busy video makes the frame smaller.** Covering detail
  removes more entropy than the panel adds. This is why the deltas shrink from
  left to right across the table — the busier the video, the less an overlay
  costs.
- **What actually costs is layer count and covered area.** The HUD is 5–10x any
  single feature. If a size budget ever needs defending, that is the only lever
  worth pulling.

And the question underneath all of it: the panel does not drop an oversized
frame, it re-encodes it lower, so the real price of an overlay is picture quality
given up by the rest of the frame. Measured that way, over all three backdrops:

| backdrop | quality bare | with the full HUD |
|---|---|---|
| black | 0.75 | 0.75 |
| dark frame | 0.75 | 0.75 |
| busy frame | 0.75 | 0.75 |

**Nothing gives up any quality at all.** The heaviest overlay over the hardest
frame reaches 78% of the cap and rate control never engages.

One methodological note, because the first two attempts at the "busy" backdrop
were worthless in the same way. Per-pixel random noise encoded to 330 KB and
6 px random blocks to 252 KB — three to four times the cap. Both swamped every
delta and both made flat panels register as *negative* cost, for covering noise
up. A frame like that never reaches the panel, since rate control would have
crushed the quality long before. The harness now searches for the coarsest
texture that still lands under the cap, which is the only frame where the
overlay's cost could decide anything.

### 7.2 Glow without a blur

WPF's `BlurEffect` on a `DrawingVisual` feeding a `RenderTargetBitmap` is slow
and unpredictable, and the render already costs ~3 ms.

Instead: draw the text geometry several times with increasing stroke width and
decreasing alpha, then the fill on top. Three or four passes gives a convincing
soft glow, costs a few tenths of a millisecond, and is entirely predictable.

Gradients use `LinearGradientBrush`, which the renderer already builds for
`BarLayer.FillColourTo` — extending it to panels and text is reuse, not new work.

---

## 8. History graphs

The most work, and the one thing here that is about capability rather than
style. Deferred from the original overlay plan; worth doing now that the rest
exists.

### 8.1 The ring buffer

`SensorRegistry` keeps a fixed-length history per numeric sensor — 120 samples,
two minutes at the default poll rate. At 74 sensors that is about 71 KB, which
is not worth optimising.

`SensorSnapshot` does **not** copy it. Copying 74 arrays per frame to serve the
one or two layers that want them would be absurd. Instead the snapshot exposes
`History(id)` which copies that single buffer on demand, and only a graph layer
ever calls it.

### 8.2 `GraphLayer`

Line, area or bar. Fill and stroke from the theme, optional baseline, optional
min/max labels, window length in seconds.

### 8.3 The cost, predicted and then measured

The prediction was that a graph would skip "far less", because its picture
changes whenever the window slides even if the reading has not moved. That
turned out to be wrong, for a reason worth keeping.

Measured over 200 ticks at 10 Hz against a 500 ms poll:

| profile | drawn | skipped |
|---|---|---|
| text only | 42 | 79% |
| text + bar | 44 | 78% |
| **text + graph** | **44** | **78%** |

**A graph costs nothing over a bar.** The floor for any live layer is one redraw
per poll — that is when a new sample exists, and nothing finer can change the
picture. A bar bound to a live sensor already pays exactly that, so a graph
beside it is free. The worry assumed a graph would redraw per *tick*; it redraws
per *sample*, and there are five ticks to a sample at these rates.

The one thing this does mean: a profile of nothing but static text skips more
than one containing any live layer at all. That is the real distinction, and it
was never about graphs.

Getting this number needed three profiles, not two. The first attempt compared
"text + bar" against "text + bar + graph", found no difference, and proved
nothing — the bar had already spent the cost the graph was being blamed for.

---

## 9. What the AI sees

Vocabulary added to the system prompt:

- The theme names and one line describing each.
- The style block's ten fields.
- The icon names — the list, not the codepoints.
- Font and shape roles.

Budget: currently **~1500 tokens**; this adds roughly **300**. The sensor list
remains the largest single part and is unchanged.

Two worked examples are added to the existing three, both chosen to show
composition rather than fields:

- *"Make a HUD-style overlay with CPU and GPU"* — theme, brackets, icons,
  condensed font, one coherent result.
- *"Make the GPU bar segmented and orange"* — a targeted restyle of an existing
  layer, which is the shape of request the style block exists to answer.

**The risk is real and worth naming.** A wider vocabulary is more for a small
model to get wrong, and the local 35B currently succeeds on every prompt tried.
Mitigations: every style field is optional, every value is validated against a
named set with a fallback, and an unknown value is dropped with a note rather
than failing the layer. The compact-schema principle is unchanged — the model
still never writes geometry or a colour.

---

## 10. Phases

| # | Scope | Done when |
|---|---|---|
| **1** | ✅ **Done.** `OverlayTheme`, four shipped themes, role-based `AccentPalette`, `Density`, role migration, editor picker. | ✅ 8/8 pixel-compared checks. Identical under `minimal`; visibly different under the other three. See §12. |
| **2** | ✅ **Done.** `LayerStyle`, `StyleApplier`, font roles, validation and notes. | ✅ 21/21. Every field reaches the renderer; an empty style block is byte-identical to none. See §12. |
| **3** | ✅ **Done.** `GlyphLayer`, `IconNames` (56 names), Fluent/MDL2 resolution, `icon` kind remapped. | ✅ Every name verified against the installed font, programmatically and by eye. See §12. |
| **4** | ✅ **Done.** Ring, arc, bracket, rule (with fade), chevron; reachable through `style.shape`. | ✅ 23/23. Every kind marks pixels; a HUD-framed cluster is buildable from specs alone. See §12. |
| **5** ✅ | Gradients on panels and text; multi-pass glow. Measure the encoded size against §7.1. | Done. Frames stay inside the cap with no quality given up at all; the size claim was verified and turned out to be **wrong**, and §7.1 is rewritten with the measurements. |
| **6** ✅ | Ring buffer, `SensorSnapshot.History`, `GraphLayer`. Measure the skip-rate change. | Done. A real 8-second all-core load spike reads 9% → 100% in the trace; the skip rate does not change at all (78% with a graph, 78% with just a bar). |
| **7** | ✅ **Done.** Themes, style block, icons and shapes in the prompt; two styled examples; `theme` on the plan. | ✅ All five live prompts clean. The originals still work; "HUD style" produces something recognisably HUD. See §12. |
| **8** ✅ | Editor: style fields in `PropertyPanel`, theme picker, icon picker. | Done. All seven layer types build a pane, all eight shape kinds, and glow/gradient/graph settings survive a JSON round trip. |
| **9** ✅ | Docs, `AGENTS.md`, release notes. | Done. README gained an Overlays section over a real video frame; `AGENTS.md` gained the overlay, AI and sensor context; version bumped to 1.5.0. |

Phases 1–2 carry most of the visible improvement and add **no rendering code at
all**. Worth doing and looking at before committing to the rest.

Phase 8 is the largest by volume and deliberately last: the editor should expose
a vocabulary that has settled, not one still moving.

---

## 11. Risks and non-goals

**Regression is the main risk, not the new features.** Every phase must leave an
untouched profile rendering exactly as before. Phase 2's acceptance criterion is
deliberately "byte for byte" for that reason — a style system that quietly
changes existing overlays is worse than no style system.

**A wider schema may exceed a small model.** Mitigated per §9, and testable:
the live suite already runs the three original prompts, and every phase re-runs
them. If the 35B starts failing them, the vocabulary is too wide and should be
narrowed rather than defended.

**Graphs were expected to cost the skip optimisation** (§8.3). Measured, they
cost nothing over a bar — the floor is one redraw per sample, which any live
layer already pays.

**Themes will make everything look like one of four things.** That is the
trade for coherence, and the style block is the escape hatch. A fifth theme is
cheap to add; freeform per-layer colour is not, because that is exactly what
produces the muddy output the palette exists to prevent.

**Deliberately not doing:** per-layer custom fonts by name, arbitrary hex from
the model, image generation for icon layers, animation or transitions (the panel
is a frame stream; anything time-varying is already possible by making a sensor
move), and gradient *angles* beyond horizontal and vertical.

---

## 12. Results

### Phase 1 — themes

Themes resolve at **draw time**, not when a layer is made. A layer stores
`"warm"` and `Palette.Literal` asks the profile's theme what that means, which
is why switching theme restyles a whole profile rather than only affecting
whatever is generated next. Colours, fonts and corner radii all work this way.

A stored `#AARRGGBB` always wins, so a shade picked by hand in the editor is
never reinterpreted.

Verified by **pixel comparison**, not by eye — 8 checks, all passing:

| Check | |
|---|---|
| A profile with no theme set renders identically to `minimal` | ✅ |
| An unknown theme name falls back to `minimal` | ✅ |
| `hud`, `terminal`, `neon` each differ from `minimal` | ✅ |
| `hud` and `terminal` differ from each other | ✅ |
| A literal colour survives a theme switch untouched | ✅ |
| A role colour follows the theme | ✅ |

Every render used **one sensor snapshot**, so a moving reading could not be
mistaken for a theme difference — without that the comparison would prove
nothing.

The three earlier suites — rotation, spec expansion and layout, generator
parse and assemble — were re-run and still pass.

### Two bugs the test found

Both would have shipped, and both made the feature look broken rather than
absent.

**1. The shipped profile ignored themes entirely.** `CreateDefault` and the
model defaults were full of hex literals, so the rule that protects a
hand-picked colour also protected every colour that was never picked by hand.
Switching theme on the default profile changed nothing.

Fixed by converting the shipped profile and the layer defaults to roles. Under
`minimal` every role resolves to exactly the hex it replaced, which is what
keeps the "identical" guarantee true.

**2. A profile made yesterday still ignored them.** Same cause, but for
already-saved work — and not fixable by changing defaults, since a saved profile
serialises every property.

`OverlaySettings.MigrateToRoles` converts the exact values the old palette
emitted, and only those; anything else was chosen deliberately and stays. On the
machine this was built on it moved 5 colours on first load. Invisible under
`minimal`, which is the point.

**And one bug the screenshots found.** The theme picker did nothing on the live
panel even after both fixes. `OverlayService.Refresh` shallow-copies the profile
for the render thread, and that copy listed fields by hand — so the newly added
`Theme` was silently dropped and the renderer always saw `minimal`.

The copy now lives on `OverlayProfile` as `ShallowCopy()`, beside the fields it
has to list, because that is the only place someone adding a field is already
looking.

### Notes

- Fan and gauge track defaults were `#50FFFFFF` and `#60FFFFFF`; both are now
  the `track` role, so a *newly created* gauge has a marginally more opaque
  track than before. Saved gauges are unaffected.
- Density scales the layout's margins and gutters, so it only affects layers as
  they are generated — it cannot reflow a profile that already exists.
- Text shadow is still baked at generation rather than resolved per theme. The
  smallest of the remaining inconsistencies, and the one worth least.

### Phase 2 — the style object

Twelve optional fields, applied **after** the factory's defaults rather than
instead of them. `StyleApplier.Apply` returns immediately when a spec carries no
style block, so the untouched path executes no new code at all — the
byte-for-byte guarantee holds by construction rather than by testing. The
adjacent risk, an *empty* block taking a different path, is tested and passes.

21 checks. Every previously unreachable property now reaches the renderer:

| Reached | Was |
|---|---|
| `font: mono` | implemented, unreachable |
| `fill: "cool->hot"` on a bar | gradient support, unreachable |
| `segments: 16` | segmented bars, unreachable |
| `ticks: 9`, `sweep: 200` | gauge ticks and sweep, unreachable |
| `outline`, `pill`, `radius` | text decoration, unreachable |
| `rotate`, `opacity` | on every layer, unreachable |

**Nonsense is handled rather than obeyed**, which matters because the input is
model-written: 9999 segments capped at 40, a negative opacity clamped, a 4000°
rotation ignored, `"Comic Sans MS"` falling back to the theme, `"chartreuse"`
falling back to neutral. Each reported as a note rather than dropped silently.

Two behaviours worth recording because they are judgement calls, not
mechanics:

- **An explicit colour clears the automatic ramp.** Almost every interesting
  sensor is a percentage or a temperature and would otherwise be re-coloured
  green-amber-red, so asking for a blue bar would silently produce a green one.
  Naming a colour is a decision and wins.
- **A narrowed sweep stays centred.** Asking for 200° rather than 270° keeps the
  gap at the bottom instead of letting the dial rotate round its face, which is
  what a naive `SweepAngle` assignment does.

**One thing the render showed that the assertions could not.** A 1.5px outline
on 22px text is genuinely heavy — the stroke is drawn at double width because
half of it falls inside the glyph, so the fill is nearly swamped. Not a
regression, and not wrong, but it confirms the plan's §7.1 ordering: the prompt
should steer towards `pill` and reserve `outline` for large text. `StyleApplier`
already drops the outline when both are asked for.

### Phase 8 — the editor catches up

Everything the AI can make, a person can now edit and create. Two layer types
had **no properties pane at all** — an AI-generated icon or graph could be moved
and deleted but not changed, which is a dead end rather than a feature. Glow,
shape gradients, arc angles and a rule's fade were all likewise unreachable.

**The icon name is a dropdown, not a text box.** 56 names, and a wrong one draws
a silently wrong picture rather than failing — the one case where free text is
strictly worse.

**A test drives the real `PropertyPanel` on a real STA thread**, building a pane
for all seven layer types and all eight shape kinds. That is the phase 1 crash
class exactly: a pane built for a type nobody checked. Reimplementing the schema
in the test would have passed while the app crashed.

Getting that test running found a trap worth recording: loading `App.xaml` as a
`ResourceDictionary` throws *"cannot create more than one Application instance"*,
because it is the project's `ApplicationDefinition` and parsing it constructs a
second `App`. Instantiate the real `App` instead — simpler, and closer to what
ships.

**Three things only the render showed.** A showcase profile using every new
feature at once, over a video-like frame:

- **Auto-scaled bar columns lie.** A column's height *is* its value, so
  rescaling an idle GPU drew sixteen half-height columns — "steady moderate
  load" about a card doing nothing. Bars ignore auto-scale now; a line only ever
  shows a shape, so zooming it is fair.
- **Then the honest version looked broken**: a truly empty box. Columns have a
  one-pixel floor, so zero reads as a row of stubs. An idle GPU is a real
  reading and the graph should say so.
- **A plot background wants `panel`, not `track`.** Track is the *light*
  unfilled part of a bar, and an area fading to nothing over it vanishes
  entirely. Not a renderer bug — the wrong role — but worth knowing, since the
  two names sound interchangeable and are not.

The area fill also went from 55% to 38% alpha, so the plot shows through it.

### Phase 6 — history graphs

`SensorRegistry` keeps 120 samples of every numeric sensor and `GraphLayer`
draws them as a line, an area or bars. §8.3 above has the skip-rate measurement,
which contradicted the plan in the reassuring direction.

**The ring buffer samples once per tick, not once per publish.** A provider that
skips an id on some tick would otherwise leave that sensor's history advancing at
its own rate, and two graphs side by side would show different spans of time
while looking identical. Sampling after every provider has reported puts them all
on one grid, and a source that stopped reporting holds its last value — which is
what a graph should show anyway.

**The snapshot does not carry history.** Copying 74 arrays per frame to serve the
one layer that wants one would be absurd, so `SensorSnapshot.History(id, seconds)`
reaches back to the registry for the single buffer asked for. The consequence is
stated on the method rather than hidden: those samples are read when you ask, not
when the snapshot was taken. For a picture of the past that is meaningless.

Four bugs, each found by looking rather than by asserting:

- **The graph was not in the render signature at all**, so it would never have
  triggered a redraw. Keyed on the poll version, which is precisely "a new sample
  exists" and nothing more.
- **Bar columns hung outside the plot.** A line spans edge to edge across n−1
  gaps, so its end points sit *on* the boundaries; centring a column there puts
  half of it outside. Columns get n slots instead.
- **A trace at 0% was half cut off**, because a stroke is centred on its path.
  The plot area is inset by half the line width.
- **The area fill drew grey under a green line.** The line took the threshold
  ramp and the fill re-resolved `FillColour` independently, so two rules coloured
  one graph. `FillColour` now decides only *whether* there is a fill.

**Generated graphs auto-scale, with a floor.** Against a full 0–100 range an idle
GPU and 19% memory both drew as a straight line on the floor — technically
correct and useless. But zooming all the way in turns a 1% wobble into a mountain
range, so the window never narrows below a fifth of the sensor's own range.
Memory sitting between 19.0 and 20.0 all minute *is* a flat line, and that is the
honest picture.

**And the test harness had the same bug as the app could have.** The first load
spike used `Task.Run` on every core and measured almost nothing: `SensorRegistry`
polls on a pool timer, so saturating the pool starved the sampler the test was
checking. It collected 13 samples where it should have had 26 and missed the
spike entirely. Dedicated threads now — and it is worth knowing that anything
flooding the thread pool will thin out the app's own history.

### Phase 5 — gradients, glow, and a refuted assumption

Cards take a vertical gradient (`FillColourTo`), text takes a glow
(`GlowRadius`), and both are reachable from a prompt. The glow is four stroked
passes, widest and faintest first, rather than a `BlurEffect` — §7.2's reasoning
held up, and the passes cost a fixed amount instead of one that scales with the
radius.

**The phase's real output was the measurement, and it contradicted the plan.**
§7.1 has been rewritten with the numbers. The short version: outlined text is not
expensive, segmented bars are not expensive, gradients are not free, and the
effect chosen barely matters next to how many layers there are. Five places in
the code repeated the wrong claim — a layer doc, a renderer comment, a
`StyleApplier` comment justifying a behaviour, a schema doc citing §7.1, and,
worst of the five, the line in the system prompt steering the model away from
outlines. All five are corrected.

The behaviour that comment justified — a pill wins when both are asked for —
stayed, because it is right on looks. Only the stated reason was wrong, and a
reason that does not hold is worth replacing even when the code it guards is
fine.

**Gradients are vertical only**, not configurable. A card shaded top to bottom
reads as a lit surface; every other angle mostly reads as a mistake, and an
angle field is one more thing a model can get wrong for no gain.

**The glow ceiling is proportional, and that came from looking.** It shipped
first as a flat clamp at 12 px, which passed its assertions and was wrong. A 1:1
sheet of every radius against every font size the layout engine emits
(`ProfileTest glow`) shows why: 12 px around 44 px text is a halo, and the same
12 px around 22 px text fills in the counters until the readout is a blob. The
rule is now three tenths of the font size, capped at 12 — read off the sheet
rather than picked, since it is the widest rule that keeps every row legible.

That sheet exists because of a misread. A first glance at a downscaled neon
render suggested the glow was swallowing the glyphs; magnified, the culprit was
the theme's drop shadow and the glow was fine. Both the alarm and the all-clear
were guesses about a thumbnail, which is what an effect can never be judged from.

**One note that was worse than silence.** The first proportional clamp announced
"eased a 6 px glow to 6 px" on every layer, because the ceiling was 5.5 and the
format rounded both ends to the same number. A note that reports a change too
small to see, in words that contradict themselves, buries the notes that matter.
Trivial easings are now silent.

### Phase 3 — icons

56 names over the icon font Windows already ships, so there is no asset to
bundle, no file to lose and no image to decode per frame.

**Every codepoint was picked by looking at it.** A tool rendered eleven labelled
sheets of the font's private use area and the map was built from those. This was
not caution for its own sake — the obvious guesses are wrong often enough that a
map written from memory would have shipped `cpu` drawing a keyboard. The sheets
also settled ones there was no way to guess: `E964` really is a memory module,
`EC48` really is a speedometer.

Verified twice over: `HasGlyph` checks each name against the installed font
programmatically, and the whole set is rendered to a labelled sheet so the
*meaning* can be checked too. Presence and correctness are different questions
and the first does not imply the second.

**`kind: "icon"` changed meaning**, deliberately. It used to make an
`ImageLayer` — a file the user picks — which a model can never usefully fill in,
because it cannot know what images exist. `icon` now means a glyph and `image`
keeps the file-backed layer for the editor. Made now, while the only profiles
using the old meaning are ones generated in testing.

Behaviours worth recording:

- **The label is a fallback for the icon field.** A model writing an icon layer
  often puts the subject in `label` instead, and honouring that costs one line.
- **An icon bound to a sensor inherits the ramp**, so a thermometer beside a
  temperature reddens with it rather than staying a fixed colour.
- **An unknown name is dropped with a note**, not drawn. A wrong codepoint
  renders as an empty box or as nothing, and neither throws — so silence here
  would be indistinguishable from a bug.
- **Fluent Icons has no fan or pump glyph.** Gears are the closest honest
  stand-in and are mapped as such, with the substitution named in the code. A
  hardware panel asks for these constantly and a blank space would read as
  broken.
- Centring is on the glyph's **measured ink**, not the font's line box: icon
  fonts carry a full text ascent and descent, and centring on the line box
  leaves every icon sitting visibly high.

### Phase 4 — decorative shapes

Five ornament kinds on the existing `ShapeLayer`, reachable through
`style.shape` rather than as five more layer kinds — which keeps the vocabulary
the model has to hold in mind short.

The assertions check something specific to ornament: that each kind **marks
pixels at all**. Ornament is stroked rather than filled, and a stroke with no
colour is silently invisible — so drawing nothing, not crashing, is the failure
mode here. Every kind is rendered and its lit pixels counted.

Three details that make ornament behave like ornament rather than like a card
that happens to be a different shape:

- **Ornament drops the card fill.** `panel` defaults to a translucent card, so
  without this a stray rectangle sits behind every bracket. The colour moves to
  the stroke, and a fill-only spec still draws — a model that names a colour
  usually means "the colour", not "the fill specifically".
- **Ornament gets a minimum stroke width.** A card's 1px default is right for a
  rule and too thin for a bracket to read as a frame.
- **A rule fades by default.** A hard-ended hairline butting into the edge of a
  cluster reads as a mistake; one that stops reads as a choice.

Synonyms are generous on purpose — `corners` and `frame` both mean bracket,
`divider` means rule, `arrow` means chevron — because these are words a model
reaches for naturally and each one it has to get exactly right is a chance to
fail. An unrecognised word stays a card and is reported.

`Chevron` has no direction property: `Rotation` already exists on every layer
and turning the mark is what direction means.

### Phase 7 — the AI reaches all of it

Phases 1–4 built a vocabulary the model could not see. This is the phase that
delivers the original request.

**The prompt grew from ~1500 to ~2320 tokens** — 812 more, against an estimate
of 300. The icon list and the style block are both larger than planned. Stated
plainly because it is the cost that matters: the risk was always that a wider
vocabulary would break a small model, and a 54% larger prompt is the shape of
that risk.

It did not. Against a local 35B, all five prompts produced clean layouts:

| Prompt | Time | Result |
|---|---|---|
| "Add a clock at the center" | 1.3 s | 1 layer, add |
| "Add GPU usage bottom left" | 1.7 s | 3 layers, add |
| "Create a full stylised overlay with CPU & GPU usage" | 2.9 s | 9 layers, replace |
| **"Make a HUD style overlay with CPU and GPU"** | 4.2 s | 11 layers, **hud** theme, 2 icons, 2 ornaments, 2 segmented bars, 2 ticked gauges, 3 styled fonts |
| **"Give me a terminal look with CPU, GPU, memory and a clock"** | 2.9 s | 13 layers, **terminal** theme, 3 icons |

The first three are the regression that matters. They were passing before the
vocabulary doubled and they still pass, which is the evidence that the schema is
not yet too wide.

**The theme descriptions do real work.** Asked for a terminal look, the model
picked the theme *and* left out ornament and segmented bars — matching that
theme's one-line description, "square, dense, no ornament". Nothing instructed
it to; the description alone was enough. That is the argument for themes being
named and described rather than being a bag of colours.

Two decisions worth recording:

- **A theme only applies on `replace`.** Applying one while adding a single
  layer would restyle the whole panel over a request that never mentioned it.
  "Add a clock" should not repaint everything.
- **The banner names the theme.** A theme change restyles every layer, so it
  must never be the part of a generation nobody mentioned.

And one bug, the same class as the `ShallowCopy` one in phase 1: **`Restore`
put the layers back but not the theme.** Discarding a generation that had
changed the theme would have left half the change behind — the worst kind of
undo. Both halves are restored now.
