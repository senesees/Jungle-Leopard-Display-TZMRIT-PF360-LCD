using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JLDisplayManager.Interop;

/// <summary>
/// One-to-one binding for JLDisplayNative.dll. The struct layouts here must stay
/// in step with jl_api.h — they are blitted, not marshalled field by field, so a
/// mismatch corrupts silently rather than throwing. <see cref="VerifyLayout"/>
/// checks the sizes at startup so a drift shows up as a clear failure instead.
/// </summary>
public static class NativeMethods
{
    private const string Dll = "JLDisplayNative.dll";

    // Mirrors JlState.
    public enum JlState
    {
        Disconnected = 0,
        Idle = 1,
        Preparing = 2,
        Calibrating = 3,
        Playing = 4,
        Error = 5,
        Preprocessing = 6,
    }

    /// <summary>
    /// Mirrors JlPreprocess: how much of the transcoding happens before playback
    /// rather than during it. Session-wide, and deliberately not part of
    /// <see cref="JlRenderOpts"/> — it changes nothing about the pixels.
    /// </summary>
    public enum JlPreprocess
    {
        Off = 0,
        Memory = 1,
        Disk = 2,
    }

    public const int JL_OK = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct JlRenderOpts
    {
        public int Stretch;
        public int Rotate;
        public int Fps;
        public int Quality;
        public int Loop;
        public int Recalibrate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Hwaccel;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct JlStatus
    {
        public int State;
        public int Connected;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Port;

        public long FramesSent;
        public long FramesDropped;
        public double Fps;

        /// <summary>Where the playing video has reached, in seconds.</summary>
        public double PositionSeconds;

        /// <summary>How long it runs, or 0 when that could not be determined.</summary>
        public double DurationSeconds;
        public int FinishedCount;
        public int FrameCount;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Message;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Error;

        /// <summary>Rolling mean of the per-frame decode + blend, in ms.</summary>
        public double OverlayComposeMs;

        /// <summary>Rolling mean of the per-frame JPEG re-encode, in ms.</summary>
        public double OverlayEncodeMs;

        /// <summary>
        /// The working JPEG quality × 100, 30–92. Falls when the panel's size
        /// cap forces it down, which is the only visible symptom of an overlay
        /// too expensive to encode — worth surfacing rather than hiding.
        /// </summary>
        public int OverlayQuality;

        /// <summary>Frames that would not fit under the cap at any quality.</summary>
        public int OverlayDrops;
    }

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int jl_find_port(StringBuilder buf, int cch);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int jl_open(string? port);

    [DllImport(Dll)]
    public static extern void jl_close();

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int jl_get_info(StringBuilder buf, int cch);

    [DllImport(Dll)]
    public static extern int jl_set_brightness(int percent);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int jl_show_image(string path, ref JlRenderOpts opts);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int jl_play_video(string path, ref JlRenderOpts opts);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int jl_calibrate(string path, ref JlRenderOpts opts);

    [DllImport(Dll)]
    public static extern void jl_stop();

    /// <summary>
    /// Moves the playing video to <paramref name="seconds"/>, taking effect at
    /// the next frame. Ignored when nothing is playing.
    /// </summary>
    [DllImport(Dll)]
    public static extern void jl_seek(double seconds);

    [DllImport(Dll)]
    public static extern void jl_get_status(out JlStatus status);

    [DllImport(Dll)]
    public static extern int jl_get_last_frame(byte[]? buf, int cap);

    [DllImport(Dll)]
    public static extern void jl_set_preprocess(int mode);

    [DllImport(Dll)]
    public static extern int jl_get_preprocess();

    [DllImport(Dll)]
    public static extern void jl_set_pack_budgets(long memoryBytes, long diskBytes);

    [DllImport(Dll)]
    public static extern long jl_memory_budget();

    [DllImport(Dll)]
    public static extern long jl_disk_budget();

    [DllImport(Dll)]
    public static extern long jl_pack_cache_bytes();

    [DllImport(Dll)]
    public static extern void jl_pack_cache_clear();

    /// <summary>
    /// Turns compositing on or off. Off costs nothing — frames reach the panel
    /// exactly as they did before the overlay existed. Takes effect on the next
    /// frame, including in the middle of a playing video.
    /// </summary>
    [DllImport(Dll)]
    public static extern void jl_overlay_set_enabled(int on);

    /// <summary>
    /// Replaces the overlay surface: 960×480 BGRA with <b>premultiplied</b>
    /// alpha, which is exactly what WPF's Pbgra32 produces. The pixels are
    /// copied, so the buffer can be reused as soon as this returns.
    /// </summary>
    [DllImport(Dll)]
    public static extern int jl_overlay_update(byte[] bgraPremultiplied, int w, int h);

    /// <summary>Drops the surface, leaving nothing drawn on top.</summary>
    [DllImport(Dll)]
    public static extern void jl_overlay_clear();

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern int jl_ffmpeg_path(StringBuilder buf, int cch);

    /// <summary>
    /// Throws if the managed structs no longer match the native header. Cheap,
    /// and it turns a whole class of silent memory corruption into one message.
    /// </summary>
    public static void VerifyLayout()
    {
        int opts = Marshal.SizeOf<JlRenderOpts>();
        int status = Marshal.SizeOf<JlStatus>();

        // 1128 originally; 1144 once JlStatus gained the two playback position
        // doubles; 1168 now it carries the four overlay diagnostics.
        if (opts != 88 || status != 1168)
        {
            throw new InvalidOperationException(
                $"JLDisplayNative.dll interop layout mismatch: " +
                $"JlRenderOpts={opts} (expected 88), JlStatus={status} (expected 1168). " +
                "Interop/NativeMethods.cs and jl_api.h have drifted apart.");
        }
    }
}
