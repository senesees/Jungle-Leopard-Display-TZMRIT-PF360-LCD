using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Overlay;

using ColorDialog = System.Windows.Forms.ColorDialog;
using DialogResult = System.Windows.Forms.DialogResult;

namespace JLDisplayManager.Views.Overlay;

/// <summary>
/// The editor's right-hand properties pane.
///
/// Built in code from an explicit per-type schema rather than by reflection or
/// by thousands of lines of XAML. Reflection would give the user field names;
/// XAML would give five nearly-identical blocks to keep in step. A schema keeps
/// the labels human and the layout in one place.
/// </summary>
public sealed class PropertyPanel : StackPanel
{
    private Action _changed = () => { };
    private Func<string?>? _pickToken;

    /// <summary>Rebuilds the pane for a layer. Null clears it.</summary>
    public void Show(OverlayLayer? layer, Action changed, Func<string?> pickToken)
    {
        _changed = changed;
        _pickToken = pickToken;

        Children.Clear();
        if (layer == null)
        {
            Children.Add(Hint("Select a layer, or add one from the list on the left."));
            return;
        }

        Section(layer.GetType().Name.Replace("Layer", "").ToUpperInvariant());
        Text("Name", () => layer.Name, v => layer.Name = v);
        Enum<LayerAnchor>("Anchor", () => layer.Anchor, v =>
        {
            // Reanchoring has to preserve the on-screen position; the canvas
            // owns that conversion.
            AnchorChanged?.Invoke(layer, v);
        });

        Section("POSITION");
        Row(
            Number("X", () => layer.X, v => layer.X = v),
            Number("Y", () => layer.Y, v => layer.Y = v));
        Row(
            Number("Width", () => layer.Width, v => layer.Width = Math.Max(4, v)),
            Number("Height", () => layer.Height, v => layer.Height = Math.Max(4, v)));
        Row(
            Number("Rotation", () => layer.Rotation, v => layer.Rotation = v),
            Number("Opacity", () => layer.Opacity, v => layer.Opacity = Math.Clamp(v, 0, 1), "0.00"));

        switch (layer)
        {
            case TextLayer t: BuildText(t); break;
            case BarLayer b: BuildBar(b); break;
            case GaugeLayer g: BuildGauge(g); break;
            case ShapeLayer s: BuildShape(s); break;
            case GlyphLayer gl: BuildGlyph(gl); break;
            case GraphLayer gr: BuildGraph(gr); break;
            case ImageLayer i: BuildImage(i); break;
        }

        Section("VISIBILITY");
        Enum<VisibilityRule>("Show", () => layer.VisibleWhen, v => layer.VisibleWhen = v);
        if (layer.VisibleWhen is VisibilityRule.SensorAbove or VisibilityRule.SensorBelow)
        {
            Text("Sensor", () => layer.VisibleSource ?? "", v => layer.VisibleSource = v, token: true);
            Number("Threshold", () => layer.VisibleThreshold, v => layer.VisibleThreshold = v);
        }
    }

    /// <summary>Raised when the anchor combo changes, so the canvas can keep the layer put.</summary>
    public event Action<OverlayLayer, LayerAnchor>? AnchorChanged;

    // -----------------------------------------------------------------------
    // Per-type schemas
    // -----------------------------------------------------------------------

