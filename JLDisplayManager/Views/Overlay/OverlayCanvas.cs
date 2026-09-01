using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Overlay;
using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Views.Overlay;

/// <summary>
/// The editor's design surface.
///
/// It paints the profile by calling <see cref="OverlayRenderer.DrawProfile"/> —
/// the very same code that produces the bitmap sent to the panel. That is the
/// whole point: WYSIWYG here is not something kept true by careful maintenance,
/// it is true because there is only one renderer. Everything this class adds on
/// top — selection handles, snap guides, the grid — is drawn *after* that call
/// and never reaches the panel.
/// </summary>
public sealed class OverlayCanvas : FrameworkElement
{
    private const double SnapDistance = 6;    // panel pixels
    private const double HandleSize = 7;      // screen pixels, so it stays grabbable when zoomed out

    private enum Grip
    {
        None, Move,
        TopLeft, Top, TopRight,
        Left, Right,
        BottomLeft, Bottom, BottomRight,
    }

    private Grip _grip = Grip.None;
    private Point _dragStart;
    private Rect _dragOrigin;
    private bool _dragging;

    // Guides shown only while dragging, in panel coordinates.
    private readonly List<double> _guidesX = new();
    private readonly List<double> _guidesY = new();

    public OverlayCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    public OverlayProfile? Profile { get; set; }

    public SensorSnapshot Values { get; set; } = SensorSnapshot.Empty;

    public LayerContext Context { get; set; } = new();

    /// <summary>What sits behind the layers: the live panel, or a still, or nothing.</summary>
    public BitmapSource? Backdrop { get; set; }

    /// <summary>
    /// True when <see cref="Backdrop"/> is a copy of the panel's own buffer and
    /// therefore carries the mounting rotation, so the canvas has to undo it.
    /// A still from disk or a flat colour does not.
    /// </summary>
    public bool BackdropIsPanelBuffer { get; set; }

    /// <summary>The mounting rotation, from Settings. Drives the backdrop's turn.</summary>
    public int Rotate { get; set; }

