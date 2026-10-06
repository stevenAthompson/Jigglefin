using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace Jigglefin.DvdMenuWorker;

internal sealed class HlsMux
{
    private const int FrameRate = 30;
    private const int AudioFramesPerVideoFrame = DvdPlayer.AudioRate / FrameRate;
    private readonly DvdPlayer _player;
    private readonly string _ffmpegPath;
    private readonly string _outputDirectory;

    internal HlsMux(DvdPlayer player, string ffmpegPath, string outputDirectory)
    {
        _player = player;
        _ffmpegPath = ffmpegPath;
        _outputDirectory = outputDirectory;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var pipeName = "jigglefin-dvd-audio-" + Guid.NewGuid().ToString("N");
        using var audioPipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4 * 1024 * 1024);
        using var ffmpeg = new Process();
        Configure(ffmpeg.StartInfo, pipeName);
        ffmpeg.ErrorDataReceived += (_, data) =>
        {
            if (data.Data is not null)
            {
                Console.Error.WriteLine(data.Data);
            }
        };

        if (!ffmpeg.Start())
        {
            throw new InvalidOperationException("Could not start the DVD HLS encoder.");
        }

        ffmpeg.BeginErrorReadLine();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var audioConnection = audioPipe.WaitForConnectionAsync(linked.Token);
        var videoTask = WriteVideoAsync(ffmpeg.StandardInput.BaseStream, linked.Token);
        var audioTask = WriteAudioAsync(audioPipe, audioConnection, linked.Token);
        var exitTask = ffmpeg.WaitForExitAsync(CancellationToken.None);
        var readyTask = AnnounceReadyAsync(ffmpeg, linked.Token);
        try
        {
            var first = await Task.WhenAny(videoTask, audioTask, exitTask).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                await first.ConfigureAwait(false);
                throw new IOException(first == exitTask
                    ? $"DVD HLS encoder stopped unexpectedly (exit {ffmpeg.ExitCode})."
                    : "DVD HLS input stopped unexpectedly.");
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await ffmpeg.StandardInput.BaseStream.DisposeAsync().ConfigureAwait(false);
            await audioPipe.DisposeAsync().ConfigureAwait(false);
            var exit = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)).ConfigureAwait(false);
            if (exit != exitTask && !ffmpeg.HasExited)
            {
                ffmpeg.Kill(entireProcessTree: true);
            }

            await exitTask.ConfigureAwait(false);
            await IgnoreCancellation(videoTask).ConfigureAwait(false);
            await IgnoreCancellation(audioTask).ConfigureAwait(false);
            await IgnoreCancellation(readyTask).ConfigureAwait(false);
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The process and pipe are being torn down after another task ended.
        }
    }

    private async Task AnnounceReadyAsync(Process ffmpeg, CancellationToken cancellationToken)
    {
        var playlist = Path.Combine(_outputDirectory, "stream.m3u8");
        while (!cancellationToken.IsCancellationRequested && !ffmpeg.HasExited)
        {
            if (_player.DecodedFrames > 0 && File.Exists(playlist))
            {
                Console.WriteLine("READY");
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteVideoAsync(Stream destination, CancellationToken cancellationToken)
    {
        var frame = ArrayPool<byte>.Shared.Rent(DvdPlayer.FrameLength);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / FrameRate));
            while (!cancellationToken.IsCancellationRequested)
            {
                _player.CopyLatestFrame(frame);
                await destination.WriteAsync(frame.AsMemory(0, DvdPlayer.FrameLength), cancellationToken).ConfigureAwait(false);
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private async Task WriteAudioAsync(NamedPipeServerStream destination, Task connected, CancellationToken cancellationToken)
    {
        await connected.ConfigureAwait(false);
        var block = new byte[AudioFramesPerVideoFrame * DvdPlayer.AudioChannels * DvdPlayer.AudioBytesPerSample];
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / FrameRate));
        while (!cancellationToken.IsCancellationRequested)
        {
            _player.CopyAudioBlock(block);
            await destination.WriteAsync(block, cancellationToken).ConfigureAwait(false);
            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private void Configure(ProcessStartInfo start, string pipeName)
    {
        start.FileName = _ffmpegPath;
        start.WorkingDirectory = _outputDirectory;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardError = true;
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "warning",
            "-thread_queue_size", "512", "-probesize", "32", "-analyzeduration", "0", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{DvdPlayer.Width}x{DvdPlayer.Height}", "-framerate", FrameRate.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
            "-thread_queue_size", "512", "-probesize", "32", "-analyzeduration", "0", "-f", "s16le", "-ar", DvdPlayer.AudioRate.ToString(CultureInfo.InvariantCulture), "-ac", DvdPlayer.AudioChannels.ToString(CultureInfo.InvariantCulture), "-i", @"\\.\pipe\" + pipeName,
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-threads", "2", "-g", (FrameRate * 2).ToString(CultureInfo.InvariantCulture), "-keyint_min", (FrameRate * 2).ToString(CultureInfo.InvariantCulture), "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k",
            "-f", "hls", "-hls_time", "2", "-hls_list_size", "8", "-hls_delete_threshold", "4", "-hls_flags", "delete_segments+independent_segments+omit_endlist", "-hls_segment_filename", Path.Combine(_outputDirectory, "segment-%05d.ts"), Path.Combine(_outputDirectory, "stream.m3u8")
        })
        {
            start.ArgumentList.Add(argument);
        }
    }
}
