using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services;

/// <summary>
/// Makes the little pictures in the library grid. Stills decode natively;
/// videos need a frame pulled out with ffmpeg, so those are cached on disk and
/// only ever generated once per file version.
/// </summary>
public static class ThumbnailService
{
    private const int Width = 320;
    private const int Height = 160;

    public static async Task EnsureThumbnailAsync(MediaItem item)
    {
        try
        {
            if (!File.Exists(item.Path)) return;

            if (!item.IsVideo)
            {
                // Nothing to cache: WPF decodes a scaled-down still cheaply, and
                // a copy on disk would just be another thing to invalidate.
                item.ThumbnailPath = item.Path;
                item.Thumbnail = Load(item.Path);
                return;
            }

            string cached = CachePathFor(item.Path);
            if (!File.Exists(cached) &&
                !await ExtractFrameAsync(item.Path, cached).ConfigureAwait(true))
            {
                return;
            }

            item.ThumbnailPath = cached;
            item.Thumbnail = Load(cached);
        }
        catch (Exception ex)
        {
            Storage.Log($"thumbnail failed for {item.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads a thumbnail at display size. DecodePixelWidth matters here: without
    /// it a grid of 4K stills would decode at full resolution and eat hundreds
    /// of megabytes.
    /// </summary>
    public static BitmapSource? Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = Width;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> ExtractFrameAsync(string source, string destination)
    {
        string? ffmpeg = DisplayService.FindFfmpeg();
        if (ffmpeg is null) return false;

        Storage.EnsureDirectories();

        // -ss before -i seeks by keyframe without decoding what came before, so
        // this stays fast even on a long file. One second in avoids the black
        // frame a lot of videos open on.
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(source);
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale={Width}:{Height}:force_original_aspect_ratio=decrease," +
                             $"pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=black");
        psi.ArgumentList.Add(destination);

        using var process = Process.Start(psi);
        if (process is null) return false;

        await process.WaitForExitAsync().ConfigureAwait(false);

        // A video shorter than the seek point produces nothing; retry from the
        // very start before giving up.
        if (process.ExitCode != 0 || !File.Exists(destination))
            return await ExtractFirstFrameAsync(ffmpeg, source, destination).ConfigureAwait(false);

        return true;
    }

    private static async Task<bool> ExtractFirstFrameAsync(string ffmpeg, string source, string destination)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(source);
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale={Width}:{Height}:force_original_aspect_ratio=decrease," +
                             $"pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=black");
        psi.ArgumentList.Add(destination);

        using var process = Process.Start(psi);
        if (process is null) return false;

        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode == 0 && File.Exists(destination);
    }

    /// <summary>
    /// Keyed on path, size and timestamp, so replacing a video in place
    /// invalidates its thumbnail the same way it invalidates its calibration.
    /// </summary>
    private static string CachePathFor(string source)
    {
        var info = new FileInfo(source);
        string material = $"{source.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        string name = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return Path.Combine(Storage.ThumbnailDirectory, name + ".jpg");
    }
}
