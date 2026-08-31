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

    /// <summary>
    /// What the AI pipeline made. Kept in its own view rather than its own
    /// stored list: the playlist holds ids into Library.Items and retention
    /// walks the same list, so one collection on disk and two views of it is
    /// far less to keep straight than two of each.
    /// </summary>
    public ObservableCollection<MediaItem> GeneratedItems { get; } = new();

    public ObservableCollection<MediaItem> PlaylistItems { get; } = new();

    private bool _suppressPlaylistSync;

    /// <summary>Which of the two the grid is currently showing.</summary>
    private bool _showingGenerated;

    public MainWindow()
    {
        InitializeComponent();

        LibraryList.ItemsSource = LibraryItems;
        PlaylistList.ItemsSource = PlaylistItems;

        foreach (var item in _app.Library.Items)
            (item.IsGenerated ? GeneratedItems : LibraryItems).Add(item);
        RebuildPlaylistFromModel();

        ShuffleBox.IsChecked = _app.Library.Shuffle;
        BrightnessSlider.Value = _app.Settings.Brightness;

        _app.Display.PropertyChanged += OnDisplayChanged;
        _app.Player.PropertyChanged += OnPlayerChanged;

        // The pipeline writes straight into the library model; these keep the
        // grid in step with it without the grid having to poll.
        _app.Pipeline.ItemGenerated += OnItemGenerated;
        _app.Pipeline.ItemPruned += OnItemPruned;
        _app.Pipeline.PropertyChanged += OnPipelineChanged;

        UpdateStatus();
        UpdateAiStatus();
        UpdateEmptyHints();
        UpdateSourceTabs();
        UpdatePlaylistHeading();

        _ = LoadThumbnailsAsync(LibraryItems.Concat(GeneratedItems).ToList());
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
                UpdatePanelPrompt();
                break;
        }
    }

    private void OnPipelineChanged(object? sender, PropertyChangedEventArgs e) =>
        UpdateAiStatus();

    /// <summary>
    /// Mirrors the pipeline into the bar under the main status line. Generation
    /// takes minutes and is otherwise invisible — without this, starting the
    /// slideshow looks like nothing happening.
    /// </summary>
    private void UpdateAiStatus()
    {
        var pipeline = _app.Pipeline;

        AiBar.Visibility = pipeline.HasActivity ? Visibility.Visible : Visibility.Collapsed;
        if (!pipeline.HasActivity) return;

        AiStatusText.Text = pipeline.Summary;

        AiStatusText.Foreground = pipeline.HasError
            ? (Brush)FindResource("Danger")
            : (Brush)FindResource("Text");

        AiDot.Fill = pipeline.HasError ? (Brush)FindResource("Danger")
            : pipeline.Running ? (Brush)FindResource("Accent")
            : (Brush)FindResource("AccentDim");

        // The error, while there is one, is more use than the prompt.
        string? detail = pipeline.HasError ? pipeline.LastError : pipeline.CurrentPrompt;
        AiPromptText.Text = detail ?? "";
        AiPromptText.Visibility = string.IsNullOrEmpty(detail)
            ? Visibility.Collapsed
            : Visibility.Visible;

        AiToggleButton.Content = pipeline.Running ? "Stop AI" : "Start AI";

        UpdatePanelPrompt();
    }

    /// <summary>Shows the prompt beside the preview when the panel holds a generated image.</summary>
    private void UpdatePanelPrompt()
    {
        // Whatever is actually on the glass, which is not always the pipeline's
        // idea of current — Show now puts something else up.
        var shown = _app.Display.Current;

        string? prompt = shown is { IsGenerated: true } item ? item.EnhancedPrompt : null;

        PanelPromptText.Text = prompt ?? "";
        PanelPromptText.Visibility = string.IsNullOrEmpty(prompt)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnToggleAi(object sender, RoutedEventArgs e)
    {
        if (_app.Pipeline.Running) _app.Pipeline.Stop();
        else _app.StartAi();

        UpdateAiStatus();
        _app.SaveAll();
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

        // Dropping files while the generated tab is up would otherwise put them
        // somewhere the user cannot see.
        if (_showingGenerated) ShowSource(generated: false);

        _app.SaveAll();
        UpdateEmptyHints();
        UpdateSourceTabs();
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

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        var dialog = new YoutubeDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.DownloadedPath is { } path)
            AddPaths(new[] { path });
    }

    // -----------------------------------------------------------------------
    // Sources
    // -----------------------------------------------------------------------

    private void OnShowLibrary(object sender, RoutedEventArgs e) => ShowSource(generated: false);

    private void OnShowGenerated(object sender, RoutedEventArgs e) => ShowSource(generated: true);

    /// <summary>
    /// Points the grid at one of the two collections and dresses the panel to
    /// match. One ListBox rather than two stacked or a TabControl: the tiles,
    /// the selection behaviour and every button below are identical, and the
    /// system-drawn TabControl chrome would fight this window's palette the way
    /// the ComboBox popup already does.
    /// </summary>
    private void ShowSource(bool generated)
    {
        _showingGenerated = generated;

        LibraryList.ItemsSource = generated ? GeneratedItems : LibraryItems;
        LibraryList.SelectedItem = null;

        LibraryTab.Style = (Style)FindResource(generated ? "Btn" : "BtnPrimary");
        GeneratedTab.Style = (Style)FindResource(generated ? "BtnPrimary" : "Btn");

        AddFilesButton.Visibility = generated ? Visibility.Collapsed : Visibility.Visible;
        OpenGeneratedButton.Visibility = generated ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = generated ? Visibility.Visible : Visibility.Collapsed;

        UpdateEmptyHints();
        UpdateSourceTabs();
    }

    /// <summary>Keeps the counts on the two tab buttons honest.</summary>
    private void UpdateSourceTabs()
    {
        LibraryTab.Content = LibraryItems.Count > 0 ? $"LIBRARY ({LibraryItems.Count})" : "LIBRARY";
        GeneratedTab.Content = GeneratedItems.Count > 0
            ? $"GENERATED ({GeneratedItems.Count})"
            : "GENERATED";
    }

    private void OnOpenGeneratedFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Storage.EnsureDirectories();
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Storage.GeneratedDirectory)
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            Storage.Log("could not open the generated folder: " + ex.Message);
        }
    }

    private void OnLibraryDoubleClick(object sender, MouseButtonEventArgs e) => ShowSelected();

    private void OnShowNow(object sender, RoutedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        if (LibraryList.SelectedItem is not MediaItem item) return;

        // Showing one thing on demand means neither the playlist nor the AI
        // slideshow is what drives the panel any more; leaving either running
        // would yank the item away at the next dwell.
        _app.ShowOne(item);
        _app.SaveAll();
    }

    private void OnRemoveFromLibrary(object sender, RoutedEventArgs e)
    {
        var selected = LibraryList.SelectedItems.Cast<MediaItem>().ToList();
        if (selected.Count == 0) return;

        // Removing a generated image is the user throwing it away, so the file
        // goes too — leaving orphans in the generated folder that nothing lists
        // would just grow silently.
        bool deleteFiles = _showingGenerated;

        foreach (var item in selected)
        {
            LibraryItems.Remove(item);
            GeneratedItems.Remove(item);
            _app.Library.Items.Remove(item);

            // An item removed from the library cannot stay in the playlist —
            // the playlist holds ids that would no longer resolve.
            _app.Library.Playlist.RemoveAll(id => id == item.Id);

            if (!deleteFiles || !item.IsGenerated) continue;

            try
            {
                if (System.IO.File.Exists(item.Path)) System.IO.File.Delete(item.Path);
            }
            catch (Exception ex)
            {
                // Locked, most likely. The entry is gone either way, which is
                // what was asked for.
                Storage.Log($"could not delete {item.Path}: {ex.Message}");
            }
        }

        RebuildPlaylistFromModel();
        _app.Player.Refresh();
        _app.SaveAll();
        UpdateEmptyHints();
        UpdateSourceTabs();
    }

    /// <summary>Newly generated: show it at the top, where it will be noticed.</summary>
    private void OnItemGenerated(object? sender, MediaItem item)
    {
        // Newest first: the one just made is the one worth looking at.
        GeneratedItems.Insert(0, item);
        UpdateEmptyHints();
        UpdateSourceTabs();
    }

    private void OnItemPruned(object? sender, MediaItem item)
    {
        GeneratedItems.Remove(item);
        LibraryItems.Remove(item);
        PlaylistItems.Remove(item);

        // Generate now works while the playlist is running, so a prune can land
        // on an item the player has already resolved into its order. Without
        // this it would reach a file that is no longer there.
        _app.Player.Refresh();

        UpdateEmptyHints();
        UpdateSourceTabs();
    }

    /// <summary>
    /// Marks generated images as keepers, so retention pruning skips them.
    /// Only meaningful for generated items; a file the user added themselves is
    /// never deleted by this app in the first place.
    /// </summary>
    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        var selected = LibraryList.SelectedItems.Cast<MediaItem>()
            .Where(i => i.IsGenerated)
            .ToList();

        if (selected.Count == 0) return;

        // One click should make the selection agree rather than invert item by
        // item: if any is unpinned, pin them all.
        bool pin = selected.Any(i => !i.Pinned);
        foreach (var item in selected) item.Pinned = pin;

        _app.SaveAll();
    }

    private void OnAi(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new AiWindow { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Storage.Log("ai window failed: " + ex);
            MessageBox.Show(this, ex.Message, "AI",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            _app.StartPlaylist();
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
        int count = _showingGenerated ? GeneratedItems.Count : LibraryItems.Count;

        EmptyHint.Text = _showingGenerated
            ? "Nothing generated yet.\nSet up SwarmUI under AI, then start the slideshow."
            : "Drop images and videos here,\nor use Add files.";

        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistEmptyHint.Visibility =
            PlaylistItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // -----------------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------------

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _app.StopAll();
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
