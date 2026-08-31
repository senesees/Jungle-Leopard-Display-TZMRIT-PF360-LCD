using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JLDisplayManager.Models;

/// <summary>
/// How much of the transcoding happens before an item starts rather than while
/// it plays. Mirrors JlPreprocess on the native side.
/// </summary>
public enum PreprocessMode
{
    /// <summary>ffmpeg streams for as long as something is on the panel.</summary>
    Off = 0,

    /// <summary>Frames are built into RAM once, then ffmpeg stops.</summary>
    Memory = 1,

    /// <summary>Frames are built into a file once and reused across runs.</summary>
    Disk = 2,
}

/// <summary>
/// Everything the app remembers between runs, split in two files: settings the
/// user tweaks, and the library they build up. Both live under LOCALAPPDATA
/// next to the calibration cache the CLI already writes.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private int _rotate;
    private bool _stretch;
    private int _fps = 30;
    private int _brightness = 100;
    private string _hwaccel = "auto";
    private string _port = "";
    private bool _startWithWindows;
    private bool _startMinimised = true;
    private bool _resumeOnStart = true;
    private bool _autoReconnect = true;
    private bool _blankOnExit = true;
    private PreprocessMode _preprocess = PreprocessMode.Memory;
    private int _memoryBudgetMB = 512;
    private int _diskBudgetMB = 8192;

    /// <summary>Global default; a MediaItem may override it.</summary>
    public int Rotate
    {
        get => _rotate;
        set => Set(ref _rotate, Normalise(value));
    }

    public bool Stretch
    {
        get => _stretch;
        set => Set(ref _stretch, value);
    }

    public int Fps
    {
        get => _fps;
        set => Set(ref _fps, Math.Clamp(value, 1, 60));
    }

    public int Brightness
    {
        get => _brightness;
        set => Set(ref _brightness, Math.Clamp(value, 0, 100));
    }

    /// <summary>"auto", "none", or an explicit ffmpeg method such as "cuda".</summary>
    public string Hwaccel
    {
        get => _hwaccel;
        set => Set(ref _hwaccel, value ?? "auto");
    }

    /// <summary>
    /// Whether frames are prepared up front. Memory is the default: it writes
    /// nothing, is bounded, and quietly falls back to streaming for a source too
    /// long to hold — so the worst case is exactly the old behaviour.
    /// </summary>
    public PreprocessMode Preprocess
    {
        get => _preprocess;
        set => Set(ref _preprocess, value);
    }

    /// <summary>
    /// Ceiling for Memory mode, in megabytes. A source whose frames would not
    /// fit streams instead, so this trades RAM for how much of the library gets
    /// the benefit rather than deciding what will play.
    /// </summary>
    public int MemoryBudgetMB
    {
        get => _memoryBudgetMB;
        set => Set(ref _memoryBudgetMB, Math.Clamp(value, 32, 16384));
    }

    /// <summary>
    /// Ceiling for the Disk pack cache, in megabytes. Reaching it evicts the
    /// least recently used packs rather than refusing to build new ones.
    /// </summary>
    public int DiskBudgetMB
    {
        get => _diskBudgetMB;
        set => Set(ref _diskBudgetMB, Math.Clamp(value, 128, 262144));
    }

    /// <summary>Empty means autodetect by hardware ID, which is nearly always right.</summary>
    public string Port
    {
        get => _port;
        set => Set(ref _port, value ?? "");
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => Set(ref _startWithWindows, value);
    }

    public bool StartMinimised
    {
        get => _startMinimised;
        set => Set(ref _startMinimised, value);
    }

    /// <summary>Put back whatever was showing when the app last closed.</summary>
    public bool ResumeOnStart
    {
        get => _resumeOnStart;
        set => Set(ref _resumeOnStart, value);
    }

    public bool AutoReconnect
    {
        get => _autoReconnect;
        set => Set(ref _autoReconnect, value);
    }

    /// <summary>
    /// Whether to leave the panel dark on exit. Off means the last frame stays
    /// frozen on the glass, which the firmware is happy to do indefinitely.
    /// </summary>
    public bool BlankOnExit
    {
        get => _blankOnExit;
        set => Set(ref _blankOnExit, value);
    }

    private static int Normalise(int degrees)
    {
        int d = ((degrees % 360) + 360) % 360;
        return d - (d % 90);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>The library and the playlist built from it.</summary>
public sealed class AppLibrary
{
    public List<MediaItem> Items { get; set; } = new();

    /// <summary>Ids into <see cref="Items"/>, in play order. Duplicates allowed.</summary>
    public List<Guid> Playlist { get; set; } = new();

    public bool Shuffle { get; set; }

    /// <summary>What to restore on launch when ResumeOnStart is set.</summary>
    public Guid? LastPlayed { get; set; }

    public bool LastWasPlaylist { get; set; }
}

public static class Storage
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JungleLeopardDisplay");

    public static string ThumbnailDirectory { get; } = Path.Combine(Directory, "thumbnails");

    /// <summary>Where a YouTube download lands before it is added to the library.</summary>
    public static string DownloadDirectory { get; } = Path.Combine(Directory, "downloads");

    /// <summary>Where the AI pipeline writes what it generates.</summary>
    public static string GeneratedDirectory { get; } = Path.Combine(Directory, "generated");

    private static string SettingsPath => Path.Combine(Directory, "settings.json");
    private static string LibraryPath => Path.Combine(Directory, "library.json");
    private static string AiPath => Path.Combine(Directory, "ai.json");
    public static string LogPath => Path.Combine(Directory, "manager.log");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(ThumbnailDirectory);
        System.IO.Directory.CreateDirectory(DownloadDirectory);
        System.IO.Directory.CreateDirectory(GeneratedDirectory);
    }

    public static AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();

    public static AppLibrary LoadLibrary() => Load<AppLibrary>(LibraryPath) ?? new AppLibrary();

    public static AiSettings LoadAi()
    {
        var ai = Load<AiSettings>(AiPath) ?? new AiSettings();

        // An untouched system prompt from an older version is moved on to the
        // current default. Anything the user has edited is left as they wrote it.
        if (AiSettings.IsSupersededSystemPrompt(ai.SystemPrompt))
            ai.SystemPrompt = AiSettings.DefaultSystemPrompt;

        return ai;
    }

    public static void SaveSettings(AppSettings s) => Save(SettingsPath, s);

    public static void SaveLibrary(AppLibrary l) => Save(LibraryPath, l);

    public static void SaveAi(AiSettings a) => Save(AiPath, a);

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop the app from starting; the
            // worst case is that it comes up with defaults.
            Log($"could not read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        try
        {
            EnsureDirectories();
            // Write beside and move, so a crash mid-write cannot leave a
            // half-written file where the real one was.
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"could not write {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public static void Log(string message)
    {
        try
        {
            EnsureDirectories();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the thing that breaks the app.
        }
    }
}