    public Brush BackdropFill { get; set; } = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));

    public double Zoom { get; set; } = 1.0;

    public bool ShowGrid { get; set; }

    public int GridSize { get; set; } = 8;

    public bool SnapEnabled { get; set; } = true;

    public OverlayLayer? Selected { get; private set; }

    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Raised continuously while a drag changes a layer's position or size, so
    /// the panel and the property boxes keep up. Cheap work only.
    /// </summary>
    public event EventHandler? LayerChanged;

    /// <summary>
    /// Raised once an edit is finished — the mouse released, or a nudge key
    /// pressed. Anything expensive, saving above all, belongs here: a drag
    /// raises <see cref="LayerChanged"/> for every mouse-move event, and
    /// rewriting overlays.json that often would be absurd.
    /// </summary>
    public event EventHandler<EditCommittedEventArgs>? EditCommitted;

    private void Committed(string? coalesceKey) =>
        EditCommitted?.Invoke(this, new EditCommittedEventArgs(coalesceKey));

    /// <summary>
    /// True when <see cref="Backdrop"/> is the panel's own output, which is
    /// taken after compositing and therefore already contains the overlay.
    /// Drawing the layers again on top of that would double every translucent
    /// fill, so the canvas leaves them to the backdrop and draws only its own
    /// chrome.
    /// </summary>
    public bool BackdropIncludesOverlay { get; set; }

    public void Select(OverlayLayer? layer)
    {
        if (ReferenceEquals(Selected, layer)) return;
        Selected = layer;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // -----------------------------------------------------------------------
    // Layout
    // -----------------------------------------------------------------------

    /// <summary>
    /// The design surface, which is 480x960 rather than 960x480 when the panel
    /// is mounted at 90 or 270 degrees. Taken from the context so the canvas,
    /// the renderer and the anchoring maths can never disagree about it.
    /// </summary>
    private double DesignWidth => Context.Width;

    private double DesignHeight => Context.Height;

    protected override Size MeasureOverride(Size availableSize) =>
        new(DesignWidth * Zoom, DesignHeight * Zoom);

    // -----------------------------------------------------------------------
    // Painting
    // -----------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = DesignWidth, h = DesignHeight;
        var panel = new Rect(0, 0, w, h);

        dc.PushTransform(new ScaleTransform(Zoom, Zoom));

        // Clip to the panel so a layer dragged off the edge is cut exactly where
        // the real panel would cut it, rather than floating in the editor.
        dc.PushClip(new RectangleGeometry(panel));

        if (Backdrop != null) DrawBackdrop(dc, panel);
        else dc.DrawRectangle(BackdropFill, null, panel);

        if (ShowGrid && GridSize > 1) DrawGrid(dc, w, h);

        if (!BackdropIncludesOverlay)
            OverlayRenderer.DrawProfile(dc, Profile, Values, Context);

        dc.Pop();   // clip

        // Chrome goes outside the clip so a handle on the very edge is still
        // grabbable, and is drawn unscaled so it stays the same size at any zoom.
        dc.Pop();   // scale

        DrawGuides(dc);
        DrawSelection(dc);
    }

    /// <summary>
    /// Draws the backdrop the right way up.
    ///
    /// The live preview is a copy of the panel's buffer, and on a rotated
    /// mounting that buffer is deliberately pre-turned so the pump head's
    /// physical rotation cancels it. Blitting it straight onto the design
    /// surface would therefore show it upside down or on its side — the one
    /// place in the app where the picture is wrong, and the worst possible place
    /// for it, since this is where a profile gets laid out.
    ///
    /// A still or a solid colour is not a panel buffer and needs no such turn,
    /// which is what <see cref="BackdropIsPanelBuffer"/> distinguishes.
    /// </summary>
    private void DrawBackdrop(DrawingContext dc, Rect panel)
    {
        Transform? turn = BackdropIsPanelBuffer
            ? OverlayRenderer.UnrotateBackdrop(Rotate)
            : null;

        if (turn == null)
        {
            dc.DrawImage(Backdrop, panel);
            return;
        }

        // Drawn at the buffer's own size and then turned, rather than stretched
        // into the design rect: at 90 and 270 those differ in aspect, and
        // stretching first would squash the picture before rotating it.
        dc.PushTransform(turn);
        dc.DrawImage(Backdrop,
            new Rect(0, 0, OverlayRenderer.PanelWidth, OverlayRenderer.PanelHeight));
        dc.Pop();
    }

    private void DrawGrid(DrawingContext dc, double w, double h)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1 / Zoom);
        pen.Freeze();

        for (double x = GridSize; x < w; x += GridSize)
            dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
        for (double y = GridSize; y < h; y += GridSize)
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
    }

    private void DrawGuides(DrawingContext dc)
    {
        if (_guidesX.Count == 0 && _guidesY.Count == 0) return;

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 0xF0, 0xA0, 0x30)), 1);
        pen.Freeze();

        foreach (double x in _guidesX)
            dc.DrawLine(pen, new Point(x * Zoom, 0),
                             new Point(x * Zoom, DesignHeight * Zoom));
        foreach (double y in _guidesY)
            dc.DrawLine(pen, new Point(0, y * Zoom),
                             new Point(DesignWidth * Zoom, y * Zoom));
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (Selected == null) return;

        Rect r = ScreenRect(Selected);

        var outline = new Pen(new SolidColorBrush(Color.FromArgb(255, 0xF0, 0xA0, 0x30)), 1.5);
        outline.Freeze();
        dc.DrawRectangle(null, outline, r);

        if (Selected.Locked) return;   // no handles on something that will not move

        Brush fill = Brushes.White;
        var edge = new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x30)), 1);
        edge.Freeze();

        foreach (Point p in HandlePoints(r))
            dc.DrawRectangle(fill, edge,
                new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    private static IEnumerable<Point> HandlePoints(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return new Point(r.Left, r.Top);
        yield return new Point(cx, r.Top);
        yield return new Point(r.Right, r.Top);
        yield return new Point(r.Left, cy);
        yield return new Point(r.Right, cy);
        yield return new Point(r.Left, r.Bottom);
        yield return new Point(cx, r.Bottom);
        yield return new Point(r.Right, r.Bottom);
    }

    // -----------------------------------------------------------------------
    // Geometry
    // -----------------------------------------------------------------------

    private Rect PanelRect(OverlayLayer l)
    {
        (double x, double y) = l.TopLeft(DesignWidth, DesignHeight);
        return new Rect(x, y, Math.Max(1, l.Width), Math.Max(1, l.Height));
    }

    private Rect ScreenRect(OverlayLayer l)
    {
        Rect r = PanelRect(l);
        return new Rect(r.X * Zoom, r.Y * Zoom, r.Width * Zoom, r.Height * Zoom);
    }

    /// <summary>
    /// Writes a panel-space rectangle back into the layer, converting to
    /// whatever its anchor measures from. Without this, dragging a
    /// bottom-right-anchored layer would move it the wrong way.
    /// </summary>
    private void ApplyRect(OverlayLayer l, Rect r)
    {
        double w = DesignWidth, h = DesignHeight;

        l.Width = Math.Max(4, r.Width);
        l.Height = Math.Max(4, r.Height);

        l.X = l.Anchor switch
        {
            LayerAnchor.TopLeft or LayerAnchor.MiddleLeft or LayerAnchor.BottomLeft => r.X,
            LayerAnchor.TopCentre or LayerAnchor.Centre or LayerAnchor.BottomCentre
                => r.X - (w - l.Width) / 2,
            _ => w - r.X - l.Width,
        };

        l.Y = l.Anchor switch
        {
            LayerAnchor.TopLeft or LayerAnchor.TopCentre or LayerAnchor.TopRight => r.Y,
            LayerAnchor.MiddleLeft or LayerAnchor.Centre or LayerAnchor.MiddleRight
                => r.Y - (h - l.Height) / 2,
            _ => h - r.Y - l.Height,
        };
    }

    /// <summary>
    /// Changes a layer's anchor without moving it. The stored offsets mean
    /// different things per anchor, so they have to be recomputed or the layer
    /// jumps across the panel the moment the anchor is changed.
    /// </summary>
    public void Reanchor(OverlayLayer l, LayerAnchor anchor)
    {
        Rect keep = PanelRect(l);
        l.Anchor = anchor;
        ApplyRect(l, keep);
        InvalidateVisual();
    }

    // -----------------------------------------------------------------------
    // Mouse
    // -----------------------------------------------------------------------

    private Point ToPanel(Point p) => new(p.X / Zoom, p.Y / Zoom);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        Point screen = e.GetPosition(this);

        // A handle on the current selection wins over anything underneath it —
        // otherwise a handle sitting over another layer can never be grabbed.
        if (Selected is { Locked: false })
        {
            Grip g = HitHandles(ScreenRect(Selected), screen);
            if (g != Grip.None)
            {
                BeginDrag(g, screen);
                e.Handled = true;
                return;
            }
        }

        OverlayLayer? hit = HitLayer(ToPanel(screen));
        Select(hit);

        if (hit is { Locked: false })
        {
            BeginDrag(Grip.Move, screen);
            e.Handled = true;
        }
    }

    private void BeginDrag(Grip g, Point screen)
    {
        _grip = g;
        _dragStart = screen;
        _dragOrigin = PanelRect(Selected!);
        _dragging = true;
        CaptureMouse();
    }

    private Grip HitHandles(Rect r, Point p)
    {
        double t = HandleSize;
        Grip[] order =
        {
            Grip.TopLeft, Grip.Top, Grip.TopRight,
            Grip.Left, Grip.Right,
            Grip.BottomLeft, Grip.Bottom, Grip.BottomRight,
        };

        int i = 0;
        foreach (Point h in HandlePoints(r))
        {
            if (Math.Abs(p.X - h.X) <= t && Math.Abs(p.Y - h.Y) <= t) return order[i];
            i++;
        }
        return Grip.None;
    }

    /// <summary>Topmost layer under the point; list order is z-order, so search backwards.</summary>
    private OverlayLayer? HitLayer(Point panel)
    {
        if (Profile == null) return null;

        for (int i = Profile.Layers.Count - 1; i >= 0; i--)
        {
            OverlayLayer l = Profile.Layers[i];
            if (!l.Enabled) continue;
            if (PanelRect(l).Contains(panel)) return l;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        Point screen = e.GetPosition(this);

        if (!_dragging)
        {
            Cursor = Selected is { Locked: false }
                ? CursorFor(HitHandles(ScreenRect(Selected), screen))
                : Cursors.Arrow;
            return;
        }

        if (Selected == null) return;

        double dx = (screen.X - _dragStart.X) / Zoom;
        double dy = (screen.Y - _dragStart.Y) / Zoom;

        Rect r = _dragOrigin;

        if (_grip == Grip.Move)
        {
            r = new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);
            r = Snap(r, moving: true);
        }
        else
        {
            double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

            if (_grip is Grip.TopLeft or Grip.Left or Grip.BottomLeft) left += dx;
            if (_grip is Grip.TopRight or Grip.Right or Grip.BottomRight) right += dx;
            if (_grip is Grip.TopLeft or Grip.Top or Grip.TopRight) top += dy;
            if (_grip is Grip.BottomLeft or Grip.Bottom or Grip.BottomRight) bottom += dy;

            // Dragging a handle past its opposite edge would invert the box;
            // clamp instead, which is what every other editor does.
            if (right - left < 4) { if (left != r.Left) left = right - 4; else right = left + 4; }
            if (bottom - top < 4) { if (top != r.Top) top = bottom - 4; else bottom = top + 4; }

            r = Snap(new Rect(left, top, right - left, bottom - top), moving: false);
        }

        ApplyRect(Selected, r);
        LayerChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private static Cursor CursorFor(Grip g) => g switch
    {
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        _ => Cursors.Arrow,
    };

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        _grip = Grip.None;
        _guidesX.Clear();
        _guidesY.Clear();
        ReleaseMouseCapture();
        InvalidateVisual();

        // No coalescing: a drag is one gesture and already one edit.
        Committed(null);
    }

    // -----------------------------------------------------------------------
    // Snapping
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pulls an edge or centre onto the panel's edges and midlines, onto other
    /// layers' edges and centres, and onto the grid. Guides are recorded for
    /// whatever it snapped to, so it is obvious why the box stopped where it did.
    /// </summary>
    private Rect Snap(Rect r, bool moving)
    {
        _guidesX.Clear();
        _guidesY.Clear();

        if (!SnapEnabled || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return r;

        double w = DesignWidth, h = DesignHeight;

        var xs = new List<double> { 0, w / 2, w };
        var ys = new List<double> { 0, h / 2, h };

        if (Profile != null)
        {
            foreach (OverlayLayer l in Profile.Layers)
            {
                if (ReferenceEquals(l, Selected) || !l.Enabled) continue;
                Rect o = PanelRect(l);
                xs.Add(o.Left); xs.Add(o.Left + o.Width / 2); xs.Add(o.Right);
                ys.Add(o.Top); ys.Add(o.Top + o.Height / 2); ys.Add(o.Bottom);
            }
        }

        if (GridSize > 1)
        {
            for (double g = 0; g <= w; g += GridSize) xs.Add(g);
            for (double g = 0; g <= h; g += GridSize) ys.Add(g);
        }

        // When moving, all three of left/centre/right may snap; when resizing,
        // only the edges actually being dragged should.
        double bestDx = double.MaxValue, guideX = 0;
        foreach (double candidate in xs)
        {
            Consider(candidate, r.Left, ref bestDx, ref guideX);
            Consider(candidate, r.Right, ref bestDx, ref guideX);
            if (moving) Consider(candidate, r.Left + r.Width / 2, ref bestDx, ref guideX);
        }

        double bestDy = double.MaxValue, guideY = 0;
        foreach (double candidate in ys)
        {
            Consider(candidate, r.Top, ref bestDy, ref guideY);
            Consider(candidate, r.Bottom, ref bestDy, ref guideY);
            if (moving) Consider(candidate, r.Top + r.Height / 2, ref bestDy, ref guideY);
        }

        if (Math.Abs(bestDx) <= SnapDistance)
        {
            _guidesX.Add(guideX);
            if (moving) r = new Rect(r.X + bestDx, r.Y, r.Width, r.Height);
            else r = SnapEdgeX(r, bestDx);
        }

        if (Math.Abs(bestDy) <= SnapDistance)
        {
            _guidesY.Add(guideY);
            if (moving) r = new Rect(r.X, r.Y + bestDy, r.Width, r.Height);
            else r = SnapEdgeY(r, bestDy);
        }

        return r;
    }

    private static void Consider(double candidate, double edge, ref double best, ref double guide)
    {
        double d = candidate - edge;
        if (Math.Abs(d) >= Math.Abs(best)) return;
        best = d;
        guide = candidate;
    }

    private Rect SnapEdgeX(Rect r, double dx) =>
        _grip is Grip.TopLeft or Grip.Left or Grip.BottomLeft
            ? new Rect(r.X + dx, r.Y, Math.Max(4, r.Width - dx), r.Height)
            : new Rect(r.X, r.Y, Math.Max(4, r.Width + dx), r.Height);

    private Rect SnapEdgeY(Rect r, double dy) =>
        _grip is Grip.TopLeft or Grip.Top or Grip.TopRight
            ? new Rect(r.X, r.Y + dy, r.Width, Math.Max(4, r.Height - dy))
            : new Rect(r.X, r.Y, r.Width, Math.Max(4, r.Height + dy));

    // -----------------------------------------------------------------------
    // Keyboard
    // -----------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Selected is null or { Locked: true }) return;

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        double dx = 0, dy = 0;

        switch (e.Key)
        {
            case Key.Left: dx = -step; break;
            case Key.Right: dx = step; break;
            case Key.Up: dy = -step; break;
            case Key.Down: dy = step; break;
            default: return;
        }

        Rect r = PanelRect(Selected);
        ApplyRect(Selected, new Rect(r.X + dx, r.Y + dy, r.Width, r.Height));
        LayerChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();

        // A nudge is a whole edit in itself, so it commits immediately — but an
        // arrow key auto-repeats, so a run of nudges on one layer is offered to
        // the history as a single edit rather than thirty.
        Committed($"nudge:{Selected.Id:N}");
        e.Handled = true;
    }
}
