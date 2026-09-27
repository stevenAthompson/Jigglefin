using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;

namespace MediaBrowser.Controller.Library;

/// <summary>Resolves local playlist references using a caller-owned, bounded directory reader.</summary>
public static class LivePlaylistResolver
{
    /// <summary>Resolves a single relative reference without following links, URLs or parent escapes.</summary>
    /// <param name="playlist">The selected playlist.</param>
    /// <param name="path">The reference text.</param>
    /// <param name="browse">An authorized directory reader with a shared work budget.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A playable file address or null.</returns>
    public static LiveDirectoryEntry? Resolve(LiveDirectoryEntry playlist, string path, Func<Guid, IReadOnlyList<LiveDirectoryEntry>> browse, CancellationToken cancellationToken)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
        if (normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal) || parts.Length is 0 or > 16
            || parts.Any(part => part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return null;
        }

        var parent = playlist.ParentId ?? playlist.RootId;
        LiveDirectoryEntry? found = null;
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found is not null && !found.File.IsDirectory)
            {
                return null;
            }

            found = browse(parent).FirstOrDefault(child => string.Equals(child.Name, part, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (found is null || found.File.IsLink || found.IsUnavailable)
            {
                return null;
            }

            parent = found.Id;
        }

        return found is not null && !found.File.IsDirectory && LiveMediaClassifier.Classify(found.Name).MediaType is MediaType.Audio or MediaType.Video ? found : null;
    }
}
