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
        System.IO.Directory.CreateDirectory(GeneratedDirectory);
    }

    public static AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();

    public static AppLibrary LoadLibrary() => Load<AppLibrary>(LibraryPath) ?? new AppLibrary();

    public static AiSettings LoadAi() => Load<AiSettings>(AiPath) ?? new AiSettings();

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
