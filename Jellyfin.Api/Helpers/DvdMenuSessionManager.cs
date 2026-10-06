using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Api.Helpers;

/// <summary>Owns short-lived, read-only DVD menu workers and their private HLS cache.</summary>
public sealed class DvdMenuSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, DvdMenuSession> _sessions = new();
    private readonly string _cacheRoot;
    private readonly Timer _reaper;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="DvdMenuSessionManager"/> class.</summary>
    public DvdMenuSessionManager(IApplicationPaths paths)
    {
        _cacheRoot = Path.Combine(paths.CachePath, "dvd-menus");
        _reaper = new Timer(_ => ReapIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Starts one isolated menu worker for an already authorized file.</summary>
    public async Task<DvdMenuSession> StartAsync(Guid itemId, Guid userId, string? deviceId, string readPath, CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.Count >= 2)
            {
                throw new InvalidOperationException("The DVD menu player is busy. Stop another DVD menu session first.");
            }

            var baseDirectory = AppContext.BaseDirectory;
            var worker = Path.Combine(baseDirectory, "Jigglefin.DvdMenuWorker.exe");
            var ffmpeg = Path.Combine(baseDirectory, "ffmpeg.exe");
            var vlc = Path.Combine(baseDirectory, "vlc");
            if (!File.Exists(worker) || !File.Exists(ffmpeg) || !File.Exists(Path.Combine(vlc, "libvlc.dll")))
            {
                throw new FileNotFoundException("The DVD menu worker or its bundled local dependencies are missing.");
            }

            var lease = LivePathLease.AcquireReadPath(readPath);
            var id = Guid.NewGuid();
            var directory = Path.Combine(_cacheRoot, id.ToString("N"));
            Process? process = null;
            try
            {
                Directory.CreateDirectory(directory);
                // VLC's DVD parser does not understand Windows volume-GUID syntax.
                // This alias lives only in a random, server-owned cache directory;
                // the source and all its ancestors remain pinned by the lease.
                var alias = Path.Combine(directory, "disc.iso");
                File.CreateSymbolicLink(alias, lease.ReadPath);
                var start = new ProcessStartInfo(worker)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = directory
                };
                start.ArgumentList.Add(alias);
                start.ArgumentList.Add(Path.Combine(directory, "hls"));
                start.ArgumentList.Add(ffmpeg);
                start.ArgumentList.Add(vlc);
                start.Environment["PATH"] = baseDirectory + Path.PathSeparator + vlc + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                process = Process.Start(start) ?? throw new IOException("The DVD menu worker could not start.");
                var session = new DvdMenuSession(id, itemId, userId, deviceId, directory, process, lease);
                _ = Task.Run(() => DrainWorkerAsync(session), CancellationToken.None);
                _sessions[id] = session;
                try
                {
                    await session.Ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                    if (process.HasExited)
                    {
                        throw new IOException("The DVD menu worker stopped before playback was ready.");
                    }

                    return session;
                }
                catch
                {
                    await StopAsync(id, userId, deviceId).ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                if (process is null)
                {
                    lease.Dispose();
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }

                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Returns an owned session and refreshes its idle timer.</summary>
    public DvdMenuSession? Get(Guid id, Guid userId, string? deviceId)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.UserId != userId || !string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal)) return null;
        if (session.Process.HasExited)
        {
            if (_sessions.TryRemove(id, out _)) _ = session.StopAsync();
            return null;
        }

        session.Touch();
        return session;
    }

    /// <summary>Stops an owned worker and removes only its private cache.</summary>
    public async Task<bool> StopAsync(Guid id, Guid userId, string? deviceId)
    {
        if (Get(id, userId, deviceId) is not { } session || !_sessions.TryRemove(id, out _)) return false;
        await session.StopAsync().ConfigureAwait(false);
        return true;
    }

    private async Task DrainWorkerAsync(DvdMenuSession session)
    {
        var errors = new StringBuilder();
        var stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await session.Process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (errors.Length < 4000) errors.AppendLine(line);
            }
        });
        try
        {
            string? line;
            while ((line = await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line == "READY") session.Ready.TrySetResult();
            }

            await stderr.ConfigureAwait(false);
            session.Ready.TrySetException(new IOException("DVD menu playback ended before it was ready. " + errors));
        }
        catch (Exception error)
        {
            session.Ready.TrySetException(error);
        }
    }

    private void ReapIdle()
    {
        foreach (var session in _sessions.Values.Where(value => DateTime.UtcNow - value.LastAccessUtc > TimeSpan.FromMinutes(5)))
        {
            _ = StopAsync(session.Id, session.UserId, session.DeviceId);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.StopAsync().GetAwaiter().GetResult();
        }

        _startGate.Dispose();
    }
}

/// <summary>A single privately cached DVD menu stream.</summary>
public sealed class DvdMenuSession : IDisposable
{
    private readonly LivePathLease _lease;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private long _lastAccessTicks = DateTime.UtcNow.Ticks;
    private int _stopped;

    internal DvdMenuSession(Guid id, Guid itemId, Guid userId, string? deviceId, string directory, Process process, LivePathLease lease)
    {
        Id = id;
        ItemId = itemId;
        UserId = userId;
        DeviceId = deviceId;
        Directory = directory;
        Process = process;
        _lease = lease;
    }

    /// <summary>Gets the opaque session ID.</summary>
    public Guid Id { get; }

    /// <summary>Gets the selected live item ID for current-access revalidation.</summary>
    public Guid ItemId { get; }

    /// <summary>Gets the owning user ID.</summary>
    public Guid UserId { get; }

    /// <summary>Gets the owning device ID.</summary>
    public string? DeviceId { get; }

    /// <summary>Gets the private cache directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the private HLS output directory.</summary>
    public string HlsDirectory => Path.Combine(Directory, "hls");

    /// <summary>Gets the worker process.</summary>
    public Process Process { get; }

    /// <summary>Gets the time of the last authorized request.</summary>
    public DateTime LastAccessUtc => new(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc);

    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Touch() => Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);

    /// <summary>Sends one disc navigation command.</summary>
    public async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Process.HasExited) throw new IOException("The DVD menu worker has stopped.");
            await Process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await Process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            if (!Process.HasExited)
            {
                await Process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                await Process.StandardInput.FlushAsync().ConfigureAwait(false);
                try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            Process.Dispose();
            _lease.Dispose();
            _commands.Dispose();
            // Directory is always a random direct child of Jigglefin's cache root.
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
