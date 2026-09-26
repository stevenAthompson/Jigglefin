using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Compatibility facade: live browsing needs neither watchers nor ingestion events.</summary>
public sealed class LiveLibraryMonitor : ILibraryMonitor
{
    /// <inheritdoc />
    public void Start()
    {
    }

    /// <inheritdoc />
    public void Stop()
    {
    }

    /// <inheritdoc />
    public void ReportFileSystemChangeBeginning(string path)
    {
    }

    /// <inheritdoc />
    public void ReportFileSystemChangeComplete(string path, bool refreshPath)
    {
    }

    /// <inheritdoc />
    public void ReportFileSystemChanged(string path)
    {
    }
}
