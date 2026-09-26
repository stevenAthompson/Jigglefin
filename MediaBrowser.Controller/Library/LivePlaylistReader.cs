using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MediaBrowser.Controller.Library;

/// <summary>Bounded, local-only playlist parsing; this never opens the listed media.</summary>
public static class LivePlaylistReader
{
    /// <summary>Gets whether this filename is a supported local playlist.</summary>
    /// <param name="path">The filename.</param>
    /// <returns>Whether the extension is M3U, M3U8 or PLS.</returns>
    public static bool IsPlaylist(string path) => Path.GetExtension(path).ToLowerInvariant() is ".m3u" or ".m3u8" or ".pls";

    /// <summary>Reads only this selected playlist, through a pinned read lease.</summary>
    /// <param name="entry">An authorized, revalidated playlist.</param>
    /// <returns>Relative entries, in playlist order, including duplicate tracks.</returns>
    public static IReadOnlyList<string> Read(LiveDirectoryEntry entry)
    {
        if (entry.File.IsDirectory || entry.File.IsLink || !IsPlaylist(entry.Name))
        {
            throw new ArgumentException("Select a local M3U, M3U8 or PLS file.");
        }

        const int maximumBytes = 1024 * 1024;
        using var stream = LivePathLease.OpenRead(entry.File.ReadPath ?? entry.File.FullPath);
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > maximumBytes)
        {
            throw new ArgumentException("Playlists are limited to 1 MiB.");
        }

        var lines = Encoding.UTF8.GetString(bytes, 0, count).TrimStart('\uFEFF').Split('\n').Select(line => line.Trim());
        IEnumerable<string> paths;
        if (Path.GetExtension(entry.Name).Equals(".pls", StringComparison.OrdinalIgnoreCase))
        {
            paths = lines.Select(line => line.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0].StartsWith("File", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(pair[0].AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .OrderBy(pair => int.Parse(pair[0].AsSpan(4), CultureInfo.InvariantCulture)).Select(pair => pair[1].Trim());
        }
        else
        {
            paths = lines.Where(line => line.Length > 0 && !line.StartsWith('#'));
        }

        var result = paths.Take(2001).ToArray();
        return result.Length <= 2000 ? result : throw new ArgumentException("Playlists are limited to 2,000 entries.");
    }
}
