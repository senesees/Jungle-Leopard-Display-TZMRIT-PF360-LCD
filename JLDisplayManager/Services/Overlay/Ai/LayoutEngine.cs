using System;
using System.Collections.Generic;
using System.Linq;

using JLDisplayManager.Models.Overlay;

namespace JLDisplayManager.Services.Overlay.Ai;

/// <summary>
/// Decides where generated layers go.
///
/// Language models are good at intent and bad at pixel arithmetic. Asked for
/// coordinates they will cheerfully overlap two clusters, or put something at
/// y=700 on a 480-tall panel. So the model says <i>where-ish</i> — an anchor and
/// a group — and this turns that into geometry.
///
/// Three rules do most of the work:
///
///   1. Layers sharing an anchor stack away from it rather than piling up. This
///      is what makes "GPU usage bottom left" a readout with a bar under it
///      instead of two things in the same place.
///   2. A panel in a group is sized to what it backs, so the model never has to
///      compute a card's extents.
///   3. Everything is clamped into the design surface, which is already
///      rotation-aware — so a portrait mounting works with no extra thought.
///
/// On an "add", existing layers are obstacles: a new cluster at an occupied
/// corner starts below what is already there rather than covering it.
/// </summary>
public static class LayoutEngine
{
    private const double Margin = 16;    // from the panel edge
    private const double Gutter = 8;     // between layers in a cluster
    private const double GroupGap = 14;  // between clusters at the same anchor
    private const double PanelPad = 12;  // inside a backing card

    /// <summary>
    /// The spacing constants above, scaled by a theme's density.
    ///
    /// Passed down rather than held in mutable statics: layout is not concurrent
    /// today, but a static that <see cref="Place"/> writes on entry would be a
    /// trap for whoever makes it so. Struct, so passing it costs nothing.
    /// </summary>
    private readonly struct Metrics
    {
        public Metrics(double density)
        {
            // A theme that asked for zero spacing would stack everything on the
            // same pixel, and one that asked for ten would push every cluster
            // off the panel. Neither is a look.
            double d = Math.Clamp(density, 0.5, 2.0);

            Margin = LayoutEngine.Margin * d;
            Gutter = LayoutEngine.Gutter * d;
            GroupGap = LayoutEngine.GroupGap * d;
            PanelPad = LayoutEngine.PanelPad * d;
        }

        public double Margin { get; }
        public double Gutter { get; }
        public double GroupGap { get; }
        public double PanelPad { get; }

        public static Metrics Default => new(1.0);
    }

