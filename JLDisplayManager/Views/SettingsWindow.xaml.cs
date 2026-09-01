using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using JLDisplayManager.Models;
using JLDisplayManager.Services;

using MessageBox = System.Windows.MessageBox;

namespace JLDisplayManager.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] HwaccelValues =
        { "auto", "none", "cuda", "qsv", "d3d11va", "dxva2" };

    /// <summary>
    /// The dark ink the other combo boxes set inline in XAML. Items built in
    /// code have to say it too, or they inherit the window's light foreground
    /// and vanish against the dropdown's background.
    /// </summary>
    private static readonly Brush ComboText = FrozenInk();

    private static Brush FrozenInk()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x07));
        brush.Freeze();   // shared across every item, so it must not stay mutable
        return brush;
    }

    private readonly App _app = App.Current;
    private bool _loaded;

    public SettingsWindow()
    {
        InitializeComponent();
        Load();
        _loaded = true;
    }

    private void Load()
    {
        var s = _app.Settings;

        RotateBox.SelectedIndex = s.Rotate / 90;
        StretchBox.IsChecked = s.Stretch;
        FpsSlider.Value = s.Fps;
        FpsValue.Text = s.Fps.ToString();

        int hw = Array.IndexOf(HwaccelValues, s.Hwaccel);
        HwaccelBox.SelectedIndex = hw >= 0 ? hw : 0;

        PreprocessBox.SelectedIndex = (int)s.Preprocess;
        ShowPreprocessHint();

        // Read the real registered state rather than the stored flag: the task
        // can be removed from outside the app, and a checkbox that disagrees
        // with Task Scheduler is worse than no checkbox.
        StartWithWindowsBox.IsChecked = StartupTask.IsRegistered();

        StartMinimisedBox.IsChecked = s.StartMinimised;
        ResumeBox.IsChecked = s.ResumeOnStart;

        // The same setting the AI window offers; it lives here as well because
        // this is where someone looks for what happens at startup.
        StartAiBox.IsChecked = _app.Ai.StartWithApp;
        StartAiHint.Text = _app.Ai.Prompts.Count == 0
            ? "Nothing to generate from yet — add prompts under AI first."
            : "Takes precedence over putting back what was showing.";

        AutoReconnectBox.IsChecked = s.AutoReconnect;
        BlankOnExitBox.IsChecked = s.BlankOnExit;
        PortBox.Text = s.Port;

        LhmBox.IsChecked = s.UseLibreHardwareMonitor;
        HwInfoBox.IsChecked = s.UseHwInfo;

        ShowSensorState();
        ShowDeviceInfo();
        ShowFfmpegState();
    }

    private void ShowDeviceInfo()
    {
        string? json = _app.Display.DeviceInfo();
        if (json is null)
        {
            DeviceInfoText.Text = "Not connected.";
            return;
        }

        try
        {
            // The panel answers with {"cmd":"info","data":{…}}; only the inner
            // object is worth showing, and only a few fields of it.
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement.GetProperty("data");

            string model = data.TryGetProperty("model", out var m) ? m.GetString() ?? "?" : "?";
            string version = data.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?";
            string uid = data.TryGetProperty("uid", out var u) ? u.GetString() ?? "?" : "?";
            int width = data.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            int height = data.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

            DeviceInfoText.Text =
                $"{model}  ·  {width}×{height}  ·  firmware {version}\n" +
                $"serial {uid}  on  {_app.Display.Port}";
        }
        catch
        {
            DeviceInfoText.Text = $"Connected on {_app.Display.Port}.";
        }
    }

    private void ShowFfmpegState()
    {
        string? path = DisplayService.FindFfmpeg();
        FfmpegText.Text = path is null
            ? "ffmpeg was not found. Install it with:\n" +
              "    winget install \"FFmpeg (Essentials Build)\"\n" +
              "or put ffmpeg.exe beside this program. Without it only images that " +
              "are already 960×480 JPEG under 80 KB can be shown."
            : $"ffmpeg: {path}";
    }

    /// <summary>
    /// Roughly what a megabyte of preprocessed frames is worth in playing time.
    /// A 960x480 frame lands around 40 KB, so 30 fps costs about 70 MB a minute
    /// — close enough to make a limit mean something before you pick it.
    /// </summary>
    private const double MegabytesPerVideoMinute = 70.0;

    private static readonly int[] MemoryPresetsMB = { 128, 256, 512, 1024, 2048, 4096 };

    private static readonly int[] DiskPresetsMB =
        { 1024, 2048, 4096, 8192, 16384, 32768, 65536 };

    /// <summary>Suppresses the change handler while the list is being rebuilt.</summary>
    private bool _populatingBudgets;

    private static string FormatSize(int megabytes) =>
        megabytes >= 1024 ? $"{megabytes / 1024} GB" : $"{megabytes} MB";

    private static string PlayingTime(int megabytes)
    {
        double minutes = megabytes / MegabytesPerVideoMinute;
        if (minutes < 1) return "under a minute";
        if (minutes < 90) return $"about {Math.Round(minutes)} minutes";
        return $"about {minutes / 60:0.#} hours";
    }

    /// <summary>
    /// Rebuilds the limit list for the selected mode. Only one limit is ever
    /// relevant — Off has none and the other two never apply at once — so this
    /// is one control that changes meaning rather than two that sit half-unused.
    /// </summary>
    private void PopulateBudgets(PreprocessMode mode)
    {
        if (mode == PreprocessMode.Off)
        {
            BudgetRow.Visibility = Visibility.Collapsed;
            return;
        }

        bool disk = mode == PreprocessMode.Disk;

        BudgetLabel.Text = disk ? "Disk limit" : "Memory limit";
        BudgetRow.Visibility = Visibility.Visible;

        int current = disk ? _app.Settings.DiskBudgetMB : _app.Settings.MemoryBudgetMB;

        // A value hand-edited into settings.json is kept as an option of its
        // own rather than silently snapped to the nearest preset.
        var choices = new List<int>(disk ? DiskPresetsMB : MemoryPresetsMB);
        if (!choices.Contains(current))
        {
            choices.Add(current);
            choices.Sort();
        }

        _populatingBudgets = true;
        BudgetBox.Items.Clear();

        foreach (int mb in choices)
        {
            BudgetBox.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock
                {
                    Text = $"{FormatSize(mb)} — {PlayingTime(mb)} of video",
                    Foreground = ComboText,
                },
            });
        }

        BudgetBox.SelectedIndex = choices.IndexOf(current);
        _budgetChoices = choices;
        _populatingBudgets = false;
    }

    private List<int> _budgetChoices = new();

    /// <summary>
    /// Says what the selected mode actually costs. The frame-rate caveat is
    /// worth stating out loud: the rate is part of what a preprocessed frame is,
    /// so changing it throws every stored one away.
    /// </summary>
    private void ShowPreprocessHint()
    {
        if (PreprocessHint is null) return;

        var mode = (PreprocessMode)Math.Max(0, PreprocessBox.SelectedIndex);

        PopulateBudgets(mode);

        PreprocessHint.Text = mode switch
        {
            PreprocessMode.Memory =>
                "ffmpeg runs once per item and then stops. Frames are held in memory and " +
                "rebuilt each time the app starts. Anything that would not fit the limit " +
                "falls back to streaming on its own, so raising it only widens what " +
                "benefits — it never decides what will play.",
            PreprocessMode.Disk =>
                "ffmpeg runs once per item, ever. Roughly 70 MB per minute of video, " +
                "evicted least-recently-used once the limit is reached. Changing rotation, " +
                "stretch or frame rate rebuilds them.",
            _ =>
                "ffmpeg runs for as long as something is on the panel.",
        };

        PackCachePanel.Visibility = mode == PreprocessMode.Disk
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (mode == PreprocessMode.Disk) ShowPackCacheSize();
    }

    private void ShowPackCacheSize()
    {
        long bytes = DisplayService.PackCacheBytes();
        string used = bytes < 1024L * 1024
            ? "empty"
            : $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";

        PackCacheText.Text = $"Cache: {used} of {FormatSize(_app.Settings.DiskBudgetMB)}";
    }

    private void OnPreprocessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;

        // Applied as soon as it is picked rather than on close: the next item to
        // start should already use it, and the hint below is about to describe
        // behaviour the session is not yet in otherwise.
        _app.Settings.Preprocess = (PreprocessMode)Math.Max(0, PreprocessBox.SelectedIndex);
        _app.Display.ApplyPreprocess();
        ShowPreprocessHint();
    }

    private void OnBudgetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _populatingBudgets) return;

        int index = BudgetBox.SelectedIndex;
        if (index < 0 || index >= _budgetChoices.Count) return;

        if (_app.Settings.Preprocess == PreprocessMode.Disk)
        {
            _app.Settings.DiskBudgetMB = _budgetChoices[index];
            ShowPackCacheSize();

            // Lowering the limit does not reclaim anything until the next pack
            // is written, so say so rather than leaving a figure that looks
            // like it is over the limit and being ignored.
            if (DisplayService.PackCacheBytes() > _app.Settings.DiskBudgetMB * 1024L * 1024L)
                PackCacheText.Text += " — trimmed as new items are added";
        }
        else
        {
            _app.Settings.MemoryBudgetMB = _budgetChoices[index];
        }

        _app.Display.ApplyPreprocess();
    }

    private void OnClearPackCache(object sender, RoutedEventArgs e)
    {
        DisplayService.ClearPackCache();
        ShowPackCacheSize();
    }

    private void OnFpsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider's Minimum of 1 coerces its default value of 0 during XAML
        // parsing, which raises this before the rest of the tree exists. The
        // named fields are still null at that point.
        if (FpsValue is null) return;

        int value = (int)Math.Round(e.NewValue);
        FpsValue.Text = value.ToString();
        if (_loaded) _app.Settings.Fps = value;
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        bool wanted = StartWithWindowsBox.IsChecked == true;

        if (!StartupTask.Apply(wanted, out string error))
        {
            MessageBox.Show(this,
                $"Could not {(wanted ? "create" : "remove")} the startup task.\n\n{error}",
                "Startup", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Snap back to whatever is actually registered, so the box never
            // claims something that is not true.
            StartWithWindowsBox.IsChecked = StartupTask.IsRegistered();
        }

        _app.Settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
    }

    // -----------------------------------------------------------------------
    // Sensors
    // -----------------------------------------------------------------------

    private void OnSensorSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;

        _app.Settings.UseLibreHardwareMonitor = LhmBox.IsChecked == true;
        _app.Settings.UseHwInfo = HwInfoBox.IsChecked == true;
        ShowSensorState();
    }

    /// <summary>
    /// Says which source is actually answering, and — the part that matters —
    /// says so plainly when none is. A CPU temperature layer that reads "--"
    /// looks like a broken app rather than a missing helper, and this is the one
    /// place that can explain the difference.
    /// </summary>
    private void ShowSensorState()
    {
        var registry = _app.Overlay?.Sensors;

        if (registry == null)
        {
            SensorStatusText.Text = "";
            return;
        }

        var live = new List<string>();
        foreach (Services.Sensors.ISensorProvider p in registry.Providers)
            if (p.Available && (p.Name.StartsWith("Libre") || p.Name.StartsWith("HWiNFO")))
                live.Add(p.Name);

        bool haveTemp = registry.Snapshot()["cpu.temp"].Available;

        if (haveTemp)
        {
            SensorStatusText.Foreground = (Brush)FindResource("TextDim");
            SensorStatusText.Text = $"Reading from {string.Join(" and ", live)}. "
                                    + "CPU temperature is available.";
            return;
        }

        SensorStatusText.Foreground = (Brush)FindResource("Accent");
        SensorStatusText.Text = live.Count > 0
            ? $"{string.Join(" and ", live)} is running but is not reporting a CPU "
              + "temperature yet."
            : "Neither is running, so CPU temperature, fan speeds and coolant "
              + "temperature are unavailable. Changes here take effect next launch.";
    }

    private void OnReleaseDevice(object sender, RoutedEventArgs e)
    {
        _app.Player.Stop();
        _app.Display.ReleaseDevice();
        AutoReconnectBox.IsChecked = false;
        ShowDeviceInfo();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Storage.EnsureDirectories();
        Process.Start(new ProcessStartInfo(Storage.Directory) { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Save();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        Save();
        base.OnClosing(e);
    }

    private void Save()
    {
        var s = _app.Settings;

        s.Rotate = Math.Max(0, RotateBox.SelectedIndex) * 90;
        s.Stretch = StretchBox.IsChecked == true;
        s.Fps = (int)Math.Round(FpsSlider.Value);
        s.Hwaccel = HwaccelValues[Math.Max(0, HwaccelBox.SelectedIndex)];
        s.Preprocess = (PreprocessMode)Math.Max(0, PreprocessBox.SelectedIndex);
        s.StartMinimised = StartMinimisedBox.IsChecked == true;
        s.ResumeOnStart = ResumeBox.IsChecked == true;

        if (_app.Ai.StartWithApp != (StartAiBox.IsChecked == true))
        {
            _app.Ai.StartWithApp = StartAiBox.IsChecked == true;
            Storage.SaveAi(_app.Ai);
        }

        s.AutoReconnect = AutoReconnectBox.IsChecked == true;
        s.BlankOnExit = BlankOnExitBox.IsChecked == true;
        s.Port = PortBox.Text.Trim();

        s.UseLibreHardwareMonitor = LhmBox.IsChecked == true;
        s.UseHwInfo = HwInfoBox.IsChecked == true;

        Storage.SaveSettings(s);
    }
}
