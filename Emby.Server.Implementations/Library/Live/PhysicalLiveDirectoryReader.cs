using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Reads filesystem attributes and single-directory listings without content reads.</summary>
public sealed class PhysicalLiveDirectoryReader : ILiveDirectoryReader
{
    /// <inheritdoc />
    public LiveFileInfo Stat(string path)
    {
        using var lease = LivePathLease.Acquire(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path);
        CheckAncestors(path);
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return ToEntry(info);
    }

    /// <inheritdoc />
    public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
    {
        using var lease = LivePathLease.Acquire(path);
        CheckAncestors(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Link/reparse directories cannot be enumerated.");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToEntry(info);
        }
    }

    private static LiveFileInfo ToEntry(FileSystemInfo info)
    {
        var attributes = info.Attributes;
        var directory = (attributes & FileAttributes.Directory) != 0;
        var link = (attributes & FileAttributes.ReparsePoint) != 0;
        // Never ask for the length of a link target. Listed links are visible but
        // cannot be selected/traversed by LiveDirectoryBrowser.
        long? length = !directory && !link ? ((FileInfo)info).Length : null;
        return new LiveFileInfo(info.FullName, directory, link, length, info.LastWriteTimeUtc);
    }

    private static void CheckAncestors(string path)
    {
        // A configured root can itself look ordinary while an ancestor is a
        // junction. Inspect attributes, never enumerate ancestors or descendants.
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        while (parent is not null)
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Paths beneath link/reparse directories cannot be opened.");
            }

            parent = Path.GetDirectoryName(parent);
        }
    }
}
