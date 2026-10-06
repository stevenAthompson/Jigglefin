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
    /// <param name="paths">Application cache paths.</param>
    public DvdMenuSessionManager(IApplicationPaths paths)
    {
        _cacheRoot = Path.Combine(paths.CachePath, "dvd-menus");
        _reaper = new Timer(_ => ReapIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Starts one isolated menu worker for an already authorized file.</summary>
    /// <param name="itemId">The live item ID.</param>
    /// <param name="userId">The owning user ID.</param>
    /// <param name="deviceId">The owning device ID.</param>
    /// <param name="readPath">The validated, read-only source path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ready session.</returns>
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
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
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
    /// <param name="id">The session ID.</param>
    /// <param name="userId">The requesting user ID.</param>
    /// <param name="deviceId">The requesting device ID.</param>
    /// <returns>The owned session, if active.</returns>
    public DvdMenuSession? Get(Guid id, Guid userId, string? deviceId)
    {
        if (!_sessions.TryGetValue(id, out var session) || !session.UserId.Equals(userId) || !string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
        {
            return null;
        }

        if (session.Process.HasExited)
        {
            if (_sessions.TryRemove(id, out _))
            {
                _ = session.StopAsync();
            }

            return null;
        }

        session.Touch();
        return session;
    }

    /// <summary>Stops an owned worker and removes only its private cache.</summary>
    /// <param name="id">The session ID.</param>
    /// <param name="userId">The requesting user ID.</param>
    /// <param name="deviceId">The requesting device ID.</param>
    /// <returns>Whether the session was stopped.</returns>
    public async Task<bool> StopAsync(Guid id, Guid userId, string? deviceId)
    {
        if (Get(id, userId, deviceId) is not { } session || !_sessions.TryRemove(id, out _))
        {
            return false;
        }

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
                if (errors.Length < 4000)
                {
                    errors.AppendLine(line);
                }
            }
        });
        try
        {
            string? line;
            while ((line = await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line == "READY")
                {
                    session.Ready.TrySetResult();
                }
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
