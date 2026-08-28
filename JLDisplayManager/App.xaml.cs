using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

using JLDisplayManager.Models;
using JLDisplayManager.Services;
using JLDisplayManager.Views;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace JLDisplayManager;

/// <summary>
/// Process lifetime and the tray presence.
///
/// The window is a view onto the service, not the app itself: closing it hides
/// it, and the session keeps the panel lit. That is the whole point of a daemon
/// with a front end, and it is why ShutdownMode is OnExplicitShutdown.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\JungleLeopardDisplayManager.Instance";
    private const string ShowEventName = @"Local\JungleLeopardDisplayManager.Show";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private NotifyIcon? _tray;
    private MainWindow? _window;

    public AppSettings Settings { get; private set; } = new();
    public AppLibrary Library { get; private set; } = new();
    public DisplayService Display { get; private set; } = null!;
    public PlaylistPlayer Player { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A tray app spends most of its life with no window on screen, so an
        // unhandled exception would otherwise vanish with nothing to show for
        // it. Everything lands in manager.log next to the settings.
        DispatcherUnhandledException += (_, args) =>
        {
            Storage.Log("UNHANDLED (ui): " + args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Storage.Log("UNHANDLED: " + args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Background thumbnailing and calibration run detached; a failure
            // there should be recorded, not take the process down.
            Storage.Log("UNOBSERVED (task): " + args.Exception);
            args.SetObserved();
        };

        // Only one process may hold the COM port, so only one may run. A second
        // launch — from the Start menu, or the logon task racing a manual start
        // — hands over to the one already running instead of failing.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        _ownsMutex = isFirst;
        if (!isFirst)
        {
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch
            {
                // The other instance is starting or stopping; either way there
                // is nothing useful this one can do.
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => Dispatcher.Invoke(ShowWindow), null, -1, false);

        Storage.EnsureDirectories();
        Settings = Storage.LoadSettings();
        Library = Storage.LoadLibrary();

        try
        {
            Display = new DisplayService(Settings);
            Player = new PlaylistPlayer(Display, Library);
            Display.Start();
        }
        catch (Exception ex)
        {
            // Almost always a missing or mismatched JLDisplayNative.dll. There is
            // no recovering from it, and a silent tray icon that does nothing
            // would be worse than saying so.
            MessageBox.Show(
                $"Could not start the display service.\n\n{ex.Message}",
                "Jungle Leopard Display", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        BuildTray();
        WarnIfFfmpegMissing();

        bool startHidden = Settings.StartMinimised
            || Array.Exists(e.Args, a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow();
        if (!startHidden) _window.Show();

        if (Settings.ResumeOnStart) Resume();
    }

    /// <summary>Puts back whatever was on the panel when the app last closed.</summary>
    private void Resume()
    {
        if (Library.LastWasPlaylist && Library.Playlist.Count > 0)
        {
            Player.Start(Library.LastPlayed);
            return;
        }

        if (Library.LastPlayed is { } id)
        {
            var item = Library.Items.Find(i => i.Id == id);
            if (item is { FileExists: true }) Display.Play(item);
        }
    }

    private void WarnIfFfmpegMissing()
    {
        if (DisplayService.FindFfmpeg() is not null) return;

        // Checked once here rather than per item: without ffmpeg every piece of
        // content fails the same way, and one clear message beats a stream of
        // identical ones.
        Storage.Log("ffmpeg not found");
        _tray?.ShowBalloonTip(10000, "ffmpeg not found",
            "Install it with:  winget install \"FFmpeg (Essentials Build)\"\n" +
            "or put ffmpeg.exe next to this program.",
            ToolTipIcon.Warning);
    }

    // -----------------------------------------------------------------------
    // Tray
    // -----------------------------------------------------------------------

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Play playlist", null, (_, _) => Player.Start());
        menu.Items.Add("Stop", null, (_, _) => { Player.Stop(); Display.Stop(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Jungle Leopard Display",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        Display.PropertyChanged += (_, _) => UpdateTrayTooltip();
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;

        // The tray tooltip is capped at 63 characters and silently truncates,
        // so keep it short rather than letting a long filename eat the status.
        string text = $"Jungle Leopard — {Display.StateText}";
        _tray.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    private static Icon LoadIcon()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (File.Exists(exe) && Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch
        {
            // Falls through to the stock icon below.
        }
        return SystemIcons.Application;
    }

    public void ShowWindow()
    {
        _window ??= new MainWindow();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>The only path that actually ends the process.</summary>
    public void ExitApp()
    {
        SaveAll();

        Player.Stop();
        if (Settings.BlankOnExit) Display.Stop();
        Display.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        Shutdown();
    }

    public void SaveAll()
    {
        Library.LastWasPlaylist = Player.Running;
        Library.LastPlayed = Player.Running
            ? Player.CurrentItem?.Id
            : Display.Current?.Id;

        Storage.SaveSettings(Settings);
        Storage.SaveLibrary(Library);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only the instance that actually acquired it may release it; a second
        // instance holds a handle to the same mutex but no ownership, and
        // releasing that throws ApplicationException on the way out.
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
