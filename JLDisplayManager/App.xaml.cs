using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

using JLDisplayManager.Interop;
using JLDisplayManager.Models;
using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services;
using JLDisplayManager.Services.Ai;
using JLDisplayManager.Services.Overlay;
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

    // Held so the menu can be updated in place rather than rebuilt each time.
    private ToolStripLabel? _trayPanelStatus;
    private ToolStripLabel? _trayAiStatus;
    private ToolStripMenuItem? _trayPlaylistItem;
    private ToolStripMenuItem? _trayAiItem;
    private ToolStripMenuItem? _trayOverlayItem;

    public AppSettings Settings { get; private set; } = new();
    public AppLibrary Library { get; private set; } = new();
    public AiSettings Ai { get; private set; } = new();
    public OverlaySettings Overlays { get; private set; } = new();
    public DisplayService Display { get; private set; } = null!;
    public PlaylistPlayer Player { get; private set; } = null!;
    public AiPipeline Pipeline { get; private set; } = null!;
    public OverlayService Overlay { get; private set; } = null!;

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
        Ai = Storage.LoadAi();
        Overlays = Storage.LoadOverlays();

        try
        {
            Display = new DisplayService(Settings);
            Player = new PlaylistPlayer(Display, Library);
            Pipeline = new AiPipeline(Ai, Library, Display);
            Display.Start();

            // After Display.Start(), so the native session exists before the
            // renderer starts pushing surfaces at it.
            Overlay = new OverlayService(
                () => Display.State == NativeMethods.JlState.Playing, Settings);
            Overlay.Start(Overlays);
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

        if (Ai.StartWithApp) StartAi();
        else if (Settings.ResumeOnStart) Resume();
    }

    // -----------------------------------------------------------------------
    // Panel arbitration
    //
    // The playlist and the AI pipeline both drive the one panel, so starting
    // either has to stop the other. Kept here rather than inside either class:
    // neither should have to know the other exists.
    // -----------------------------------------------------------------------

    /// <summary>Starts the AI slideshow, taking the panel from the playlist.</summary>
    public void StartAi()
    {
        Player.Stop();
        Pipeline.Start();
    }

    /// <summary>Starts the playlist, taking the panel from the AI slideshow.</summary>
    public void StartPlaylist(Guid? startAt = null)
    {
        Pipeline.Stop();
        Player.Start(startAt);
    }

    /// <summary>Stops whatever is driving the panel and shows one item.</summary>
    public void ShowOne(MediaItem item)
    {
        Pipeline.Stop();
        Player.Stop();
        Display.Play(item);
    }

    /// <summary>Stops everything, leaving the panel on its last frame.</summary>
    public void StopAll()
    {
        Pipeline.Stop();
        Player.Stop();
        Display.Stop();
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

        // Labels rather than disabled items: the point is to be read, and a
        // greyed-out menu entry reads as broken rather than as information.
        _trayPanelStatus = new ToolStripLabel
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold),
        };
        _trayAiStatus = new ToolStripLabel { Visible = false };

        menu.Items.Add(_trayPanelStatus);
        menu.Items.Add(_trayAiStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());

        _trayPlaylistItem = new ToolStripMenuItem("Play playlist", null,
            (_, _) => { if (Player.Running) { Player.Stop(); Display.Stop(); } else StartPlaylist(); });

        _trayAiItem = new ToolStripMenuItem("Start AI slideshow", null,
            (_, _) => { if (Pipeline.Running) Pipeline.Stop(); else StartAi(); });

        menu.Items.Add(_trayPlaylistItem);
        menu.Items.Add(_trayAiItem);
        menu.Items.Add("Stop", null, (_, _) => StopAll());
        menu.Items.Add(new ToolStripSeparator());

        // The overlay is a thing people turn on and off and switch between far
        // more often than they edit, so it earns a tray entry rather than
        // living only behind the editor window.
        _trayOverlayItem = new ToolStripMenuItem("Overlay");
        menu.Items.Add(_trayOverlayItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        // Refreshed on open as well as on change: the menu is not visible most
        // of the time, and this is the moment it has to be right.
        menu.Opening += (_, _) => UpdateTray();

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Jungle Leopard Display",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        Display.PropertyChanged += (_, _) => UpdateTray();
        Pipeline.PropertyChanged += (_, _) => UpdateTray();
        Player.PropertyChanged += (_, _) => UpdateTray();

        UpdateTray();
    }

    /// <summary>
    /// Keeps the tray tooltip and menu in step with what is actually happening.
    /// The tray is the only surface a user sees while the window is hidden,
    /// which is most of the time.
    /// </summary>
    private void UpdateTray()
    {
        if (_tray is null) return;

        string panel = Display.StateText;
        string ai = Pipeline.Summary;

        // The tooltip is capped at 63 characters and silently truncates, so the
        // AI line only earns its place there while the pipeline is running.
        string tip = Pipeline.Running && ai.Length > 0
            ? $"Jungle Leopard — {ai}"
            : $"Jungle Leopard — {panel}";
        _tray.Text = tip.Length <= 63 ? tip : tip[..60] + "…";

        if (_trayPanelStatus is not null) _trayPanelStatus.Text = panel;

        if (_trayAiStatus is not null)
        {
            _trayAiStatus.Text = ai;
            _trayAiStatus.Visible = ai.Length > 0;
            _trayAiStatus.ForeColor = Pipeline.HasError
                ? Color.FromArgb(0xE2, 0x60, 0x3C)
                : System.Drawing.SystemColors.ControlText;
        }

        if (_trayPlaylistItem is not null)
            _trayPlaylistItem.Text = Player.Running ? "Stop playlist" : "Play playlist";

        RebuildOverlayMenu();

        if (_trayAiItem is not null)
            _trayAiItem.Text = Pipeline.Running ? "Stop AI slideshow" : "Start AI slideshow";
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

    /// <summary>
    /// The overlay submenu: an on/off toggle, the profiles, and a way into the
    /// editor. Rebuilt each time the menu opens rather than kept in sync,
    /// because profiles can be added and renamed while it is closed.
    /// </summary>
    private void RebuildOverlayMenu()
    {
        if (_trayOverlayItem is null || Overlay is null) return;

        _trayOverlayItem.DropDownItems.Clear();

        var toggle = new ToolStripMenuItem(
            Overlay.Enabled ? "Turn overlay off" : "Turn overlay on",
            null,
            (_, _) =>
            {
                Overlay.SetEnabled(!Overlay.Enabled);
                Storage.SaveOverlays(Overlays);
                UpdateTray();
            });
        _trayOverlayItem.DropDownItems.Add(toggle);
        _trayOverlayItem.DropDownItems.Add(new ToolStripSeparator());

        Guid? active = Overlay.Profile?.Id;
        foreach (OverlayProfile p in Overlays.Profiles)
        {
            OverlayProfile captured = p;
            var item = new ToolStripMenuItem(p.Name, null, (_, _) =>
            {
                Overlays.ActiveProfileId = captured.Id;
                Overlay.Refresh(captured);
                Storage.SaveOverlays(Overlays);
                UpdateTray();
            })
            {
                Checked = p.Id == active,
            };
            _trayOverlayItem.DropDownItems.Add(item);
        }

        _trayOverlayItem.DropDownItems.Add(new ToolStripSeparator());
        _trayOverlayItem.DropDownItems.Add("Edit…", null, (_, _) =>
        {
            ShowWindow();
            _window?.OpenOverlayEditor();
        });
    }

    /// <summary>The only path that actually ends the process.</summary>
    public void ExitApp()
    {
        SaveAll();

        Pipeline.Dispose();
        Player.Stop();

        // Before Display: the renderer pushes into the native session, so it
        // has to stop while that session is still there.
        Overlay?.Dispose();

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
        Storage.SaveAi(Ai);
        Storage.SaveOverlays(Overlays);
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