    private void BuildText(TextLayer t)
    {
        Section("TEXT");
        Text("Template", () => t.Template, v => t.Template = v, token: true);
        Text("Font", () => t.FontFamily, v => t.FontFamily = v);
        Row(
            Number("Size", () => t.FontSize, v => t.FontSize = Math.Max(1, v)),
            Enum<TextAlign>("Align", () => t.Align, v => t.Align = v));
        Row(
            Bool("Bold", () => t.Bold, v => t.Bold = v),
            Bool("Italic", () => t.Italic, v => t.Italic = v));
        Row(
            Bool("Wrap", () => t.Wrap, v => t.Wrap = v),
            Number("Line height", () => t.LineHeight, v => t.LineHeight = Math.Max(0, v)));
        Colour("Colour", () => t.Colour, v => t.Colour = v);

        Section("LEGIBILITY");
        Children.Add(Hint("Over video, plain text is often unreadable. An outline, "
                          + "a glow or a pill all fix that, and measured against the "
                          + "size cap they cost about the same — pick on looks."));
        Row(
            Number("Outline", () => t.OutlineWidth, v => t.OutlineWidth = Math.Max(0, v)),
            ColourBox("Outline colour", () => t.OutlineColour, v => t.OutlineColour = v));
        Row(
            Number("Shadow X", () => t.ShadowOffsetX, v => t.ShadowOffsetX = v),
            Number("Shadow Y", () => t.ShadowOffsetY, v => t.ShadowOffsetY = v));
        Row(
            Number("Glow", () => t.GlowRadius, v => t.GlowRadius = Math.Max(0, v)),
            ColourNullable("Glow colour", () => t.GlowColour, v => t.GlowColour = v));
        Children.Add(Hint("A glow reads best at about a quarter of the font size. "
                          + "Leave its colour empty to follow the text."));
        ColourNullable("Pill", () => t.BackgroundColour, v => t.BackgroundColour = v);
        Row(
            Number("Pill radius", () => t.BackgroundRadius, v => t.BackgroundRadius = Math.Max(0, v)),
            Number("Pill padding", () => t.BackgroundPadding, v => t.BackgroundPadding = Math.Max(0, v)));

        Section("THRESHOLD COLOURS");
        Text("Driven by", () => t.ThresholdSource ?? "", v => t.ThresholdSource = Empty(v), token: true);
        Thresholds(t.Thresholds);
    }

    private void BuildBar(BarLayer b)
    {
        Section("BAR");
        Text("Sensor", () => b.Source, v => b.Source = v, token: true);
        Row(
            NumberNullable("Min", () => b.Min, v => b.Min = v),
            NumberNullable("Max", () => b.Max, v => b.Max = v));
        Children.Add(Hint("Leave Min and Max empty to use the sensor's own range."));
        Row(
            Enum<BarOrientation>("Direction", () => b.Orientation, v => b.Orientation = v),
            Bool("Reversed", () => b.Reversed, v => b.Reversed = v));
        Row(
            Number("Corner radius", () => b.CornerRadius, v => b.CornerRadius = Math.Max(0, v)),
            Number("Segments", () => b.Segments, v => b.Segments = Math.Max(0, (int)v), "0"));
        Number("Segment gap", () => b.SegmentGap, v => b.SegmentGap = Math.Max(0, v));

        Section("COLOURS");
        Colour("Track", () => b.TrackColour, v => b.TrackColour = v);
        Colour("Fill", () => b.FillColour, v => b.FillColour = v);
        ColourNullable("Gradient to", () => b.FillColourTo, v => b.FillColourTo = v);
        ColourNullable("Border", () => b.BorderColour, v => b.BorderColour = v);
        Number("Border width", () => b.BorderWidth, v => b.BorderWidth = Math.Max(0, v));
        Thresholds(b.Thresholds);
    }

    private void BuildGauge(GaugeLayer g)
    {
        Section("GAUGE");
        Text("Sensor", () => g.Source, v => g.Source = v, token: true);
        Row(
            NumberNullable("Min", () => g.Min, v => g.Min = v),
            NumberNullable("Max", () => g.Max, v => g.Max = v));
        Row(
            Number("Start angle", () => g.StartAngle, v => g.StartAngle = v),
            Number("Sweep", () => g.SweepAngle, v => g.SweepAngle = v));
        Children.Add(Hint("135° start with a 270° sweep is the classic dial. "
                          + "A negative sweep runs anticlockwise."));
        Row(
            Number("Thickness", () => g.Thickness, v => g.Thickness = Math.Max(1, v)),
            Bool("Round caps", () => g.RoundCaps, v => g.RoundCaps = v));
        Row(
            Number("Ticks", () => g.Ticks, v => g.Ticks = Math.Max(0, (int)v), "0"),
            ColourBox("Tick colour", () => g.TickColour, v => g.TickColour = v));

        Section("CENTRE");
        Text("Centre text", () => g.CentreTemplate, v => g.CentreTemplate = v, token: true);
        Row(
            Number("Size", () => g.CentreFontSize, v => g.CentreFontSize = Math.Max(1, v)),
            ColourBox("Colour", () => g.CentreColour, v => g.CentreColour = v));
        Text("Caption", () => g.Caption, v => g.Caption = v, token: true);
        Row(
            Number("Caption size", () => g.CaptionFontSize, v => g.CaptionFontSize = Math.Max(1, v)),
            ColourBox("Caption colour", () => g.CaptionColour, v => g.CaptionColour = v));
        Text("Font", () => g.FontFamily, v => g.FontFamily = v);

        Section("COLOURS");
        Colour("Track", () => g.TrackColour, v => g.TrackColour = v);
        Colour("Fill", () => g.FillColour, v => g.FillColour = v);
        Thresholds(g.Thresholds);
    }

