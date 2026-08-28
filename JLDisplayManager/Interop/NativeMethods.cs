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
        public int FinishedCount;
        public int FrameCount;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Message;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Error;
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

    [DllImport(Dll)]
    public static extern void jl_get_status(out JlStatus status);

    [DllImport(Dll)]
    public static extern int jl_get_last_frame(byte[]? buf, int cap);

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

        if (opts != 88 || status != 1128)
        {
            throw new InvalidOperationException(
                $"JLDisplayNative.dll interop layout mismatch: " +
                $"JlRenderOpts={opts} (expected 88), JlStatus={status} (expected 1128). " +
                "Interop/NativeMethods.cs and jl_api.h have drifted apart.");
        }
    }
}
