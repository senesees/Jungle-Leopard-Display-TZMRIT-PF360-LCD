using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services;

/// <summary>
/// Walks a playlist. The native side plays exactly one item and says when a
/// video ends; deciding what comes next, and how long a still gets, is this
/// class's whole job.
/// </summary>
public sealed class PlaylistPlayer : INotifyPropertyChanged
{
    private readonly DisplayService _display;
    private readonly AppLibrary _library;
    private readonly DispatcherTimer _dwell;
    private readonly Random _random = new();

    private List<MediaItem> _order = new();
    private int _index = -1;
    private bool _running;

    public PlaylistPlayer(DisplayService display, AppLibrary library)
    {
        _display = display;
        _library = library;

        _dwell = new DispatcherTimer();
        _dwell.Tick += (_, _) => { _dwell.Stop(); Advance(); };

        _display.ItemFinished += (_, _) => { if (_running) Advance(); };

        // A device that comes back should pick up where it left off rather than
        // leaving the panel dark until someone notices.
        _display.Reconnected += (_, _) => { if (_running) PlayCurrent(); };
    }

    public bool Running
    {
        get => _running;
        private set => Set(ref _running, value);
    }

    public MediaItem? CurrentItem =>
        _index >= 0 && _index < _order.Count ? _order[_index] : null;

    public int Position => _index + 1;

    public int Count => _order.Count;

    /// <summary>Starts the playlist, optionally at a particular item.</summary>
    public void Start(Guid? startAt = null)
    {
        _order = BuildOrder();
        if (_order.Count == 0)
        {
            Stop();
            return;
        }

        _index = 0;
        if (startAt is { } id)
        {
            int found = _order.FindIndex(i => i.Id == id);
            if (found >= 0) _index = found;
        }

        Running = true;
        PlayCurrent();
    }

    public void Stop()
    {
        Running = false;
        _dwell.Stop();
        _index = -1;
        Raise(nameof(CurrentItem));
        Raise(nameof(Position));
    }

    public void Next()
    {
        if (!Running) return;
        Advance();
    }

    public void Previous()
    {
        if (!Running || _order.Count == 0) return;
        _dwell.Stop();
        _index = (_index - 1 + _order.Count) % _order.Count;
        PlayCurrent();
    }

    /// <summary>Rebuilds the order after the playlist is edited, keeping our place.</summary>
    public void Refresh()
    {
        if (!Running) return;

        Guid? current = CurrentItem?.Id;
        _order = BuildOrder();

        if (_order.Count == 0)
        {
            _display.Stop();
            Stop();
            return;
        }

        int found = current is { } id ? _order.FindIndex(i => i.Id == id) : -1;
        if (found >= 0)
        {
            // Still in the list: stay on it rather than restarting playback.
            _index = found;
            Raise(nameof(CurrentItem));
            Raise(nameof(Position));
            Raise(nameof(Count));
            return;
        }

        _index = 0;
        PlayCurrent();
    }

    private List<MediaItem> BuildOrder()
    {
        var byId = _library.Items.ToDictionary(i => i.Id);

        var items = _library.Playlist
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Where(i => i.FileExists)
            .ToList();

        if (_library.Shuffle)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        return items;
    }

    private void Advance()
    {
        if (!Running || _order.Count == 0) return;

        _index = (_index + 1) % _order.Count;
        PlayCurrent();
    }

    private void PlayCurrent()
    {
        _dwell.Stop();

        var item = CurrentItem;
        if (item is null) return;

        Raise(nameof(CurrentItem));
        Raise(nameof(Position));
        Raise(nameof(Count));

        // A looping video in a list of several would never hand over, so looping
        // is only honoured when the item is the whole playlist.
        bool soloVideo = _order.Count == 1;

        if (!_display.Play(item, forceNoLoop: !soloVideo))
        {
            // Could not start — a missing file, or the device went away. Skip on
            // rather than stalling, unless there is nothing else to try.
            if (_order.Count > 1) ScheduleAdvance(TimeSpan.FromSeconds(2));
            return;
        }

        // A still never ends on its own, so its dwell is the only thing that
        // moves the list along. A solo still needs no timer at all.
        if (!item.IsVideo && _order.Count > 1)
            ScheduleAdvance(TimeSpan.FromSeconds(item.DwellSeconds));
    }

    private void ScheduleAdvance(TimeSpan after)
    {
        _dwell.Interval = after;
        _dwell.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Raise(name!);
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