    private void BuildShape(ShapeLayer s)
    {
        Section("SHAPE");
        Enum<ShapeKind>("Kind", () => s.Kind, v => s.Kind = v);
        ColourNullable("Fill", () => s.FillColour, v => s.FillColour = v);
        ColourNullable("Gradient to", () => s.FillColourTo, v => s.FillColourTo = v);
        ColourNullable("Stroke", () => s.StrokeColour, v => s.StrokeColour = v);
        Row(
            Number("Stroke width", () => s.StrokeWidth, v => s.StrokeWidth = Math.Max(0, v)),
            Number("Corner radius", () => s.CornerRadius, v => s.CornerRadius = Math.Max(0, v)));

        if (s.Kind is ShapeKind.Arc or ShapeKind.Ring)
        {
            Row(
                Number("Start angle", () => s.StartAngle, v => s.StartAngle = v),
                Number("Sweep", () => s.SweepAngle, v => s.SweepAngle = v));
        }

        if (s.Kind == ShapeKind.Rule)
            Bool("Fade at the ends", () => s.Fade, v => s.Fade = v);
    }

    /// <summary>
    /// An icon from the system font. The name is a dropdown rather than a text
    /// box: there are 56 of them, a wrong one draws a silently wrong picture,
    /// and nobody can be expected to remember the list.
    /// </summary>
    private void BuildGlyph(GlyphLayer gl)
    {
        Section("ICON");

        var combo = new ComboBox { ItemTemplate = TryFindResource("ComboText") as DataTemplate };
        foreach (string name in IconNames.All) combo.Items.Add(name);
        combo.SelectedItem = gl.Icon;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string picked) { gl.Icon = picked; _changed(); }
        };

        Labelled("Icon", combo);

        Number("Size", () => gl.Size, v => gl.Size = Math.Max(0, v));
        Children.Add(Hint("Size 0 fits the glyph to the layer box."));
        Colour("Colour", () => gl.Colour, v => gl.Colour = v);

        Section("THRESHOLD COLOURS");
        Text("Driven by", () => gl.ThresholdSource ?? "",
            v => gl.ThresholdSource = Empty(v), token: true);
        Thresholds(gl.Thresholds);
    }

    private void BuildGraph(GraphLayer gr)
    {
        Section("GRAPH");
        Text("Sensor", () => gr.Source, v => gr.Source = v, token: true);
        Row(
            Enum<GraphStyle>("Style", () => gr.Style, v => gr.Style = v),
            Number("Window (s)", () => gr.WindowSeconds,
                v => gr.WindowSeconds = Math.Clamp(v, 2, 120), "0"));
        Children.Add(Hint("Two minutes of history is kept, so a longer window "
                          + "shows what there is."));
        Row(
            NumberNullable("Min", () => gr.Min, v => gr.Min = v),
            NumberNullable("Max", () => gr.Max, v => gr.Max = v));
        Bool("Auto-scale to the window", () => gr.AutoScale, v => gr.AutoScale = v);
        Children.Add(Hint("Auto-scale reads a trend that a full range would "
                          + "flatten, at the cost of a scale that keeps moving."));

        Section("COLOURS");
        Colour("Line", () => gr.LineColour, v => gr.LineColour = v);
        ColourNullable("Area fill", () => gr.FillColour, v => gr.FillColour = v);
        Children.Add(Hint("The area takes the line's colour; this only decides "
                          + "whether there is one."));
        ColourNullable("Plot background", () => gr.BackgroundColour,
            v => gr.BackgroundColour = v);
        Row(
            Number("Line width", () => gr.LineWidth, v => gr.LineWidth = Math.Max(0.5, v)),
            Number("Corner radius", () => gr.CornerRadius, v => gr.CornerRadius = Math.Max(0, v)));
        Row(
            NumberNullable("Baseline", () => gr.Baseline, v => gr.Baseline = v),
            ColourBox("Baseline colour", () => gr.BaselineColour, v => gr.BaselineColour = v));
        Thresholds(gr.Thresholds);
    }

    private void BuildImage(ImageLayer i)
    {
        Section("IMAGE");
        Text("File", () => i.File, v => i.File = v);
        Children.Add(Hint("A file name inside the overlay assets folder, or a full path."));
        Bool("Keep aspect ratio", () => i.PreserveAspect, v => i.PreserveAspect = v);
    }

    // -----------------------------------------------------------------------
    // Widgets
    // -----------------------------------------------------------------------

    private void Section(string title)
    {
        Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, Children.Count == 0 ? 0 : 14, 0, 6),
            Style = TryFindResource("Heading") as Style,
        });
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Margin = new Thickness(0, 0, 0, 8),
        Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0x8F, 0x80)),
    };

    private void Row(UIElement a, UIElement b)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The helpers have already appended these, so detach them from this
        // panel BEFORE reparenting. Adding first would briefly give each element
        // two logical parents, which WPF refuses outright.
        Children.Remove(a);
        Children.Remove(b);

        Grid.SetColumn(a, 0);
        Grid.SetColumn(b, 2);
        g.Children.Add(a);
        g.Children.Add(b);

        Children.Add(g);
    }

    private StackPanel Labelled(string label, UIElement editor)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0x8F, 0x80)),
        });
        sp.Children.Add(editor);
        Children.Add(sp);
        return sp;
    }

    private StackPanel Text(string label, Func<string> get, Action<string> set, bool token = false)
    {
        var box = new TextBox { Text = get() };
        box.LostFocus += (_, _) => { set(box.Text); _changed(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            set(box.Text);
            _changed();
        };

        if (!token) return Labelled(label, box);

        // A token field gets an insert button, because remembering sensor ids is
        // the single most tedious part of writing a template by hand.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pick = new Button
        {
            Content = "+",
            Width = 26,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Insert a sensor",
            Style = TryFindResource("Btn") as Style,
        };
        pick.Click += (_, _) =>
        {
            string? id = _pickToken?.Invoke();
            if (string.IsNullOrEmpty(id)) return;

            int at = box.SelectionStart;
            box.Text = box.Text.Insert(at, id);
            box.SelectionStart = at + id.Length;
            set(box.Text);
            _changed();
        };

        Grid.SetColumn(box, 0);
        Grid.SetColumn(pick, 1);
        grid.Children.Add(box);
        grid.Children.Add(pick);

        return Labelled(label, grid);
    }

    private StackPanel Number(string label, Func<double> get, Action<double> set, string fmt = "0.##")
    {
        var box = new TextBox { Text = get().ToString(fmt, CultureInfo.CurrentCulture) };

        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v))
                set(v);

            // Always write back the parsed value: typing nonsense then tabbing
            // away should show what the layer actually holds, not the nonsense.
            box.Text = get().ToString(fmt, CultureInfo.CurrentCulture);
            _changed();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        return Labelled(label, box);
    }

    private StackPanel NumberNullable(string label, Func<double?> get, Action<double?> set)
    {
        var box = new TextBox { Text = get()?.ToString("0.##", CultureInfo.CurrentCulture) ?? "" };

        void Commit()
        {
            if (string.IsNullOrWhiteSpace(box.Text)) set(null);
            else if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                                     out double v)) set(v);

            box.Text = get()?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
            _changed();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        return Labelled(label, box);
    }

    private StackPanel Bool(string label, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 14, 0, 0) };
        check.Checked += (_, _) => { set(true); _changed(); };
        check.Unchecked += (_, _) => { set(false); _changed(); };

        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(check);
        Children.Add(sp);
        return sp;
    }

    private StackPanel Enum<T>(string label, Func<T> get, Action<T> set) where T : struct, Enum
    {
        // ComboText, not a Foreground on the ComboBox: string items render
        // through a generated TextBlock that picks up the app's implicit
        // TextBlock style, and that explicit setter beats anything inherited.
        // App.xaml carries the template and the full explanation.
        var combo = new ComboBox { ItemTemplate = TryFindResource("ComboText") as DataTemplate };
        foreach (T v in System.Enum.GetValues<T>()) combo.Items.Add(Pretty(v.ToString()!));
        combo.SelectedIndex = Array.IndexOf(System.Enum.GetValues<T>(), get());

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            set(System.Enum.GetValues<T>()[combo.SelectedIndex]);
            _changed();
        };

        return Labelled(label, combo);
    }

    private static string Pretty(string name)
    {
        // "BottomRight" -> "Bottom right", so the combo reads like English.
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) { sb.Append(' '); sb.Append(char.ToLower(name[i])); }
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private StackPanel Colour(string label, Func<string> get, Action<string> set) =>
        Labelled(label, ColourEditor(get, v => set(v ?? "#FFFFFFFF")));

    private StackPanel ColourNullable(string label, Func<string?> get, Action<string?> set) =>
        Labelled(label, ColourEditor(get, set));

    /// <summary>A colour editor packaged for <see cref="Row"/>.</summary>
    private StackPanel ColourBox(string label, Func<string> get, Action<string> set) =>
        Colour(label, get, set);

    private UIElement ColourEditor(Func<string?> get, Action<string?> set)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var swatch = new Border
        {
            Width = 24,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x2C, 0x24)),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var box = new TextBox { Text = get() ?? "" };

        void Paint()
        {
            string? v = get();
            swatch.Background = string.IsNullOrWhiteSpace(v)
                ? Brushes.Transparent
                : Palette.Brush(v) ?? Brushes.Transparent;
        }

        void Commit()
        {
            set(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
            box.Text = get() ?? "";
            Paint();
            _changed();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };

        // WPF has no colour picker, and the tray already pulls in Windows Forms
        // for NotifyIcon, so this costs nothing extra. It has no alpha channel,
        // so the existing alpha is preserved and the text box remains the way to
        // set it.
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            byte alpha = 0xFF;
            string? current = get();
            if (!string.IsNullOrWhiteSpace(current))
            {
                try { alpha = Palette.Parse(current).A; } catch { /* keep opaque */ }
            }

            using var dlg = new ColorDialog { FullOpen = true };
            if (!string.IsNullOrWhiteSpace(current))
            {
                try
                {
                    Color c = Palette.Parse(current);
                    dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
                }
                catch { /* start from the default */ }
            }

            if (dlg.ShowDialog() != DialogResult.OK) return;

            box.Text = $"#{alpha:X2}{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            Commit();
        };

        Paint();

        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(swatch);
        grid.Children.Add(box);
        return grid;
    }

    /// <summary>
    /// Threshold stops, as one line of text: "0=#FF4AD995, 70=#FFFFB43A".
    /// A grid of colour pickers would be a lot of surface for something most
    /// profiles set once and never touch.
    /// </summary>
    private void Thresholds(List<ColourStop> stops)
    {
        string ToText() => string.Join(", ", stops.ConvertAll(s =>
            $"{s.AtOrAbove.ToString("0.##", CultureInfo.InvariantCulture)}={s.Colour}"));

        var box = new TextBox { Text = ToText(), TextWrapping = TextWrapping.Wrap };

        void Commit()
        {
            var parsed = new List<ColourStop>();
            foreach (string part in box.Text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] halves = part.Split('=', 2);
                if (halves.Length != 2) continue;
                if (!double.TryParse(halves[0].Trim(), NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out double at)) continue;

                string colour = halves[1].Trim();
                if (Palette.Brush(colour) == null) continue;   // drop anything unparseable

                parsed.Add(new ColourStop { AtOrAbove = at, Colour = colour });
            }

            stops.Clear();
            stops.AddRange(parsed);
            box.Text = ToText();
            _changed();
        }

        box.LostFocus += (_, _) => Commit();
        Labelled("Stops  (value=colour, comma separated)", box);
        Children.Add(Hint("The highest stop the value has reached wins. Leave empty "
                          + "to use the plain colour above."));
    }

    private static string? Empty(string v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
