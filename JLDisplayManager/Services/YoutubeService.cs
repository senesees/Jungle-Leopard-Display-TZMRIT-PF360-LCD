using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services;

/// <summary>
/// One way to run yt-dlp that was actually found on the machine.
///
/// yt-dlp is a free single-file program (or a pip module); how it is invoked
/// depends on how the user installed it, so the finder tries each form against
/// --version and keeps the first that runs.
/// </summary>
public sealed class YtDlp
{
    private readonly string _executable;
    private readonly string[] _moduleArgs;

    private YtDlp(string executable, string[] moduleArgs)
    {
        _executable = executable;
        _moduleArgs = moduleArgs;
    }

    /// <summary>Every way yt-dlp might be installed, tried in order.</summary>
    private static readonly (string? Executable, string[] ModuleArgs)[] Candidates =
    {
        (BundledPath(), Array.Empty<string>()),
        ("yt-dlp.exe", Array.Empty<string>()),
        ("yt-dlp", Array.Empty<string>()),
        ("python", new[] { "-m", "yt_dlp" }),
        ("python3", new[] { "-m", "yt_dlp" }),
        ("py", new[] { "-m", "yt_dlp" }),
    };

    /// <summary>
    /// The copy of yt-dlp shipped beside the manager, if it is there.
    ///
    /// Checked first so a download works on any machine with no pip install or
    /// PATH entry. Falls back to null so the finder moves on to a system one.
    /// </summary>
    private static string? BundledPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The invocation found last time, so the dialog and the download that
    /// follows it do not each pay for the probe. The bundled yt-dlp is a packed
    /// Python program that unpacks itself on every start, which makes even
    /// --version cost the better part of a second.
    /// </summary>
    private static YtDlp? _cached;

    public static YtDlp? Find()
    {
        if (_cached is not null) return _cached;

        foreach (var (executable, moduleArgs) in Candidates)
        {
            if (executable is null) continue;
            if (Runs(executable, moduleArgs)) return _cached = new YtDlp(executable, moduleArgs);
        }

        return null;
    }

    /// <summary>How the found invocation is run, for a status line.</summary>
    public string Description =>
        _moduleArgs.Length > 0 ? $"{_executable} {_moduleArgs[1]}" : _executable;

    private static bool Runs(string executable, string[] moduleArgs)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in moduleArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs yt-dlp to completion and hands back its exit code.
    ///
    /// Cancelling kills the process rather than just abandoning the wait, so a
    /// cancelled download stops using the network instead of carrying on
    /// invisibly. Blocks until the process exits, so call it off the UI thread.
    /// </summary>
    public int Run(string[] args, Action<string>? onLine, CancellationToken token = default)
    {
        var psi = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // yt-dlp writes UTF-8. Without saying so, .NET decodes it as the
            // system code page and a title with anything outside it comes back
            // corrupted.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in _moduleArgs) psi.ArgumentList.Add(a);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        // Both drained concurrently so reading progress never deadlocks on a
        // full stderr buffer while we wait for stdout. The handlers run on
        // thread pool threads, so onLine must not touch the UI directly.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) onLine?.Invoke(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }))
        {
            process.WaitForExit();
        }

        token.ThrowIfCancellationRequested();
        return process.ExitCode;
    }
}

