using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Api.Helpers;

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
    /// <param name="command">The remote-control command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing command delivery.</returns>
    public async Task SendAsync(string command, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Process.HasExited)
            {
                throw new IOException("The DVD menu worker has stopped.");
            }

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
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        try
        {
            if (!Process.HasExited)
            {
                await Process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                await Process.StandardInput.FlushAsync().ConfigureAwait(false);
                try
                {
                    await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Process.Dispose();
            _lease.Dispose();
            _commands.Dispose();
            // Directory is always a random direct child of Jigglefin's cache root.
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
