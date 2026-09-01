using System;
using System.Collections.Generic;
using System.Text.Json;

using JLDisplayManager.Models.Overlay;

namespace JLDisplayManager.Views.Overlay;

/// <summary>
/// Undo and redo for the overlay editor.
///
/// Snapshots rather than commands. A command-based history would need one class
/// per mutation — every layer property, every reorder, the theme, the AI's
/// wholesale replacement — and the failure mode is silent: add a property,
/// forget its command, and undo quietly skips it. A snapshot cannot miss a field
/// it does not know about, which matters here because the mutation surface is
/// most of a hundred properties and still growing.
///
/// What it costs is memory, and that turns out not to matter: a profile is a
/// dozen layers, so a snapshot is a few KB and a full history is under a
/// megabyte. The layer model is polymorphic and already round-trips through
/// <c>System.Text.Json</c> — the same trick <c>Duplicate profile</c> uses.
///
/// Scope is the profile <em>list</em>, not one profile. Deleting a profile is
/// exactly the action people most want back, and the active id travels with the
/// snapshot, so undoing an edit made in another profile switches to it rather
/// than silently changing something out of sight.
///
/// Deliberately outside: the master enable switch, the render rate and the poll
/// interval. Those are app settings that happen to live in the same file, and a
/// Ctrl+Z that turns the whole overlay off would be a nasty surprise.
/// </summary>
public sealed class EditHistory
{
    /// <summary>
    /// Deep enough that nobody reaches the end in a session, shallow enough that
    /// the memory stays uninteresting. At a few KB a snapshot this is well under
    /// a megabyte.
    /// </summary>
    private const int Depth = 100;

    /// <summary>
    /// How close together two edits of the same thing collapse into one.
    ///
    /// Only nudging needs this, and it needs it badly: an arrow key auto-repeats
    /// about thirty times a second and each repeat is a committed edit, so
    /// without coalescing a one-second press costs thirty presses of Ctrl+Z to
    /// undo. Everything else already commits once per gesture — a drag on
    /// mouse-up, a text field on Enter or focus loss.
    /// </summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(700);

    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();

    /// <summary>The state as of the last commit — what an undo would go back to.</summary>
    private Entry _baseline;

    private string? _lastKey;
    private DateTime _lastAt = DateTime.MinValue;

    public EditHistory(OverlaySettings settings)
    {
        _baseline = Entry.Capture(settings);
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised when the stacks change, so the buttons can enable and disable.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Records that an edit has completed.
    ///
    /// <paramref name="coalesceKey"/> names the thing being edited — passing the
    /// same key twice in quick succession replaces the top entry instead of
    /// stacking a second one. Null never coalesces.
    /// </summary>
    public void Commit(OverlaySettings settings, string? coalesceKey = null)
    {
        DateTime now = DateTime.UtcNow;

        bool merge = coalesceKey != null
                     && coalesceKey == _lastKey
                     && now - _lastAt < CoalesceWindow
                     && _undo.Count > 0;

        // A merged edit keeps the older entry: undoing a run of nudges should
        // land where the run started, not one step into it.
        if (!merge)
        {
            _undo.Add(_baseline);
            if (_undo.Count > Depth) _undo.RemoveAt(0);
        }

        // Any new edit invalidates the redo branch. Editing after undoing is a
        // decision to go a different way.
        _redo.Clear();

        _baseline = Entry.Capture(settings);
        _lastKey = coalesceKey;
        _lastAt = now;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Steps back. Returns false when there is nothing to undo, so the caller
    /// can leave the UI alone rather than repainting for nothing.
    /// </summary>
    public bool Undo(OverlaySettings settings) => Step(settings, _undo, _redo);

    public bool Redo(OverlaySettings settings) => Step(settings, _redo, _undo);

    private bool Step(OverlaySettings settings, List<Entry> from, List<Entry> to)
    {
        if (from.Count == 0) return false;

        Entry target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(_baseline);

        target.RestoreInto(settings);
        _baseline = target;

        // A step never coalesces with whatever comes next: undo, then nudge,
        // and the nudge has to be its own entry or it would eat the undo.
        _lastKey = null;

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Forgets everything and rebases on the current state. For when the profile
    /// list changed underneath the editor and the old snapshots no longer
    /// describe anything real.
    /// </summary>
    public void Reset(OverlaySettings settings)
    {
        _undo.Clear();
        _redo.Clear();
        _baseline = Entry.Capture(settings);
        _lastKey = null;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------------

    /// <summary>One point in the history: every profile, and which was active.</summary>
    private readonly struct Entry
    {
        private readonly string _json;
        private readonly Guid? _active;

        private Entry(string json, Guid? active)
        {
            _json = json;
            _active = active;
        }

        public static Entry Capture(OverlaySettings s) =>
            new(JsonSerializer.Serialize(s.Profiles), s.ActiveProfileId);

        public void RestoreInto(OverlaySettings s)
        {
            var profiles = JsonSerializer.Deserialize<List<OverlayProfile>>(_json);
            if (profiles == null) return;

            // Mutated in place, never reassigned: App and OverlayService both
            // hold this same OverlaySettings, so swapping the list would leave
            // them pointing at the old one.
            s.Profiles.Clear();
            s.Profiles.AddRange(profiles);
            s.ActiveProfileId = _active;
        }
    }
}
