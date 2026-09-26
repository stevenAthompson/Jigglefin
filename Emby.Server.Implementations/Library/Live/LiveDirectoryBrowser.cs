using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>
/// Lists the filesystem as it exists now, without consulting or populating a catalog.
/// The caller must authorize the configured root before invoking this service.
/// </summary>
public sealed class LiveDirectoryBrowser
{
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly ILiveDirectoryReader _reader;

    /// <summary>Initializes a new instance of the <see cref="LiveDirectoryBrowser"/> class.</summary>
    /// <param name="reader">The read-only filesystem adapter.</param>
    public LiveDirectoryBrowser(ILiveDirectoryReader reader)
    {
        _reader = reader;
    }

    /// <summary>Validates a root's own attributes without reading its directory contents.</summary>
    /// <param name="name">The configured display name.</param>
    /// <param name="path">The fully qualified root path.</param>
    /// <returns>A stable root descriptor.</returns>
    public LiveMediaRoot Mount(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A media root must have a fully qualified path.", nameof(path));
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RequireDirectory(_reader.Stat(fullPath));
        return new LiveMediaRoot(CreateId("root", CanonicalIdentityPath(fullPath)), name, fullPath);
    }

    /// <summary>Reads one entry's current attributes, without loading metadata.</summary>
    /// <param name="root">The authorized configured root.</param>
    /// <param name="relativePath">The relative path, or empty for the root itself.</param>
    /// <returns>The current entry.</returns>
    public LiveDirectoryEntry GetEntry(LiveMediaRoot root, string relativePath)
    {
        var fullPath = ResolveWithinRoot(root, relativePath);
        var info = StatWithoutFollowingLinks(root, fullPath);
        return Describe(root, info);
    }

    /// <summary>Reads only the selected directory, never its descendants.</summary>
    /// <param name="root">The authorized configured root.</param>
    /// <param name="relativePath">The relative directory path, or empty for the root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The immediate filesystem entries, with no cached membership.</returns>
    public IReadOnlyList<LiveDirectoryEntry> Browse(LiveMediaRoot root, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolveWithinRoot(root, relativePath);
        RequireDirectory(StatWithoutFollowingLinks(root, fullPath));
        var entries = new List<LiveDirectoryEntry>();
        foreach (var info in _reader.EnumerateDirectory(fullPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetDirectoryName(info.FullPath), fullPath, _pathComparison))
            {
                throw new IOException("The directory reader returned an entry outside the requested directory.");
            }

            entries.Add(Describe(root, info));
        }

        return entries;
    }

    private static string ResolveWithinRoot(LiveMediaRoot root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A media entry must use a relative path.", nameof(relativePath));
        }

        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || (OperatingSystem.IsWindows() && (segment.EndsWith(' ') || segment.EndsWith('.'))))
            {
                throw new ArgumentException("The media entry path contains an unsafe segment.", nameof(relativePath));
            }
        }

        var fullPath = Path.GetFullPath(Path.Combine(root.FullPath, relativePath));
        var rootPrefix = Path.EndsInDirectorySeparator(root.FullPath) ? root.FullPath : root.FullPath + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, root.FullPath, _pathComparison) && !fullPath.StartsWith(rootPrefix, _pathComparison))
        {
            throw new ArgumentException("The media entry path escapes its root.", nameof(relativePath));
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private LiveFileInfo StatWithoutFollowingLinks(LiveMediaRoot root, string fullPath)
    {
        var info = _reader.Stat(root.FullPath);
        RequireDirectory(info);
        var relative = Path.GetRelativePath(root.FullPath, fullPath);
        if (relative == ".")
        {
            return info;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar);
        var current = root.FullPath;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            info = _reader.Stat(current);
            if (info.IsLink)
            {
                throw new UnauthorizedAccessException("Link/reparse entries cannot be traversed or opened.");
            }

            if (index < segments.Length - 1)
            {
                RequireDirectory(info);
            }
        }

        return info;
    }

    private static void RequireDirectory(LiveFileInfo info)
    {
        if (info.IsLink)
        {
            throw new UnauthorizedAccessException("A live directory cannot be a link/reparse point.");
        }

        if (!info.IsDirectory)
        {
            throw new IOException("The requested path is not a directory.");
        }
    }

    private static LiveDirectoryEntry Describe(LiveMediaRoot root, LiveFileInfo info)
    {
        var relativePath = Path.GetRelativePath(root.FullPath, info.FullPath);
        if (relativePath == ".")
        {
            return new LiveDirectoryEntry(root.Id, root.Id, null, root.Name, string.Empty, info);
        }

        var parent = Path.GetDirectoryName(relativePath);
        var parentId = string.IsNullOrEmpty(parent) ? root.Id : EntryId(root, parent);
        return new LiveDirectoryEntry(EntryId(root, relativePath), root.Id, parentId, Path.GetFileName(info.FullPath), relativePath, info);
    }

    private static Guid EntryId(LiveMediaRoot root, string relativePath)
        => CreateId(root.Id.ToString("N"), CanonicalIdentityPath(relativePath));

    private static string CanonicalIdentityPath(string path)
        => OperatingSystem.IsWindows() ? path.Replace('\\', '/').ToUpperInvariant() : path;

    private static Guid CreateId(string scope, string path)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("jigglefin/live/v1\0" + scope + "\0" + path));
        return new Guid(digest.AsSpan(0, 16));
    }
}