    /// <summary>
    /// Places <paramref name="fresh"/> within a surface of the given size,
    /// avoiding <paramref name="existing"/>. Mutates the fresh layers' X, Y and,
    /// for group panels, Width and Height.
    /// </summary>
    public static void Place(IReadOnlyList<OverlayLayer> fresh, IReadOnlyList<OverlayLayer> existing,
        IReadOnlyList<LayerSpec> specs, double surfaceWidth, double surfaceHeight,
        double density = 1.0)
    {
        var m = new Metrics(density);

        // A spec per layer, in the same order, so a layer can find its group.
        var groupOf = new Dictionary<OverlayLayer, string?>();
        for (int i = 0; i < fresh.Count; i++)
            groupOf[fresh[i]] = i < specs.Count ? Normalise(specs[i].Group) : null;

        // Anchor -> the clusters that want it, in the order the model listed
        // them. Ungrouped layers each become their own single-layer cluster, so
        // two unrelated readouts at one corner still stack.
        var byAnchor = new Dictionary<LayerAnchor, List<List<OverlayLayer>>>();

        foreach (OverlayLayer layer in fresh)
        {
            if (!byAnchor.TryGetValue(layer.Anchor, out List<List<OverlayLayer>>? clusters))
                byAnchor[layer.Anchor] = clusters = new List<List<OverlayLayer>>();

            string? group = groupOf[layer];

            List<OverlayLayer>? target = group == null
                ? null
                : clusters.FirstOrDefault(c => groupOf[c[0]] == group);

            if (target == null) clusters.Add(new List<OverlayLayer> { layer });
            else target.Add(layer);
        }

        // Fit each cluster to its share of the width BEFORE stacking. Two
        // clusters at opposite corners of a narrow surface would otherwise meet
        // in the middle: a 220-wide readout is comfortable on a 960 panel and
        // half the width of a 480 one, and nothing about the anchors alone
        // notices that.
        BudgetWidths(byAnchor, surfaceWidth, m);

        foreach ((LayerAnchor anchor, List<List<OverlayLayer>> clusters) in byAnchor)
        {
            // How far in from this anchor the existing layout already reaches,
            // so an "add" lands beside what is there rather than on it.
            double used = OccupiedDepth(anchor, existing, surfaceWidth, surfaceHeight, m);

            // Dials go side by side, not one above the other. A gauge is square
            // and self-contained, and a row of them is how every dashboard
            // presents dials; stacking them wastes the height a panel has least
            // of and reads as a list rather than an instrument cluster.
            double railX = 0;
            double railHeight = 0;

            foreach (List<OverlayLayer> cluster in clusters)
            {
                bool dials = cluster.All(l => l is GaugeLayer);

                if (dials)
                {
                    double width = cluster.Max(l => l.Width);

                    // Out of room on this rail: drop to a new one.
                    if (railX > 0 && m.Margin + railX + width > surfaceWidth - m.Margin)
                    {
                        used += railHeight + m.GroupGap;
                        railX = 0;
                        railHeight = 0;
                    }

                    LayoutRow(cluster, anchor, used, railX, m);

                    railX += width + m.GroupGap;
                    railHeight = Math.Max(railHeight, cluster.Max(l => l.Height));
                    continue;
                }

                // Anything else ends the rail and stacks below it.
                if (railHeight > 0)
                {
                    used += railHeight + m.GroupGap;
                    railX = 0;
                    railHeight = 0;
                }

                double height = LayoutCluster(cluster, groupOf, anchor, used, m);
                used += height + m.GroupGap;
            }
        }

        foreach (OverlayLayer layer in fresh) Clamp(layer, surfaceWidth, surfaceHeight, m);
    }

    /// <summary>
    /// Gives every cluster a width it can have without meeting its neighbour.
    ///
    /// The nine anchors form three columns across three rows. Clusters only
    /// compete horizontally with those in the same row, so each row's width is
    /// split between however many of its columns are actually used — one column
    /// gets nearly everything, two get half each. A cluster wider than its share
    /// is scaled down proportionally rather than cropped, so it stays legible
    /// and keeps its proportions.
    /// </summary>
    private static void BudgetWidths(Dictionary<LayerAnchor, List<List<OverlayLayer>>> byAnchor,
        double surfaceWidth, Metrics m)
    {
        foreach (int row in new[] { 0, 1, 2 })
        {
            var inRow = byAnchor.Where(kv => Row(kv.Key) == row).ToList();
            if (inRow.Count == 0) continue;

            int columns = inRow.Select(kv => Column(kv.Key)).Distinct().Count();
            double budget = (surfaceWidth - m.Margin * (columns + 1)) / columns;

            foreach ((LayerAnchor anchor, List<List<OverlayLayer>> clusters) in inRow)
                foreach (List<OverlayLayer> cluster in clusters)
                {
                    // The budget covers the whole footprint, backing card
                    // included, since that is what actually occupies the row.
                    bool boxed = cluster.Any(l => l is ShapeLayer)
                                 && cluster.Any(l => l is not ShapeLayer);

                    double allowance = boxed ? budget - m.PanelPad * 2 : budget;
                    double widest = cluster.Where(l => l is not ShapeLayer)
                                           .Select(l => l.Width)
                                           .DefaultIfEmpty(0)
                                           .Max();

                    if (widest <= allowance || widest <= 0) continue;

                    // Floored: past about two thirds the text stops being
                    // readable at arm's length, and a slightly tight layout is
                    // better than one nobody can read.
                    double scale = Math.Max(0.62, allowance / widest);
                    foreach (OverlayLayer layer in cluster) Scale(layer, scale);
                }
        }
    }

