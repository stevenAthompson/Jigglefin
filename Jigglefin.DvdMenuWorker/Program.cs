namespace Jigglefin.DvdMenuWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
        {
            await Console.Error.WriteLineAsync("Usage: Jigglefin.DvdMenuWorker <read-only DVD ISO> <private empty output directory> <ffmpeg.exe> <portable VLC directory>").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var isoPath = Path.GetFullPath(args[0]);
            var outputDirectory = Path.GetFullPath(args[1]);
            var ffmpegPath = Path.GetFullPath(args[2]);
            var vlcRoot = Path.GetFullPath(args[3]);
            if (!isoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || !File.Exists(isoPath))
            {
                throw new ArgumentException("The input must be an existing local DVD ISO.");
            }

            if (!File.Exists(ffmpegPath) || !File.Exists(Path.Combine(vlcRoot, "libvlc.dll")) || !Directory.Exists(Path.Combine(vlcRoot, "plugins")))
            {
                throw new ArgumentException("The verified FFmpeg and portable VLC dependencies are required.");
            }

            if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            {
                throw new ArgumentException("The private output directory must be empty.");
            }

            Directory.CreateDirectory(outputDirectory);
            using var player = new DvdPlayer(vlcRoot);
            player.Start(isoPath);
            using var cancellation = new CancellationTokenSource();
            var muxTask = new HlsMux(player, ffmpegPath, outputDirectory).RunAsync(cancellation.Token);
            // Console.In.ReadLineAsync may synchronously block under a redirected
            // Windows console. Keep it off the startup/health-monitor thread.
            var commandTask = Task.Run(() => ReadCommandsAsync(player), CancellationToken.None);
            var monitorTask = MonitorAsync(player, cancellation.Token);
            Console.WriteLine("STARTED");
            try
            {
                var first = await Task.WhenAny(muxTask, commandTask, monitorTask).ConfigureAwait(false);
                if (first == muxTask || first == monitorTask)
                {
                    await first.ConfigureAwait(false);
                }
            }
            finally
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await muxTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when an input command or DVD error ends the worker.
                }
            }

            return 0;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task ReadCommandsAsync(DvdPlayer player)
    {
        while (true)
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (line is null || line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("STOPPING");
                return;
            }

            Console.WriteLine(player.Command(line) ? "OK" : "UNKNOWN COMMAND");
        }
    }

    private static async Task MonitorAsync(DvdPlayer player, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            if (player.State == 7)
            {
                throw new IOException("libVLC reported a DVD playback error.");
            }

            if (player.DecodedFrames == 0 && DateTime.UtcNow - started > TimeSpan.FromSeconds(45))
            {
                throw new IOException("No DVD video was decoded within 45 seconds.");
            }
        }
    }
}
