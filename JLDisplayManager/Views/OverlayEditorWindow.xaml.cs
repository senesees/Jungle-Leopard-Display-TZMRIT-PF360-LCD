using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using JLDisplayManager.Models;
using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Overlay;
using JLDisplayManager.Services.Overlay.Ai;
using JLDisplayManager.Services.Sensors;
using JLDisplayManager.Views.Overlay;

using MessageBox = System.Windows.MessageBox;

namespace JLDisplayManager.Views;

/// <summary>
/// Builds an overlay profile.
///
/// The canvas paints through the same <see cref="OverlayRenderer"/> the panel
/// uses and against the same live sensor values, so what is designed here is
/// what appears on the glass — including the numbers moving while you work.
/// Edits are pushed to the running renderer as they happen; there is no apply
/// step, because the panel is right there and showing it is faster than
/// describing it.
/// </summary>
public partial class OverlayEditorWindow : Window
{
    private readonly App _app = App.Current;
    private readonly DispatcherTimer _tick;

    private OverlayProfile? _profile;
    private bool _loading;
    private int _lastRotate;

    /// <summary>What the canvas last painted, so an unchanged tick does nothing.</summary>
    private string _lastCanvasState = "";

    private readonly EditHistory _history;

    private static readonly double[] Zooms = { 0.5, 0.75, 1.0, 1.25, 1.5 };

    public OverlayEditorWindow()
    {
        InitializeComponent();

        // Before LoadProfiles, so the opening state is the first thing that can
        // be returned to.
        _history = new EditHistory(_app.Overlays);
        _history.Changed += (_, _) => RefreshUndoButtons();

        BackdropBox.ItemsSource = new[] { "Live panel", "Dark", "Mid grey", "Checkerboard" };
        BackdropBox.SelectedIndex = 0;

        ThemeBox.ItemsSource = OverlayTheme.All.Select(t => t.Name).ToList();
        ThemeBox.ToolTip = string.Join("\n",
            OverlayTheme.All.Select(t => $"{t.Name} — {t.Description}"));

        ZoomBox.ItemsSource = Zooms.Select(z => $"{z * 100:0}%").ToArray();

        // 75% rather than 1:1, so the whole 960-wide panel fits the canvas
        // column at the default window size. Opening onto a design surface that
        // needs scrolling before you can see it is a poor first impression.
        ZoomBox.SelectedIndex = Array.IndexOf(Zooms, 0.75);

        (double dw, double dh) = _app.Overlay.DesignSize;
        Canvas.Context = new LayerContext
        {
            AssetDirectory = Storage.OverlayAssetDirectory,
            Width = dw,
            Height = dh,
        };
        _lastRotate = _app.Settings.Rotate;
        Canvas.SelectionChanged += (_, _) => ShowProperties();

        // Split deliberately: LayerChanged fires for every mouse-move of a drag
        // and only pushes the new pixels, while EditCommitted fires once the
        // drag ends and is the only thing that writes the file.
        Canvas.LayerChanged += (_, _) => { Push(); RefreshProperties(); };
        Canvas.EditCommitted += (_, e) => Commit(e.CoalesceKey);

        Properties.AnchorChanged += (layer, anchor) =>
        {
            Canvas.Reanchor(layer, anchor);
            Touch();
            ShowProperties();
        };

        EnabledBox.IsChecked = _app.Overlay.Enabled;

        LoadProfiles();

        // The canvas shows live values, so it has to repaint even when nothing
        // is being edited. 5 Hz is enough for a clock and a load bar and cheap
        // enough not to matter.
        _tick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _tick.Tick += (_, _) => Repaint();
        _tick.Start();

        Closed += (_, _) =>
        {
            _tick.Stop();
            _generating?.Cancel();

            // A pending result was never accepted, so closing the window is a
            // decision not to keep it. Leaving it applied would mean an unsaved
            // change silently becoming the saved one on the next edit.
            Restore();
        };
    }

    // -----------------------------------------------------------------------
    // Profiles
    // -----------------------------------------------------------------------

    private void LoadProfiles()
    {
        _loading = true;

        ProfileBox.ItemsSource = _app.Overlays.Profiles.Select(p => p.Name).ToList();

        int index = _app.Overlays.Profiles.FindIndex(p => p.Id == _app.Overlays.ActiveProfileId);
        ProfileBox.SelectedIndex = index >= 0 ? index : (_app.Overlays.Profiles.Count > 0 ? 0 : -1);

        _loading = false;
        UseProfile(ProfileBox.SelectedIndex);
    }

