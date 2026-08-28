using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using JLDisplayManager.Interop;
using JLDisplayManager.Models;
using JLDisplayManager.Services;

using MessageBox = System.Windows.MessageBox;

namespace JLDisplayManager.Views;

public partial class MainWindow : Window
{
    private readonly App _app = App.Current;

    /// <summary>
    /// The library and playlist as the UI sees them. The persisted model keeps
    /// the playlist as a list of ids; these are the resolved items, and
    /// <see cref="SyncPlaylistToModel"/> is the one place they are written back.
    /// </summary>
    public ObservableCollection<MediaItem> LibraryItems { get; } = new();

    public ObservableCollection<MediaItem> PlaylistItems { get; } = new();

    private bool _suppressPlaylistSync;

    public MainWindow()
    {
        InitializeComponent();

        LibraryList.ItemsSource = LibraryItems;
        PlaylistList.ItemsSource = PlaylistItems;

        foreach (var item in _app.Library.Items) LibraryItems.Add(item);
        RebuildPlaylistFromModel();

        ShuffleBox.IsChecked = _app.Library.Shuffle;
        BrightnessSlider.Value = _app.Settings.Brightness;

        _app.Display.PropertyChanged += OnDisplayChanged;
        _app.Player.PropertyChanged += OnPlayerChanged;

        UpdateStatus();
        UpdateEmptyHints();
        UpdatePlaylistHeading();

        _ = LoadThumbnailsAsync(LibraryItems.ToList());
    }

    // -----------------------------------------------------------------------
    // Window lifetime
    // -----------------------------------------------------------------------

