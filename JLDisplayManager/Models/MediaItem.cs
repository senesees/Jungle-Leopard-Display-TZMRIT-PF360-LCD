using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace JLDisplayManager.Models;

/// <summary>
/// One thing that can be put on the panel. Stills and videos differ enough in
/// how they end — a still never does, a video does — that the distinction is
/// baked in here rather than inferred at every call site.
/// </summary>
public sealed class MediaItem : INotifyPropertyChanged
{
    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".gif", ".wmv", ".m4v", ".mpg", ".mpeg", ".flv"
    };

    public static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".avif", ".heic"
    };

    private string _name = "";
    private int _dwellSeconds = 15;
    private bool _loop = true;
    private int? _rotate;
    private bool? _stretch;
    private string? _thumbnailPath;
    private BitmapSource? _thumbnail;
    private CalibrationState _calibration = CalibrationState.Unknown;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Path { get; set; } = "";

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public bool IsVideo { get; set; }

    /// <summary>How long a still stays up before the playlist moves on.</summary>
    public int DwellSeconds
    {
        get => _dwellSeconds;
        set => Set(ref _dwellSeconds, Math.Clamp(value, 1, 86400));
    }

    /// <summary>
    /// Only consulted when this video is playing on its own. In a playlist of
    /// more than one item a looping video would never hand over, so the player
    /// overrides it — see PlaylistPlayer.
    /// </summary>
    public bool Loop
    {
        get => _loop;
        set => Set(ref _loop, value);
    }

    /// <summary>Per-item override of the global rotation; null follows settings.</summary>
    public int? Rotate
    {
        get => _rotate;
        set => Set(ref _rotate, value);
    }

    public bool? Stretch
    {
        get => _stretch;
        set => Set(ref _stretch, value);
    }

    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => Set(ref _thumbnailPath, value);
    }

    /// <summary>
    /// The decoded thumbnail, held here so the grid can bind straight to it
    /// rather than going through a converter that would re-decode on every
    /// container recycle.
    /// </summary>
    [JsonIgnore]
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    /// <summary>
    /// Whether this video's JPEG quality has been surveyed yet. Not persisted:
    /// the real cache lives beside the CLI's, keyed on the file's timestamp, so
    /// this is only what the UI knows about this session.
    /// </summary>
    [JsonIgnore]
    public CalibrationState Calibration
    {
        get => _calibration;
        set => Set(ref _calibration, value);
    }

    [JsonIgnore]
    public bool FileExists => File.Exists(Path);

    [JsonIgnore]
    public string Kind => IsVideo ? "Video" : "Image";

    public static MediaItem FromPath(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

        // A GIF is both a valid image extension and a valid video one. Treating
        // it as video is the useful reading: an animated GIF played as a still
        // would show only its first frame.
        bool isVideo = Array.IndexOf(VideoExtensions, ext) >= 0;

        return new MediaItem
        {
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            IsVideo = isVideo,
        };
    }

    public static bool IsSupported(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(VideoExtensions, ext) >= 0
            || Array.IndexOf(ImageExtensions, ext) >= 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum CalibrationState
{
    Unknown,
    Running,
    Ready,
    Failed,
}