    private void UseProfile(int index)
    {
        _profile = index >= 0 && index < _app.Overlays.Profiles.Count
            ? _app.Overlays.Profiles[index]
            : null;

        if (_profile != null) _app.Overlays.ActiveProfileId = _profile.Id;

        Canvas.Profile = _profile;
        _app.Overlay.Refresh(_profile);

        // Under _loading so selecting the profile's own theme does not read as
        // the user changing it.
        _loading = true;
        int theme = _profile == null
            ? 0
            : OverlayTheme.All.ToList().FindIndex(t =>
                  string.Equals(t.Name, OverlayTheme.ByName(_profile.Theme).Name,
                      StringComparison.OrdinalIgnoreCase));
        ThemeBox.SelectedIndex = theme >= 0 ? theme : 0;
        _loading = false;

        RebuildLayerList();
        Canvas.Select(_profile?.Layers.LastOrDefault());
        Repaint();
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Canvas == null) return;
        UseProfile(ProfileBox.SelectedIndex);
        Save();
    }

    private void OnNewProfile(object sender, RoutedEventArgs e)
    {
        string? name = Prompt("New profile", "Name", "Untitled");
        if (name == null) return;

        var p = new OverlayProfile { Name = name };
        _app.Overlays.Profiles.Add(p);
        _app.Overlays.ActiveProfileId = p.Id;
        LoadProfiles();
        Commit();
    }

    private void OnDuplicateProfile(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;

        // Round-tripping through JSON is the cheapest correct deep copy here:
        // the layer model is polymorphic, and a hand-written clone would need a
        // new arm every time a layer type is added.
        OverlayProfile? copy = Clone(_profile);
        if (copy == null) return;

        copy.Id = Guid.NewGuid();
        copy.Name = _profile.Name + " copy";
        foreach (OverlayLayer l in copy.Layers) l.Id = Guid.NewGuid();

        _app.Overlays.Profiles.Add(copy);
        _app.Overlays.ActiveProfileId = copy.Id;
        LoadProfiles();
        Commit();
    }

    private void OnRenameProfile(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;

        string? name = Prompt("Rename profile", "Name", _profile.Name);
        if (name == null) return;

        _profile.Name = name;
        LoadProfiles();
        Commit();
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;

        if (_app.Overlays.Profiles.Count == 1)
        {
            MessageBox.Show(this, "There has to be at least one profile.", "Overlay",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Delete \"{_profile.Name}\"?", "Overlay",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _app.Overlays.Profiles.Remove(_profile);
        _app.Overlays.ActiveProfileId = _app.Overlays.Profiles[0].Id;
        LoadProfiles();

        // Deleting a profile is the single thing people most want back.
        Commit();
    }

    private static OverlayProfile? Clone(OverlayProfile p)
    {
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(p);
            return System.Text.Json.JsonSerializer.Deserialize<OverlayProfile>(json);
        }
        catch (Exception ex)
        {
            Storage.Log($"could not copy the overlay profile: {ex.Message}");
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Layers
    // -----------------------------------------------------------------------

    private void RebuildLayerList()
    {
        _loading = true;

        // Reversed, so the topmost layer is at the top of the list — the way a
        // stack of things is normally read.
        var items = new List<string>();
        if (_profile != null)
        {
            for (int i = _profile.Layers.Count - 1; i >= 0; i--)
            {
                OverlayLayer l = _profile.Layers[i];
                string label = string.IsNullOrWhiteSpace(l.Name)
                    ? l.GetType().Name.Replace("Layer", "")
                    : l.Name;

                items.Add($"{(l.Enabled ? "●" : "○")} {label}{(l.Locked ? "  🔒" : "")}");
            }
        }

        LayerList.ItemsSource = items;
        LayerList.SelectedIndex = IndexOf(Canvas.Selected);

        _loading = false;
    }

    private int IndexOf(OverlayLayer? layer)
    {
        if (_profile == null || layer == null) return -1;
        int i = _profile.Layers.IndexOf(layer);
        return i < 0 ? -1 : _profile.Layers.Count - 1 - i;
    }

    private OverlayLayer? At(int listIndex)
    {
        if (_profile == null || listIndex < 0 || listIndex >= _profile.Layers.Count) return null;
        return _profile.Layers[_profile.Layers.Count - 1 - listIndex];
    }

    private void OnLayerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Canvas.Select(At(LayerList.SelectedIndex));
    }

    private void Add(OverlayLayer layer)
    {
        if (_profile == null) return;

        _profile.Layers.Add(layer);
        Canvas.Select(layer);
        RebuildLayerList();
        Touch();
        ShowProperties();
    }

    private void OnAddText(object sender, RoutedEventArgs e) => Add(new TextLayer
    {
        Name = "Text", X = 40, Y = 40, Width = 260, Height = 34,
        Template = "CPU {cpu.load:0}%",
    });

    private void OnAddBar(object sender, RoutedEventArgs e) => Add(new BarLayer
    {
        Name = "Bar", X = 40, Y = 90, Width = 260, Height = 14, Source = "cpu.load",
    });

    private void OnAddGauge(object sender, RoutedEventArgs e) => Add(new GaugeLayer
    {
        Name = "Gauge", X = 40, Y = 120, Width = 120, Height = 120,
        Source = "gpu.load", CentreTemplate = "{gpu.load:0}%", Caption = "GPU",
    });

    private void OnAddGraph(object sender, RoutedEventArgs e) => Add(new GraphLayer
    {
        Name = "Graph", X = 40, Y = 160, Width = 260, Height = 70, Source = "cpu.load",

        // A plot background by default, for the same reason generated graphs get
        // one: a bare trace over video reads as a stray line, not a chart.
        BackgroundColour = "panel",
    });

    private void OnAddGlyph(object sender, RoutedEventArgs e) => Add(new GlyphLayer
    {
        Name = "Icon", X = 40, Y = 40, Width = 48, Height = 48, Icon = "cpu",
    });

    private void OnAddShape(object sender, RoutedEventArgs e) => Add(new ShapeLayer
    {
        Name = "Panel", X = 30, Y = 30, Width = 280, Height = 100, CornerRadius = 10,
    });

    private void OnAddImage(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add an image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        // Copied into the assets folder so the profile keeps working when the
        // original is moved, and stays portable if it is exported.
        string name = System.IO.Path.GetFileName(dlg.FileName);
        string target = System.IO.Path.Combine(Storage.OverlayAssetDirectory, name);
        try
        {
            Storage.EnsureDirectories();
            if (!string.Equals(dlg.FileName, target, StringComparison.OrdinalIgnoreCase))
                System.IO.File.Copy(dlg.FileName, target, overwrite: true);

            OverlayRenderer.ClearImageCache();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not copy that image.\n\n{ex.Message}", "Overlay",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Add(new ImageLayer { Name = name, X = 40, Y = 40, Width = 96, Height = 96, File = name });
    }

    private void OnDuplicateLayer(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Canvas.Selected == null) return;

        var holder = new OverlayProfile { Layers = { Canvas.Selected } };
        OverlayProfile? copy = Clone(holder);
        if (copy == null || copy.Layers.Count == 0) return;

        OverlayLayer l = copy.Layers[0];
        l.Id = Guid.NewGuid();
        l.Name += " copy";
        l.X += 12;
        l.Y += 12;
        Add(l);
    }

    private void OnDeleteLayer(object sender, RoutedEventArgs e)
    {
        if (_profile == null || Canvas.Selected == null) return;

        _profile.Layers.Remove(Canvas.Selected);
        Canvas.Select(_profile.Layers.LastOrDefault());
        RebuildLayerList();
        Touch();
        ShowProperties();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Reorder(+1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Reorder(-1);

    private void Reorder(int delta)
    {
        if (_profile == null || Canvas.Selected == null) return;

        int i = _profile.Layers.IndexOf(Canvas.Selected);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _profile.Layers.Count) return;

        (_profile.Layers[i], _profile.Layers[j]) = (_profile.Layers[j], _profile.Layers[i]);
        RebuildLayerList();
        Touch();
    }

    // -----------------------------------------------------------------------
    // Painting and plumbing
    // -----------------------------------------------------------------------

    private void Repaint()
    {
        Canvas.Values = _app.Overlay.Sensors.Snapshot();
        Canvas.Context.IsPlaying =
            _app.Display.State == Interop.NativeMethods.JlState.Playing;

        // Settings can be changed while this window is open, and turning the
        // panel swaps the design surface between 960x480 and 480x960.
        if (_app.Settings.Rotate != _lastRotate)
        {
            _lastRotate = _app.Settings.Rotate;
            (double w, double h) = _app.Overlay.DesignSize;
            Canvas.Context.Width = w;
            Canvas.Context.Height = h;
            Canvas.InvalidateMeasure();
        }

        Canvas.Rotate = _lastRotate;

        if (BackdropBox.SelectedIndex == 0)
        {
            // Keep the last frame rather than taking null. The preview is
            // cleared whenever one is not ready — between items, or while a
            // still is simply being held — and blanking the backdrop on that
            // made the canvas alternate between the video and a flat fill
            // several times a second. Worse, it flipped the flag below with it,
            // so the layers themselves blinked on and off. That is the flicker.
            BitmapSource? frame = _app.Display.Preview;
            if (frame != null) Canvas.Backdrop = frame;

            Canvas.BackdropIsPanelBuffer = Canvas.Backdrop != null;

            // The panel's own output is taken after compositing, so with the
            // overlay on it already carries the layers. Re-drawing them would
            // double every translucent fill. With the overlay off it is plain
            // video and the layers do need drawing, which is also how you
            // design a profile before switching it on.
            //
            // Un-rotating the backdrop turns the overlay baked into it upright
            // along with the video, so this stays correct on a rotated panel.
            Canvas.BackdropIncludesOverlay =
                _app.Overlay.Enabled && Canvas.Backdrop != null;
        }
        else
        {
            Canvas.Backdrop = null;
            Canvas.BackdropIncludesOverlay = false;
            Canvas.BackdropIsPanelBuffer = false;
        }

        // Only repaint when something visible actually moved. At 5 Hz over a
        // full-size backdrop this was redrawing constantly whether or not
        // anything had changed, which is work the editor does not need to do
        // and which the compositor is already contending with.
        string state = $"{Canvas.Backdrop?.GetHashCode()}|{Canvas.Values.Version}|"
                       + $"{Canvas.BackdropIncludesOverlay}|{Canvas.Selected?.Id}";

        if (state != _lastCanvasState)
        {
            _lastCanvasState = state;
            Canvas.InvalidateVisual();
        }

        StatusText.Text = _profile == null
            ? ""
            : $"{_profile.Layers.Count} layer(s)   ·   "
              + $"renderer {_app.Overlay.Rendered} drawn, {_app.Overlay.Skipped} skipped   ·   "
              + $"{_app.Overlay.Sensors.Descriptors.Count} sensors";
    }

    private void ShowProperties()
    {
        Properties.Show(Canvas.Selected, () => { Touch(); RebuildLayerList(); }, PickToken);
        LayerList.SelectedIndex = IndexOf(Canvas.Selected);
    }

    private void RefreshProperties()
    {
        // Dragging changes X/Y/W/H, and the boxes showing them have to follow.
        if (Canvas.Selected != null) ShowProperties();
    }

    /// <summary>
    /// Pushes the edit to the running renderer so the panel shows it. Cheap:
    /// safe to call for every mouse-move of a drag.
    /// </summary>
    private void Push()
    {
        _app.Overlay.Refresh(_profile);

        // Clearing the cached state forces the next tick to repaint. An edit
        // changes layer geometry rather than any of the values the state string
        // is built from, so without this a drag would not show until a sensor
        // happened to move.
        _lastCanvasState = "";
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// Pushes and persists. There is no apply step on purpose — the panel is
    /// right there, and showing the change is faster than describing it.
    /// </summary>
    private void Touch()
    {
        Push();
        Commit();
    }

    /// <summary>
    /// Records an undoable edit and persists it.
    ///
    /// Distinct from <see cref="Save"/>, which only writes the file. Saving
    /// happens for things that are not edits — switching profile, toggling the
    /// master enable — and those must not land in the history, or Ctrl+Z would
    /// undo something the user never thinks of as a change.
    /// </summary>
    private void Commit(string? coalesceKey = null)
    {
        _history.Commit(_app.Overlays, coalesceKey);
        Save();
    }

    private void Save() => Storage.SaveOverlays(_app.Overlays);

    // -----------------------------------------------------------------------
    // Undo
    // -----------------------------------------------------------------------

    private void OnUndo(object sender, RoutedEventArgs e) => Step(redo: false);

    private void OnRedo(object sender, RoutedEventArgs e) => Step(redo: true);

    private void Step(bool redo)
    {
        // A pending generation is applied to the profile but never committed, so
        // it sits in front of the history rather than in it. Undo takes that
        // back first and stops there — exactly what Discard does — because
        // falling through would undo the generation AND the edit before it, two
        // things for one keypress.
        if (!redo && _pending != null)
        {
            OnDiscardGenerated(this, new RoutedEventArgs());
            return;
        }

        bool moved = redo ? _history.Redo(_app.Overlays) : _history.Undo(_app.Overlays);
        if (!moved) return;

        // The whole profile list may have changed — including which one is
        // active, since an edit in another profile has to be shown to be
        // understood — so rebuild rather than patch.
        LoadProfiles();
        Save();
    }

    private void RefreshUndoButtons()
    {
        if (UndoButton == null || RedoButton == null) return;

        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
    }

    private string? PickToken()
    {
        var dlg = new TokenPickerWindow(_app.Overlay.Sensors) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Token : null;
    }

    // -----------------------------------------------------------------------
    // Toolbar
    // -----------------------------------------------------------------------

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _app.Overlay.SetEnabled(EnabledBox.IsChecked == true);
        Save();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Canvas == null || _profile == null) return;
        if (ThemeBox.SelectedIndex < 0) return;

        _profile.Theme = OverlayTheme.All[ThemeBox.SelectedIndex].Name;

        // Colours, fonts and corner radii all resolve at draw time, so this is
        // the whole of applying a theme — the layers themselves are untouched.
        Touch();
    }

    private void OnZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Canvas == null || ZoomBox.SelectedIndex < 0) return;
        Canvas.Zoom = Zooms[ZoomBox.SelectedIndex];
        Canvas.InvalidateMeasure();
        Canvas.InvalidateVisual();
    }

    // Every handler below has to tolerate being called from inside
    // InitializeComponent: a property set in XAML — SnapBox's IsChecked — raises
    // its event while the generated field assignments are still running, so the
    // controls it refers to do not exist yet.
    private void OnViewOptionChanged(object sender, RoutedEventArgs e)
    {
        if (Canvas == null) return;
        Canvas.ShowGrid = GridBox.IsChecked == true;
        Canvas.SnapEnabled = SnapBox.IsChecked == true;
        Canvas.InvalidateVisual();
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Canvas == null) return;

        switch (BackdropBox.SelectedIndex)
        {
            case 0:
                Canvas.Backdrop = _app.Display.Preview;
                Canvas.BackdropIsPanelBuffer = Canvas.Backdrop != null;
                break;
            case 1:
                Canvas.Backdrop = null;
                Canvas.BackdropIsPanelBuffer = false;
                Canvas.BackdropFill = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14));
                break;
            case 2:
                Canvas.Backdrop = null;
                Canvas.BackdropIsPanelBuffer = false;
                Canvas.BackdropFill = new SolidColorBrush(Color.FromRgb(0x46, 0x4E, 0x56));
                break;
            default:
                Canvas.Backdrop = null;
                Canvas.BackdropIsPanelBuffer = false;
                Canvas.BackdropFill = Checkerboard();
                break;
        }
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// The usual transparency check, so it is obvious which parts of a layer are
    /// see-through and which are merely dark.
    /// </summary>
    private static Brush Checkerboard()
    {
        var group = new DrawingGroup();
        using (DrawingContext dc = group.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E)), null,
                new Rect(0, 0, 16, 16));
            var dark = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x32));
            dc.DrawRectangle(dark, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(dark, null, new Rect(8, 8, 8, 8));
        }

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    // -----------------------------------------------------------------------
    // Keyboard
    // -----------------------------------------------------------------------

    /// <summary>
    /// Layer shortcuts, handled for the whole window so they work whether the
    /// canvas or the layer list has focus.
    ///
    /// The guard matters more than the shortcuts: Delete has to keep deleting
    /// *characters* while a text box or combo has focus. Typing a template and
    /// losing the layer to a stray Delete would be an unforgivable way to lose
    /// work.
    /// </summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        if (Keyboard.FocusedElement is TextBox or ComboBox or System.Windows.Controls.Primitives.TextBoxBase)
            return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        OverlayLayer? layer = Canvas.Selected;

        switch (e.Key)
        {
            case Key.Delete when layer != null:
            case Key.Back when layer != null:
                OnDeleteLayer(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.D when ctrl && layer != null:
                OnDuplicateLayer(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            // Ctrl+Z inside a text box is the box's own undo, and the guard
            // above has already returned for that — which is what you want:
            // undoing a typo should not roll back the whole profile.
            case Key.Z when ctrl && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                Step(redo: false);
                e.Handled = true;
                break;

            case Key.Y when ctrl:
            case Key.Z when ctrl && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                Step(redo: true);
                e.Handled = true;
                break;

            case Key.Escape:
                Canvas.Select(null);
                e.Handled = true;
                break;

            // Bring forward / send backward. Ctrl to avoid stealing the plain
            // arrow keys, which nudge.
            case Key.Up when ctrl:
            case Key.OemCloseBrackets when ctrl:
                OnMoveUp(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.Down when ctrl:
            case Key.OemOpenBrackets when ctrl:
                OnMoveDown(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.H when ctrl && layer != null:
                layer.Enabled = !layer.Enabled;
                Touch();
                RebuildLayerList();
                e.Handled = true;
                break;

            case Key.L when ctrl && layer != null:
                layer.Locked = !layer.Locked;
                Touch();
                RebuildLayerList();
                e.Handled = true;
                break;

            case Key.Tab when _profile is { Layers.Count: > 0 }:
            {
                // Cycle through the stack, so a layer buried under another can
                // be reached without hunting for it in the list.
                int i = layer == null ? -1 : _profile.Layers.IndexOf(layer);
                int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                int next = ((i + step) % _profile.Layers.Count + _profile.Layers.Count)
                           % _profile.Layers.Count;
                Canvas.Select(_profile.Layers[next]);
                Canvas.Focus();
                e.Handled = true;
                break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Generation
    //
    // A result is applied to the profile straight away, so it can be judged
    // against the live panel at the right size with real values — but the
    // profile it replaced is held, and nothing is saved until Accept. Discard
    // puts the snapshot back.
    // -----------------------------------------------------------------------

    private OverlayGenerator? _generator;
    private CancellationTokenSource? _generating;

    /// <summary>The profile as it was before the pending result was applied.</summary>
    private OverlayProfile? _snapshot;

    /// <summary>The pending result, kept so its intent can be flipped in place.</summary>
    private GenerationResult? _pending;

    private string _lastPrompt = "";

    private OverlayGenerator Generator =>
        _generator ??= new OverlayGenerator(_app.Ai, _app.Overlay.Sensors);

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnGenerate(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void OnGenerate(object sender, RoutedEventArgs e) =>
        _ = GenerateAsync(PromptBox.Text, retry: false);

    private void OnRetryGenerate(object sender, RoutedEventArgs e) =>
        _ = GenerateAsync(_lastPrompt, retry: true);

    private async Task GenerateAsync(string prompt, bool retry)
    {
        if (_profile == null) return;
        if (_generating != null) return;   // one at a time; the button is disabled anyway

        if (string.IsNullOrWhiteSpace(prompt))
        {
            ShowBanner("Type what you want on the panel, then press Generate.", null, false);
            return;
        }

        // Trying again replaces the pending result rather than stacking on it,
        // so the snapshot has to go back first.
        if (retry) Restore();

        _lastPrompt = prompt;
        _generating = new CancellationTokenSource();

        GenerateButton.IsEnabled = false;
        GenerateButton.Content = "Thinking…";
        ShowBanner("Asking the model…", null, false);

        try
        {
            (double w, double h) = _app.Overlay.DesignSize;

            GenerationResult result = await Generator
                .GenerateAsync(prompt, _profile, w, h, _generating.Token)
                .ConfigureAwait(true);

            if (!result.Success)
            {
                ShowBanner(result.Error ?? "That did not work.", null, false);
                return;
            }

            Apply(result);
        }
        catch (OperationCanceledException)
        {
            HideBanner();
        }
        catch (Exception ex)
        {
            // The generator swallows endpoint failures itself, so anything here
            // is a genuine bug rather than a bad reply. Say so plainly.
            Storage.Log("overlay ai: " + ex);
            ShowBanner("Something went wrong: " + ex.Message, null, false);
        }
        finally
        {
            _generating?.Dispose();
            _generating = null;
            GenerateButton.IsEnabled = true;
            GenerateButton.Content = "Generate";
        }
    }

    /// <summary>Puts a result on the canvas and the panel, pending acceptance.</summary>
    private void Apply(GenerationResult result)
    {
        if (_profile == null) return;

        // Snapshot once per pending result — a flip or a retry must go back to
        // the profile as it was before the *first* generation, not to the one
        // the previous attempt left behind.
        _snapshot ??= Clone(_profile);

        if (result.Intent == OverlayIntent.Replace) _profile.Layers.Clear();
        _profile.Layers.AddRange(result.Layers);

        if (result.Theme != null)
        {
            _profile.Theme = result.Theme;

            // Under _loading, or selecting it would read as the user having
            // chosen it and re-save on top of the pending change.
            _loading = true;
            int i = OverlayTheme.All.ToList().FindIndex(t => t.Name == result.Theme);
            if (i >= 0) ThemeBox.SelectedIndex = i;
            _loading = false;
        }

        _pending = result;

        Canvas.Select(result.Layers.Count > 0 ? result.Layers[^1] : null);
        RebuildLayerList();
        Push();          // to the panel, but deliberately NOT saved yet
        ShowProperties();

        string what = result.Intent == OverlayIntent.Replace
            ? $"Replaced the overlay with {result.Layers.Count} layer(s)"
            : $"Added {result.Layers.Count} layer(s)";

        // Naming the theme matters: it restyles everything, so it should never
        // be the part of a change nobody mentioned.
        if (result.Theme != null) what += $", {result.Theme} theme";

        ShowBanner(
            string.IsNullOrWhiteSpace(result.Note) ? what : $"{what} — {result.Note}",
            result.Notes.Count > 0 ? string.Join("  ·  ", result.Notes) : null,
            true);
    }

    /// <summary>Accepting is the commit; the generation was only ever applied.</summary>
    private void OnAcceptGenerated(object sender, RoutedEventArgs e)
    {
        _snapshot = null;
        _pending = null;
        HideBanner();
        Save();
        PromptBox.Clear();
    }

    private void OnDiscardGenerated(object sender, RoutedEventArgs e)
    {
        Restore();
        HideBanner();
    }

    /// <summary>
    /// Re-applies the same answer the other way round. Goes back through the
    /// generator rather than shuffling layers, because add and replace lay out
    /// against different obstacles.
    /// </summary>
    private void OnFlipIntent(object sender, RoutedEventArgs e)
    {
        if (_pending?.Plan == null || _profile == null) return;

        OverlayIntent flipped = _pending.Intent == OverlayIntent.Replace
            ? OverlayIntent.Add
            : OverlayIntent.Replace;

        Restore();

        (double w, double h) = _app.Overlay.DesignSize;

        GenerationResult again = Generator.Assemble(
            _pending.Plan, _profile, _app.Overlay.Sensors.Snapshot(), w, h, flipped);

        if (again.Success) Apply(again);
        else ShowBanner(again.Error ?? "That did not work.", null, false);
    }

    /// <summary>Puts the profile back as it was before the pending result.</summary>
    private void Restore()
    {
        if (_snapshot == null || _profile == null) return;

        _profile.Layers.Clear();
        _profile.Layers.AddRange(_snapshot.Layers);

        // The theme as well as the layers: a generation may have changed it, and
        // discarding half of a change is worse than discarding none of it.
        _profile.Theme = _snapshot.Theme;

        _loading = true;
        int themeIndex = OverlayTheme.All.ToList()
            .FindIndex(t => string.Equals(t.Name, OverlayTheme.ByName(_profile.Theme).Name,
                StringComparison.OrdinalIgnoreCase));
        if (themeIndex >= 0) ThemeBox.SelectedIndex = themeIndex;
        _loading = false;

        _snapshot = null;
        _pending = null;

        Canvas.Select(_profile.Layers.LastOrDefault());
        RebuildLayerList();
        Push();
        ShowProperties();
    }

    private void ShowBanner(string headline, string? detail, bool actionable)
    {
        AiHeadline.Text = headline;

        AiDetail.Text = detail ?? "";
        AiDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        AiActions.Visibility = actionable ? Visibility.Visible : Visibility.Collapsed;
        AiBanner.Visibility = Visibility.Visible;

        if (actionable && _pending != null)
        {
            AiIntentButton.Content = _pending.Intent == OverlayIntent.Replace
                ? "Add instead"
                : "Replace instead";
        }
    }

    private void HideBanner() => AiBanner.Visibility = Visibility.Collapsed;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // -----------------------------------------------------------------------

    private string? Prompt(string title, string label, string initial)
    {
        var dlg = new PromptWindow(title, label, initial) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
