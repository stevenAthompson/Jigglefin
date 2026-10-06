using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Jigglefin.DvdMenuWorker;

internal static class VlcNative
{
    private static IntPtr _library;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr LockFrame(IntPtr opaque, IntPtr planes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void UnlockFrame(IntPtr opaque, IntPtr picture, IntPtr planes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DisplayFrame(IntPtr opaque, IntPtr picture);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void PlayAudio(IntPtr opaque, IntPtr samples, uint count, long pts);

    internal static void Load(string root)
    {
        _library = NativeLibrary.Load(Path.Combine(root, "libvlc.dll"));
        NativeLibrary.SetDllImportResolver(typeof(VlcNative).Assembly, (name, _, _) => name == "libvlc" ? _library : IntPtr.Zero);
    }

    [DllImport("libvlc", EntryPoint = "libvlc_new", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr New(int count, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] arguments);

    [DllImport("libvlc", EntryPoint = "libvlc_release", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Release(IntPtr instance);

    [DllImport("libvlc", EntryPoint = "libvlc_media_new_location", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MediaNewLocation(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string mrl);

    [DllImport("libvlc", EntryPoint = "libvlc_media_new_path", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MediaNewPath(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libvlc", EntryPoint = "libvlc_media_release", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MediaRelease(IntPtr media);

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_new_from_media", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MediaPlayerNewFromMedia(IntPtr media);

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_release", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MediaPlayerRelease(IntPtr player);

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_play", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MediaPlayerPlay(IntPtr player);

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_stop", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MediaPlayerStop(IntPtr player);

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_set_pause", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MediaPlayerSetPause(IntPtr player, int pause);

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_get_state", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MediaPlayerGetState(IntPtr player);

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_navigate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MediaPlayerNavigate(IntPtr player, uint mode);

    [DllImport("libvlc", EntryPoint = "libvlc_video_set_callbacks", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VideoSetCallbacks(IntPtr player, LockFrame locked, UnlockFrame unlocked, DisplayFrame displayed, IntPtr opaque);

    [DllImport("libvlc", EntryPoint = "libvlc_video_set_format", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VideoSetFormat(IntPtr player, [MarshalAs(UnmanagedType.LPStr)] string chroma, uint width, uint height, uint pitch);

    [DllImport("libvlc", EntryPoint = "libvlc_audio_set_callbacks", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AudioSetCallbacks(IntPtr player, PlayAudio played, IntPtr paused, IntPtr resumed, IntPtr flushed, IntPtr drained, IntPtr opaque);

    [DllImport("libvlc", EntryPoint = "libvlc_audio_set_format", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AudioSetFormat(IntPtr player, [MarshalAs(UnmanagedType.LPStr)] string format, uint rate, uint channels);
}
