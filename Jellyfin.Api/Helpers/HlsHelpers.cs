using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.StreamingDtos;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// The hls helpers.
/// </summary>
public static class HlsHelpers
{
    /// <summary>Checks that a segment belongs to one exact server-generated output prefix.</summary>
    /// <param name="playlistId">The server's output hash.</param>
    /// <param name="segmentId">The filename without extension.</param>
    /// <returns>Whether the segment is a numbered member of that playlist.</returns>
    public static bool IsLiveSegmentName(string playlistId, string segmentId)
        => Guid.TryParseExact(playlistId, "N", out _) && segmentId.StartsWith(playlistId, StringComparison.Ordinal)
            && int.TryParse(segmentId.AsSpan(playlistId.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index) && index >= -1;

    /// <summary>Emits only local, authenticated segment URLs for native live-playlist consumers.</summary>
    /// <param name="text">The server-generated playlist.</param>
    /// <param name="playlistId">The server's exact output hash.</param>
    /// <param name="token">The requesting client's token.</param>
    /// <param name="nested">Whether the playlist already resides in its legacy segment route.</param>
    /// <returns>A playlist whose segments and initialization asset carry the same authentication.</returns>
    public static string AuthenticateLivePlaylist(string text, string playlistId, string token, bool nested = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        string Asset(string input)
        {
            var filename = input.Replace('\\', '/').Split('/')[^1];
            if (!IsLiveSegmentName(playlistId, Path.GetFileNameWithoutExtension(filename))
                || Path.GetExtension(filename) is not (".ts" or ".mp4" or ".m4s" or ".aac" or ".mp3"))
            {
                throw new InvalidDataException("The generated playlist contains an unexpected segment.");
            }

            return (nested ? string.Empty : "hls/" + playlistId + "/") + filename + "?ApiKey=" + Uri.EscapeDataString(token);
        }

        return string.Join('\n', text.Split('\n').Select(line =>
        {
            line = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                return line;
            }

            if (!line.StartsWith('#'))
            {
                return Asset(line);
            }

            var uri = line.IndexOf("URI=\"", StringComparison.Ordinal);
            if (uri < 0)
            {
                return line;
            }

            var end = line.IndexOf('"', uri + 5);
            if (!line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) || end < 0)
            {
                throw new InvalidDataException("The generated playlist contains an unsupported asset reference.");
            }

            return line[..(uri + 5)] + Asset(line[(uri + 5)..end]) + line[end..];
        }));
    }

    /// <summary>
    /// Waits for a minimum number of segments to be available.
    /// </summary>
    /// <param name="playlist">The playlist string.</param>
    /// <param name="segmentCount">The segment count.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
    /// <returns>A <see cref="Task"/> indicating the waiting process.</returns>
    public static async Task WaitForMinimumSegmentCount(string playlist, int? segmentCount, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogDebug("Waiting for {0} segments in {1}", segmentCount, playlist);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Need to use FileShare.ReadWrite because we're reading the file at the same time it's being written
                var fileStream = new FileStream(
                    playlist,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    IODefaults.FileStreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (fileStream.ConfigureAwait(false))
                {
                    using var reader = new StreamReader(fileStream);
                    var count = 0;

                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                    {
                        if (line.Contains("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                            if (count >= segmentCount)
                            {
                                logger.LogDebug("Finished waiting for {0} segments in {1}", segmentCount, playlist);
                                return;
                            }
                        }
                    }
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // May get an error if the file is locked
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the #EXT-X-MAP string.
    /// </summary>
    /// <param name="outputPath">The output path of the file.</param>
    /// <param name="state">The <see cref="StreamState"/>.</param>
    /// <param name="isOsDepends">Get a normal string or depends on OS.</param>
    /// <returns>The string text of #EXT-X-MAP.</returns>
    public static string GetFmp4InitFileName(string outputPath, StreamState state, bool isOsDepends)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
        var outputFileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        var outputPrefix = Path.Combine(directory, outputFileNameWithoutExtension);
        var outputExtension = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer);

        // on Linux/Unix
        // #EXT-X-MAP:URI="prefix-1.mp4"
        var fmp4InitFileName = outputFileNameWithoutExtension + "-1" + outputExtension;
        if (!isOsDepends)
        {
            return fmp4InitFileName;
        }

        if (OperatingSystem.IsWindows())
        {
            // on Windows
            // #EXT-X-MAP:URI="X:\transcodes\prefix-1.mp4"
            fmp4InitFileName = outputPrefix + "-1" + outputExtension;
        }

        return fmp4InitFileName;
    }

    /// <summary>
    /// Gets the hls playlist text.
    /// </summary>
    /// <param name="path">The path to the playlist file.</param>
    /// <param name="state">The <see cref="StreamState"/>.</param>
    /// <returns>The playlist text as a string.</returns>
    public static string GetLivePlaylistText(string path, StreamState state)
    {
        var text = File.ReadAllText(path);

        var segmentFormat = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer).TrimStart('.');
        if (string.Equals(segmentFormat, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            var fmp4InitFileName = GetFmp4InitFileName(path, state, true);
            var baseUrlParam = string.Format(
                CultureInfo.InvariantCulture,
                "hls/{0}/",
                Path.GetFileNameWithoutExtension(path));
            var newFmp4InitFileName = baseUrlParam + GetFmp4InitFileName(path, state, false);

            // Replace fMP4 init file URI.
            text = text.Replace(fmp4InitFileName, newFmp4InitFileName, StringComparison.InvariantCulture);
        }

        return text;
    }
}
