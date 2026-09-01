using System;

namespace JLDisplayManager.Views.Overlay;

/// <summary>
/// Says what finished, so the undo history can tell a run of small repeats from
/// a sequence of separate decisions.
/// </summary>
public sealed class EditCommittedEventArgs : EventArgs
{
    public EditCommittedEventArgs(string? coalesceKey)
    {
        CoalesceKey = coalesceKey;
    }

    /// <summary>
    /// Names the thing being edited — <c>nudge:{layer id}</c> — so two of the
    /// same in quick succession collapse into one undo step. Null for anything
    /// that is already one gesture per edit, which is nearly everything.
    /// </summary>
    public string? CoalesceKey { get; }
}