    /// <summary>
    /// Closing the window hides it. The session keeps running — ending it is
    /// Exit on the tray menu, which is the only thing that stops the panel.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        _app.SaveAll();
        Hide();
    }

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------

    private void OnDisplayChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DisplayService.Preview):
                PreviewImage.Source = _app.Display.Preview;
                PreviewPlaceholder.Visibility =
                    _app.Display.Preview is null ? Visibility.Visible : Visibility.Collapsed;
                break;

            default:
                UpdateStatus();
                break;
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdatePlaylistHeading();
        PlaylistButton.Content = _app.Player.Running ? "Stop playlist" : "Play playlist";
        PlaylistList.SelectedItem = _app.Player.CurrentItem;
    }

    private void UpdateStatus()
    {
        var d = _app.Display;

        StatusText.Text = d.StateText;

        StatusDot.Fill = d.State switch
        {
            NativeMethods.JlState.Error => (Brush)FindResource("Danger"),
            NativeMethods.JlState.Disconnected => (Brush)FindResource("Danger"),
            NativeMethods.JlState.Playing => (Brush)FindResource("Accent"),
            _ => (Brush)FindResource("AccentDim"),
        };

        StatusDetail.Text = d.HasError ? d.Error : d.Message;

        PlaybackStats.Text = d.State == NativeMethods.JlState.Playing && d.FramesSent > 0
            ? $"{d.FramesSent:N0} frames · {d.Fps:N1} fps" +
              (d.FramesDropped > 0 ? $" · {d.FramesDropped:N0} dropped" : "")
            : "";

        ReconnectButton.Visibility = d.Connected ? Visibility.Collapsed : Visibility.Visible;
    }

    // -----------------------------------------------------------------------
    // Library
    // -----------------------------------------------------------------------

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add images and videos",
            Multiselect = true,
            Filter = "Media|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.avif;*.heic;" +
                     "*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.m4v;*.mpg;*.mpeg;*.flv|" +
                     "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.avif;*.heic|" +
                     "Videos|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.gif;*.wmv;*.m4v;*.mpg;*.mpeg;*.flv|" +
                     "All files|*.*",
        };

        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var added = new List<MediaItem>();

        foreach (string path in paths)
        {
            if (!MediaItem.IsSupported(path)) continue;

            // Adding the same file twice would give two library entries that
            // cannot be told apart in the grid.
            if (LibraryItems.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var item = MediaItem.FromPath(path);
            LibraryItems.Add(item);
            _app.Library.Items.Add(item);
            added.Add(item);
        }

        if (added.Count == 0) return;

        _app.SaveAll();
        UpdateEmptyHints();
        _ = LoadThumbnailsAsync(added);
        _ = PrecalibrateAsync(added.Where(i => i.IsVideo).ToList());
    }

    private static async Task LoadThumbnailsAsync(IReadOnlyList<MediaItem> items)
    {
        foreach (var item in items)
            await ThumbnailService.EnsureThumbnailAsync(item);
    }

    /// <summary>
    /// Surveys new videos in the background so that pressing Show is instant
    /// later. This touches no device state, so it is safe while something plays.
    /// </summary>
    private async Task PrecalibrateAsync(IReadOnlyList<MediaItem> videos)
    {
        foreach (var video in videos)
        {
            video.Calibration = CalibrationState.Running;
            var opts = _app.Display.BuildOpts(video);

            int result = await Task.Run(() =>
                NativeMethods.jl_calibrate(video.Path, ref opts));

            video.Calibration = result > 0 ? CalibrationState.Ready : CalibrationState.Failed;
            Storage.Log($"pre-calibrated {video.Name}: {(result > 0 ? $"-q:v {result}" : "failed")}");
        }
    }

    private void OnLibraryDoubleClick(object sender, MouseButtonEventArgs e) => ShowSelected();

    private void OnShowNow(object sender, RoutedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        if (LibraryList.SelectedItem is not MediaItem item) return;

        // Showing one thing on demand means the playlist is no longer what is
        // driving the panel; leaving it running would yank the item away at the
        // next dwell.
        _app.Player.Stop();
        _app.Display.Play(item);
        _app.SaveAll();
    }

    private void OnRemoveFromLibrary(object sender, RoutedEventArgs e)
    {
        var selected = LibraryList.SelectedItems.Cast<MediaItem>().ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            LibraryItems.Remove(item);
            _app.Library.Items.Remove(item);

            // An item removed from the library cannot stay in the playlist —
            // the playlist holds ids that would no longer resolve.
            _app.Library.Playlist.RemoveAll(id => id == item.Id);
        }

        RebuildPlaylistFromModel();
        _app.Player.Refresh();
        _app.SaveAll();
        UpdateEmptyHints();
    }

    // -----------------------------------------------------------------------
    // Playlist
    // -----------------------------------------------------------------------

    private void OnAddToPlaylist(object sender, RoutedEventArgs e)
    {
        var selected = LibraryList.SelectedItems.Cast<MediaItem>().ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            PlaylistItems.Add(item);
            _app.Library.Playlist.Add(item.Id);
        }

        _app.Player.Refresh();
        _app.SaveAll();
        UpdateEmptyHints();
        UpdatePlaylistHeading();
    }

    private void OnRemoveFromPlaylist(object sender, RoutedEventArgs e)
    {
        int index = PlaylistList.SelectedIndex;
        if (index < 0) return;

        PlaylistItems.RemoveAt(index);
        SyncPlaylistToModel();

        PlaylistList.SelectedIndex = Math.Min(index, PlaylistItems.Count - 1);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        int from = PlaylistList.SelectedIndex;
        int to = from + delta;
        if (from < 0 || to < 0 || to >= PlaylistItems.Count) return;

        PlaylistItems.Move(from, to);
        SyncPlaylistToModel();
        PlaylistList.SelectedIndex = to;
    }

    private void OnTogglePlaylist(object sender, RoutedEventArgs e)
    {
        if (_app.Player.Running)
        {
            _app.Player.Stop();
            _app.Display.Stop();
        }
        else
        {
            if (PlaylistItems.Count == 0) return;
            _app.Player.Start();
        }

        _app.SaveAll();
    }

    private void OnNext(object sender, RoutedEventArgs e) => _app.Player.Next();

    private void OnPrevious(object sender, RoutedEventArgs e) => _app.Player.Previous();

    private void OnShuffleChanged(object sender, RoutedEventArgs e)
    {
        _app.Library.Shuffle = ShuffleBox.IsChecked == true;
        _app.Player.Refresh();
        _app.SaveAll();
    }

    private void RebuildPlaylistFromModel()
    {
        _suppressPlaylistSync = true;
        try
        {
            PlaylistItems.Clear();
            var byId = _app.Library.Items.ToDictionary(i => i.Id);
            foreach (var id in _app.Library.Playlist)
                if (byId.TryGetValue(id, out var item))
                    PlaylistItems.Add(item);
        }
        finally
        {
            _suppressPlaylistSync = false;
        }

        UpdatePlaylistHeading();
    }

    private void SyncPlaylistToModel()
    {
        if (_suppressPlaylistSync) return;

        _app.Library.Playlist = PlaylistItems.Select(i => i.Id).ToList();
        _app.Player.Refresh();
        _app.SaveAll();
        UpdateEmptyHints();
        UpdatePlaylistHeading();
    }

    private void UpdatePlaylistHeading()
    {
        PlaylistHeading.Text = _app.Player.Running && _app.Player.Count > 0
            ? $"PLAYLIST — {_app.Player.Position} OF {_app.Player.Count}"
            : $"PLAYLIST — {PlaylistItems.Count} ITEM{(PlaylistItems.Count == 1 ? "" : "S")}";
    }

    private void UpdateEmptyHints()
    {
        EmptyHint.Visibility = LibraryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistEmptyHint.Visibility =
            PlaylistItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // -----------------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------------

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _app.Player.Stop();
        _app.Display.Stop();
        _app.SaveAll();
    }

    private void OnReconnect(object sender, RoutedEventArgs e)
    {
        _app.Settings.AutoReconnect = true;

        if (!_app.Display.Connect())
        {
            MessageBox.Show(this,
                string.IsNullOrEmpty(_app.Display.Error)
                    ? "The display was not found. Check that it is plugged in."
                    : _app.Display.Error,
                "Could not connect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guarded for the same reason as the frame-rate slider: a coerced value
        // during XAML parsing raises this before the label field is assigned.
        if (BrightnessValue is null) return;

        int value = (int)Math.Round(e.NewValue);
        BrightnessValue.Text = $"{value}%";

        // IsLoaded guards the initial value being applied during construction,
        // before there is a session to apply it to.
        if (!IsLoaded) return;

        _app.Settings.Brightness = value;
        _app.Display.ApplyBrightness();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new SettingsWindow { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Storage.Log("settings window failed: " + ex);
            MessageBox.Show(this, ex.Message, "Settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Settings may have changed the brightness; the main slider is the
        // other view of the same value.
        BrightnessSlider.Value = _app.Settings.Brightness;
        _app.SaveAll();
    }
}