/// <summary>
/// Downloads a YouTube video with yt-dlp and returns the file on disk, ready to
/// be added to the library.
///
/// The panel only cares that ffmpeg can decode the result, so any container
/// yt-dlp produces would work; mp4 is asked for so the library holds one kind
/// of file and the thumbnailer never meets a container it cannot seek.
/// </summary>
public static class YoutubeService
{
    private static readonly Regex _progress =
        new(@"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);

    /// <summary>
    /// The advisory yt-dlp prints on every YouTube extraction when no
    /// JavaScript runtime is installed. It is not a failure, and it must never
    /// be shown as the reason for one: it appears on every run, so it would
    /// stand in for the real error whenever a download fails without yt-dlp
    /// printing an ERROR of its own.
    /// </summary>
    private const string JsRuntimeAdvisory = "No supported JavaScript runtime";

    /// <summary>Runtimes yt-dlp can drive, best first.</summary>
    private static readonly string[] JsRuntimeNames = { "deno", "node", "bun" };

    /// <summary>How yt-dlp is available, or null if it is not installed.</summary>
    public static string? FindYtDlp() => YtDlp.Find()?.Description;

    /// <summary>
    /// A JavaScript runtime for yt-dlp to sign YouTube requests with, as
    /// "name:path", or null if the machine has none.
    ///
    /// YouTube's player is JavaScript, and yt-dlp has deprecated extracting
    /// without a runtime to run it: without one, formats go missing and the
    /// good ones are usually the ones that disappear. Only deno is enabled by
    /// default, so anything else has to be pointed at explicitly.
    /// </summary>
    private static string? FindJsRuntime()
    {
        foreach (string name in JsRuntimeNames)
        {
            string? path = OnPath(name + ".exe");
            // The full path rather than the bare name: the app inherits
            // whatever PATH it was launched with, which is not always the one
            // the runtime was installed onto.
            if (path is not null) return $"{name}:{path}";
        }

        return null;
    }

    private static string? OnPath(string executable)
    {
        string? paths = Environment.GetEnvironmentVariable("PATH");
        if (paths is null) return null;

        foreach (string dir in paths.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            try
            {
                string candidate = Path.Combine(dir.Trim(), executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // A malformed PATH entry is not worth failing the search over.
            }
        }

        return null;
    }

    /// <summary>
    /// Runs one video and returns the downloaded file path.
    ///
    /// Throws <see cref="InvalidOperationException"/> carrying what yt-dlp
    /// actually said when the download fails. The caller shows that text:
    /// "produced nothing" on its own gives the user nothing to act on, while
    /// the real reason is usually specific and fixable.
    /// </summary>
    /// <param name="onStage">Called with what yt-dlp is doing now. Runs on a thread pool thread.</param>
    /// <param name="onProgress">Called with 0-100 as yt-dlp reports it. Runs on a thread pool thread.</param>
    public static string Download(
        string url,
        Action<string>? onStage = null,
        Action<double>? onProgress = null,
        CancellationToken token = default)
    {
        YtDlp? ytdlp = YtDlp.Find();
        if (ytdlp is null)
            throw new InvalidOperationException("yt-dlp was not found.");

        Storage.EnsureDirectories();

        // A template rather than a fixed name, so the library caption is the
        // video's title and two downloads never collide.
        string template = Path.Combine(Storage.DownloadDirectory, "%(title)s.%(ext)s");

        var args = new List<string>
        {
            // Best video plus best audio, falling back to a pre-merged stream
            // where the two cannot be had separately, and to any height at all
            // where nothing fits the cap.
            //
            // Capped at 720p because the panel is 960x480: a 4K source is a
            // several-hundred-megabyte download that gets thrown away in the
            // downscale. 720p still leaves headroom for a clean one. The "<=?"
            // form drops the filter rather than the format when a stream does
            // not declare its height.
            "-f", "bv*[height<=?720]+ba/b[height<=?720]/bv*+ba/b",
            "--merge-output-format", "mp4",
            "--no-playlist",
            "--no-mtime",
            "--windows-filenames",
            // Progress as whole lines rather than one line redrawn in place, so
            // the regex sees every update instead of a single run-together line.
            "--newline",
            // --print-to-file below implies --quiet, which takes the progress
            // with it. This puts it back; without it the dialog sits on one
            // message for the whole download with nothing to show it is alive.
            "--progress",
            "--no-color",
            "-o", template,
        };

        // Merging separate video and audio needs ffmpeg. The app already
        // locates one for thumbnails, so pointing yt-dlp at the same copy means
        // a machine with no ffmpeg on PATH still merges.
        string? ffmpeg = null;
        try
        {
            ffmpeg = DisplayService.FindFfmpeg();
        }
        catch
        {
            // The lookup crosses into JLDisplayNative.dll. A download should not
            // be the thing that fails when that is missing: yt-dlp falls back to
            // a pre-merged stream on its own.
        }

        if (ffmpeg is not null)
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg);
        }

        string? js = FindJsRuntime();
        if (js is not null)
        {
            args.Add("--js-runtimes");
            args.Add(js);
        }

        // The finished name is yt-dlp's to decide: the title template, the
        // merge, and --windows-filenames all rewrite it. Asking for the path it
        // settled on beats guessing from a directory listing, which cannot tell
        // this download's file from one an earlier run left behind.
        //
        // To a file rather than to stdout, because yt-dlp encodes stdout with
        // the console code page: --windows-filenames swaps characters Windows
        // forbids for fullwidth stand-ins, and every one of those is outside
        // cp1252, so a printed path comes back with the character replaced and
        // no longer names a file that exists. The file it writes is UTF-8.
        string pathFile = Path.Combine(
            Path.GetTempPath(), $"jl-ytdlp-{Guid.NewGuid():N}.txt");

        args.Add("--print-to-file");
        args.Add("after_move:%(filepath)s");
        args.Add(pathFile);

        args.Add(url);

        var complaints = new List<string>();

        try
        {
            int exit = ytdlp.Run(args.ToArray(), line =>
            {
                Match m = _progress.Match(line);
                if (m.Success &&
                    double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double pct))
                {
                    onProgress?.Invoke(pct);
                    return;
                }

                string? stage = StageOf(line);
                if (stage is not null)
                {
                    onStage?.Invoke(stage);
                    return;
                }

                if (line.Contains(JsRuntimeAdvisory, StringComparison.Ordinal)) return;

                if (line.StartsWith("ERROR:", StringComparison.Ordinal) ||
                    line.StartsWith("WARNING:", StringComparison.Ordinal))
                {
                    // Both pipes are drained on their own threads, so this list
                    // is reached from two of them.
                    lock (complaints) complaints.Add(line);
                }
            }, token);

            string? produced = ReadProducedPath(pathFile);

            if (produced is not null && File.Exists(produced) && MediaItem.IsSupported(produced))
                return produced;

            // No usable file. Prefer yt-dlp's own words; its ERROR lines say why
            // far better than an exit code does.
            List<string> said;
            lock (complaints) said = new List<string>(complaints);

            string reason = Explain(said)
                ?? (exit == 0
                    ? "yt-dlp finished but wrote no file the player can read."
                    : $"yt-dlp exited with code {exit}.");

            Storage.Log($"youtube: {url} failed - {reason}");
            throw new InvalidOperationException(reason);
        }
        finally
        {
            try { File.Delete(pathFile); } catch { }
        }
    }

    /// <summary>
    /// The path yt-dlp recorded for the finished file, or null if it never got
    /// that far.
    /// </summary>
    private static string? ReadProducedPath(string pathFile)
    {
        try
        {
            if (!File.Exists(pathFile)) return null;

            // One line per finished file; --no-playlist means there is only
            // ever one, but the last is the right one either way.
            string[] lines = File.ReadAllLines(pathFile, Encoding.UTF8);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length > 0) return line;
            }
        }
        catch (Exception ex)
        {
            Storage.Log($"youtube: could not read the download path - {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// What a yt-dlp line says it is doing, in words worth putting on screen,
    /// or null if the line is not about a change of stage.
    ///
    /// A download of separate video and audio runs the percentage from zero
    /// twice and then merges, which looks like a stall or a restart with only a
    /// number to go on. Naming the stage is what makes that legible.
    /// </summary>
    private static string? StageOf(string line)
    {
        if (line.StartsWith("[Merger]", StringComparison.Ordinal) ||
            line.Contains("Merging formats", StringComparison.Ordinal))
            return "Merging video and audio…";

        // yt-dlp skips a file it already has, so there is no progress to show
        // and the dialog would otherwise finish without ever saying why.
        if (line.Contains("has already been downloaded", StringComparison.Ordinal))
            return "Already downloaded — adding it.";

        if (line.StartsWith("[download] Destination:", StringComparison.Ordinal))
            return "Downloading…";

        if (line.StartsWith("[info]", StringComparison.Ordinal) &&
            line.Contains("Downloading", StringComparison.Ordinal))
            return "Starting download…";

        if (line.StartsWith("[youtube]", StringComparison.Ordinal) ||
            line.StartsWith("[youtube:", StringComparison.Ordinal))
            return "Fetching video details…";

        if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal) ||
            line.StartsWith("[VideoConvertor]", StringComparison.Ordinal))
            return "Converting…";

        return null;
    }

    /// <summary>
    /// The most useful line out of a failed run, with yt-dlp's own prefix
    /// trimmed so the dialog shows a sentence rather than a log line.
    ///
    /// The last of each kind rather than the first: yt-dlp opens with generic
    /// warnings (a missing JS runtime, a format-selection note) and only says
    /// what actually went wrong once it has tried.
    /// </summary>
    private static string? Explain(List<string> lines)
    {
        string? error = null;
        string? warning = null;

        foreach (string line in lines)
        {
            if (line.StartsWith("ERROR:", StringComparison.Ordinal)) error = line;
            else warning = line;
        }

        string? pick = error ?? warning;
        if (pick is null) return null;

        int colon = pick.IndexOf(':');
        return colon >= 0 ? pick[(colon + 1)..].Trim() : pick;
    }
}