    /// <summary>
    /// Shrinks a layer proportionally, type by type. Scaling the box alone
    /// would leave a 32pt font in a 60px-wide slot, so whatever drives a
    /// layer's apparent size scales with it.
    /// </summary>
    private static void Scale(OverlayLayer layer, double scale)
    {
        layer.Width *= scale;
        layer.Height *= scale;

        switch (layer)
        {
            case TextLayer t:
                t.FontSize *= scale;
                t.ShadowOffsetX *= scale;
                t.ShadowOffsetY *= scale;
                break;

            case BarLayer b:
                b.CornerRadius *= scale;
                break;

            case GaugeLayer g:
                g.Thickness *= scale;
                g.CentreFontSize *= scale;
                g.CaptionFontSize *= scale;
                break;

            case GraphLayer gr:
                gr.CornerRadius *= scale;
                gr.LineWidth = Math.Max(1, gr.LineWidth * scale);
                break;
        }
    }

    private static int Row(LayerAnchor a) => a switch
    {
        LayerAnchor.TopLeft or LayerAnchor.TopCentre or LayerAnchor.TopRight => 0,
        LayerAnchor.MiddleLeft or LayerAnchor.Centre or LayerAnchor.MiddleRight => 1,
        _ => 2,
    };

    private static int Column(LayerAnchor a) => a switch
    {
        LayerAnchor.TopLeft or LayerAnchor.MiddleLeft or LayerAnchor.BottomLeft => 0,
        LayerAnchor.TopCentre or LayerAnchor.Centre or LayerAnchor.BottomCentre => 1,
        _ => 2,
    };

    // -----------------------------------------------------------------------

    /// <summary>
    /// Stacks one cluster starting <paramref name="depth"/> in from its anchor,
    /// and returns how tall it ended up. A panel in the cluster is fitted around
    /// the rest and pushed behind it.
    /// </summary>
    private static double LayoutCluster(List<OverlayLayer> cluster,
        Dictionary<OverlayLayer, string?> groupOf, LayerAnchor anchor, double depth,
        Metrics m)
    {
        List<OverlayLayer> panels = cluster.Where(l => l is ShapeLayer).ToList();
        List<OverlayLayer> content = cluster.Where(l => l is not ShapeLayer).ToList();

        // A cluster of nothing but a panel is a decoration, not a container.
        if (content.Count == 0) content = cluster;

        bool boxed = panels.Count > 0 && content != cluster;
        double inset = boxed ? m.PanelPad : 0;

        double width = content.Max(l => l.Width);
        double y = depth + inset;

        // At a bottom anchor the offset is measured upward, so laying out in
        // list order would put the first layer lowest and read bottom-to-top —
        // a bar above the label it belongs to. Reversing restores the order the
        // model wrote, which is the order it expects to see.
        foreach (OverlayLayer layer in InReadingOrder(content, anchor))
        {
            // Offsets are measured from the anchor, so both the X and Y stored
            // here are distances inward — the model's "bottom left" means 16
            // from the left and 16 up from the bottom, whichever way that is.
            layer.X = m.Margin + inset + Indent(anchor, layer.Width, width);
            layer.Y = m.Margin + y;
            y += layer.Height + m.Gutter;
        }

        double contentHeight = y - depth - inset - m.Gutter;

        foreach (OverlayLayer panel in panels)
        {
            if (!boxed) continue;

            // Sized to what it backs, then placed behind it.
            panel.X = m.Margin;
            panel.Y = m.Margin + depth;
            panel.Width = width + m.PanelPad * 2;
            panel.Height = contentHeight + m.PanelPad * 2;
        }

        return boxed
            ? contentHeight + m.PanelPad * 2
            : contentHeight;
    }

    /// <summary>
    /// Places a cluster along a rail rather than down a column — used for
    /// dials. <paramref name="railX"/> is how far along the rail it starts,
    /// measured inward from the anchor like every other offset here.
    /// </summary>
    private static void LayoutRow(List<OverlayLayer> cluster, LayerAnchor anchor,
        double depth, double railX, Metrics m)
    {
        double x = railX;

        // A right anchor measures X leftward, so the same reversal that fixes
        // vertical stacking at the bottom fixes horizontal order on the right.
        foreach (OverlayLayer layer in InReadingOrder(cluster, anchor, horizontal: true))
        {
            layer.X = m.Margin + x;
            layer.Y = m.Margin + depth;
            x += layer.Width + m.Gutter;
        }
    }

    /// <summary>
    /// The order to assign offsets in so the result reads the way it was
    /// written. Offsets run inward from the anchor, which means they run
    /// backwards at the bottom and right edges.
    /// </summary>
    private static IEnumerable<OverlayLayer> InReadingOrder(List<OverlayLayer> layers,
        LayerAnchor anchor, bool horizontal = false)
    {
        bool reverse = horizontal ? Column(anchor) == 2 : Row(anchor) == 2;
        return reverse ? Enumerable.Reverse(layers) : layers;
    }

