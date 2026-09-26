using System.Collections.Generic;
using System.Threading;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Read-only filesystem primitives for live browsing. There is deliberately no
/// recursive enumeration, file-content, metadata-provider, or write operation.
/// </summary>
public interface ILiveDirectoryReader
{
    /// <summary>Reads the basic attributes of one path without enumerating it.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The filesystem entry.</returns>
    LiveFileInfo Stat(string path);

    /// <summary>Lists only the immediate children of one directory.</summary>
    /// <param name="path">The absolute directory path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Immediate entries, including link entries without following them.</returns>
    IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken);
}
