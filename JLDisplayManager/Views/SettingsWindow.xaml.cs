using System;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

using JLDisplayManager.Models;
using JLDisplayManager.Services;

using MessageBox = System.Windows.MessageBox;

namespace JLDisplayManager.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] HwaccelValues =
        { "auto", "none", "cuda", "qsv", "d3d11va", "dxva2" };

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

        // Read the real registered state rather than the stored flag: the task
        // can be removed from outside the app, and a checkbox that disagrees
        // with Task Scheduler is worse than no checkbox.
        StartWithWindowsBox.IsChecked = StartupTask.IsRegistered();

        StartMinimisedBox.IsChecked = s.StartMinimised;
        ResumeBox.IsChecked = s.ResumeOnStart;
        AutoReconnectBox.IsChecked = s.AutoReconnect;
        BlankOnExitBox.IsChecked = s.BlankOnExit;
        PortBox.Text = s.Port;

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
        s.StartMinimised = StartMinimisedBox.IsChecked == true;
        s.ResumeOnStart = ResumeBox.IsChecked == true;
        s.AutoReconnect = AutoReconnectBox.IsChecked == true;
        s.BlankOnExit = BlankOnExitBox.IsChecked == true;
        s.Port = PortBox.Text.Trim();

        Storage.SaveSettings(s);
    }
}