    /// <summary>
    /// Keeps a narrow layer aligned with its cluster: flush left at a left
    /// anchor, flush right at a right one, centred in the middle. Without this a
    /// short bar under a long readout would sit ragged.
    /// </summary>
    private static double Indent(LayerAnchor anchor, double width, double clusterWidth)
    {
        double slack = Math.Max(0, clusterWidth - width);

        return anchor switch
        {
            LayerAnchor.TopRight or LayerAnchor.MiddleRight or LayerAnchor.BottomRight => 0,
            LayerAnchor.TopCentre or LayerAnchor.Centre or LayerAnchor.BottomCentre => slack / 2,
            _ => 0,
        };
    }

    /// <summary>
    /// How far the existing layout already extends inward from an anchor, so new
    /// work can start past it. Measured as a depth rather than by testing every
    /// rectangle: clusters stack in one direction, so one number is enough and
    /// the result stays predictable.
    /// </summary>
    private static double OccupiedDepth(LayerAnchor anchor, IReadOnlyList<OverlayLayer> existing,
        double surfaceWidth, double surfaceHeight, Metrics m)
    {
        double deepest = 0;

        foreach (OverlayLayer layer in existing)
        {
            if (layer.Anchor != anchor || !layer.Enabled) continue;

            // Y is the inward distance for every anchor except the middle row,
            // where it is an offset from centre and says nothing about depth.
            double depth = layer.Y + layer.Height;
            if (depth > deepest) deepest = depth;
        }

        // Nothing there: start at the margin. Something there: start past it,
        // measured from the margin the first cluster used.
        return deepest <= 0 ? 0 : deepest - m.Margin + m.GroupGap;
    }

    /// <summary>
    /// Pulls a layer back inside the surface.
    ///
    /// Anchors mean offsets are inward distances, so a layer is off-panel when
    /// its offset plus its size exceeds the surface. Clamping in anchor space
    /// keeps the layer attached to the corner it was asked for rather than
    /// silently relocating it.
    /// </summary>
    private static void Clamp(OverlayLayer layer, double surfaceWidth, double surfaceHeight,
        Metrics m)
    {
        layer.Width = Math.Min(layer.Width, surfaceWidth - m.Margin);
        layer.Height = Math.Min(layer.Height, surfaceHeight - m.Margin);

        bool centredX = layer.Anchor is LayerAnchor.TopCentre or LayerAnchor.Centre
            or LayerAnchor.BottomCentre;
        bool centredY = layer.Anchor is LayerAnchor.MiddleLeft or LayerAnchor.Centre
            or LayerAnchor.MiddleRight;

        // A centred offset runs either way from the middle, so it is bounded by
        // half the surface rather than by the whole of it.
        if (centredX)
        {
            double limit = (surfaceWidth - layer.Width) / 2;
            layer.X = Math.Clamp(layer.X, -limit, limit);
        }
        else
        {
            layer.X = Math.Clamp(layer.X, 0, Math.Max(0, surfaceWidth - layer.Width));
        }

        if (centredY)
        {
            double limit = (surfaceHeight - layer.Height) / 2;
            layer.Y = Math.Clamp(layer.Y, -limit, limit);
        }
        else
        {
            layer.Y = Math.Clamp(layer.Y, 0, Math.Max(0, surfaceHeight - layer.Height));
        }
    }

    /// <summary>
    /// Applies a model's optional nudge. Kept separate from placement so the
    /// stacking above stays predictable and the offset is plainly an override.
    /// </summary>
    public static void ApplyOffsets(IReadOnlyList<OverlayLayer> layers,
        IReadOnlyList<LayerSpec> specs, double surfaceWidth, double surfaceHeight,
        double density = 1.0)
    {
        var m = new Metrics(density);

        for (int i = 0; i < layers.Count && i < specs.Count; i++)
        {
            double[]? offset = specs[i].Offset;
            if (offset is not { Length: 2 }) continue;

            layers[i].X += offset[0];
            layers[i].Y += offset[1];
            Clamp(layers[i], surfaceWidth, surfaceHeight, m);
        }
    }

    private static string? Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();
}
