using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Jigglefin.DvdMenuWorker;

internal sealed class DvdPlayer : IDisposable
{
    internal const int Width = 720;
    internal const int Height = 480;
    internal const int FrameLength = Width * Height * 4;
    internal const int AudioRate = 48000;
    internal const int AudioChannels = 2;
    internal const int AudioBytesPerSample = 2;

    private readonly byte[] _latestFrame = new byte[FrameLength];
    private readonly object _frameGate = new();
    private readonly ConcurrentQueue<byte[]> _audioChunks = new();
    private readonly IntPtr _pixels = Marshal.AllocHGlobal(FrameLength);
    private readonly VlcNative.LockFrame _lockFrame;
    private readonly VlcNative.UnlockFrame _unlockFrame;
    private readonly VlcNative.DisplayFrame _displayFrame;
    private readonly VlcNative.PlayAudio _playAudio;
    private IntPtr _instance;
    private IntPtr _media;
    private IntPtr _player;
    private byte[] _currentAudioChunk = [];
    private int _currentAudioOffset;
    private long _queuedAudioBytes;
    private long _decodedFrames;
    private bool _disposed;

    internal DvdPlayer(string vlcRoot)
    {
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(vlcRoot, "plugins"));
        Environment.SetEnvironmentVariable("DVDCSS_CACHE", "off");
        VlcNative.Load(vlcRoot);
        _lockFrame = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, _pixels);
            return IntPtr.Zero;
        };
        _unlockFrame = (_, _, _) => { };
        _displayFrame = (_, _) =>
        {
            lock (_frameGate)
            {
                Marshal.Copy(_pixels, _latestFrame, 0, FrameLength);
            }

            Interlocked.Increment(ref _decodedFrames);
        };
        _playAudio = (_, samples, count, _) =>
        {
            if (count > AudioRate * 5)
            {
                return;
            }

            var chunk = new byte[checked((int)count * AudioChannels * AudioBytesPerSample)];
            Marshal.Copy(samples, chunk, 0, chunk.Length);
            _audioChunks.Enqueue(chunk);
            Interlocked.Add(ref _queuedAudioBytes, chunk.Length);
            while (Volatile.Read(ref _queuedAudioBytes) > AudioRate * AudioChannels * AudioBytesPerSample * 2
                && _audioChunks.TryDequeue(out var dropped))
            {
                Interlocked.Add(ref _queuedAudioBytes, -dropped.Length);
            }
        };
    }

    internal int State => _player == IntPtr.Zero ? 0 : VlcNative.MediaPlayerGetState(_player);

    internal long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    internal void Start(string isoPath)
    {
        string[] options = ["--ignore-config", "--intf=dummy", "--no-metadata-network-access", "--avcodec-hw=none", "--no-video-title-show"];
        var pointers = Array.ConvertAll(options, Marshal.StringToCoTaskMemUTF8);
        try
        {
            _instance = VlcNative.New(pointers.Length, pointers);
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        if (_instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("libVLC could not initialize.");
        }

        var uri = "dvd:///" + (isoPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? isoPath : isoPath.Replace('\\', '/'));
        _media = VlcNative.MediaNewLocation(_instance, uri);
        if (_media == IntPtr.Zero)
        {
            throw new InvalidOperationException("libVLC could not open the DVD ISO.");
        }

        _player = VlcNative.MediaPlayerNewFromMedia(_media);
        if (_player == IntPtr.Zero)
        {
            throw new InvalidOperationException("libVLC could not create the DVD player.");
        }

        VlcNative.VideoSetCallbacks(_player, _lockFrame, _unlockFrame, _displayFrame, IntPtr.Zero);
        VlcNative.VideoSetFormat(_player, "RV32", Width, Height, Width * 4);
        VlcNative.AudioSetCallbacks(_player, _playAudio, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        VlcNative.AudioSetFormat(_player, "S16N", AudioRate, AudioChannels);
        if (VlcNative.MediaPlayerPlay(_player) != 0)
        {
            throw new InvalidOperationException("libVLC could not start DVD playback.");
        }
    }

    internal void CopyLatestFrame(byte[] destination)
    {
        lock (_frameGate)
        {
            Buffer.BlockCopy(_latestFrame, 0, destination, 0, FrameLength);
        }
    }

    internal void CopyAudioBlock(byte[] destination)
    {
        Array.Clear(destination);
        var cursor = 0;
        while (cursor < destination.Length)
        {
            if (_currentAudioOffset == _currentAudioChunk.Length)
            {
                if (!_audioChunks.TryDequeue(out var next))
                {
                    break;
                }

                Interlocked.Add(ref _queuedAudioBytes, -next.Length);
                _currentAudioChunk = next;
                _currentAudioOffset = 0;
            }

            var length = Math.Min(destination.Length - cursor, _currentAudioChunk.Length - _currentAudioOffset);
            Buffer.BlockCopy(_currentAudioChunk, _currentAudioOffset, destination, cursor, length);
            _currentAudioOffset += length;
            cursor += length;
        }
    }

    internal bool Command(string command)
    {
        if (_player == IntPtr.Zero)
        {
            return false;
        }

        switch (command.Trim().ToLowerInvariant())
        {
            case "select":
            case "enter":
                VlcNative.MediaPlayerNavigate(_player, 0);
                return true;
            case "up":
                VlcNative.MediaPlayerNavigate(_player, 1);
                return true;
            case "down":
                VlcNative.MediaPlayerNavigate(_player, 2);
                return true;
            case "left":
                VlcNative.MediaPlayerNavigate(_player, 3);
                return true;
            case "right":
                VlcNative.MediaPlayerNavigate(_player, 4);
                return true;
            case "menu":
                VlcNative.MediaPlayerNavigate(_player, 5);
                return true;
            case "pause":
                VlcNative.MediaPlayerSetPause(_player, 1);
                return true;
            case "resume":
                VlcNative.MediaPlayerSetPause(_player, 0);
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
        if (_player != IntPtr.Zero)
        {
            VlcNative.MediaPlayerStop(_player);
            VlcNative.MediaPlayerRelease(_player);
        }

        if (_media != IntPtr.Zero)
        {
            VlcNative.MediaRelease(_media);
        }

        if (_instance != IntPtr.Zero)
        {
            VlcNative.Release(_instance);
        }

        Marshal.FreeHGlobal(_pixels);
        GC.KeepAlive(_lockFrame);
        GC.KeepAlive(_unlockFrame);
        GC.KeepAlive(_displayFrame);
        GC.KeepAlive(_playAudio);
    }

    ~DvdPlayer() => Dispose();
}
