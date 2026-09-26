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
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return ToEntry(info);
    }

    /// <inheritdoc />
    public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
    {
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
}
