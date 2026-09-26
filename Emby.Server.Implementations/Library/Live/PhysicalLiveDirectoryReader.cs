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
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        using var lease = LivePathLease.Acquire(parent ?? path);
        var readPath = parent is null ? lease.ReadPath : Path.Combine(lease.ReadPath, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        var attributes = File.GetAttributes(readPath);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(readPath)
            : new FileInfo(readPath);
        return ToEntry(info, path);
    }

    /// <inheritdoc />
    public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
    {
        using var lease = LivePathLease.Acquire(path);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        foreach (var info in new DirectoryInfo(lease.ReadPath).EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToEntry(info, Path.Combine(path, info.Name));
        }
    }

    private static LiveFileInfo ToEntry(FileSystemInfo info, string logicalPath)
    {
        var attributes = info.Attributes;
        var directory = (attributes & FileAttributes.Directory) != 0;
        var link = (attributes & FileAttributes.ReparsePoint) != 0;
        // Never ask for the length of a link target. Listed links are visible but
        // cannot be selected/traversed by LiveDirectoryBrowser.
        long? length = !directory && !link ? ((FileInfo)info).Length : null;
        return new LiveFileInfo(logicalPath, directory, link, length, info.LastWriteTimeUtc);
    }

}
