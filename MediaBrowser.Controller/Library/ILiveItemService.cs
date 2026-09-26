using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Library;

/// <summary>Projects one selected filesystem item into the upstream playback engine.</summary>
public interface ILiveItemService
{
    /// <summary>Finds ownership without touching media, for pre-access authorization.</summary>
    /// <param name="id">The item identifier.</param>
    /// <returns>The owning group, or null.</returns>
    LiveLibraryDefinition? FindLibrary(Guid id);

    /// <summary>Reads attributes and creates a temporary item; does not probe or parse sidecars.</summary>
    /// <param name="id">An encountered item or group identifier.</param>
    /// <param name="user">The user, or null for trusted internal callers.</param>
    /// <returns>The current authorized item, or null.</returns>
    BaseItem? Resolve(Guid id, User? user = null);

    /// <summary>Reads local metadata for an explicitly selected item only.</summary>
    /// <param name="item">The selected item.</param>
    void LoadLocalMetadata(BaseItem item);

    /// <summary>Probes and prepares only the selected local media file.</summary>
    /// <param name="item">The authorized selected item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An independent media source safe to customize for a client.</returns>
    Task<MediaSourceInfo> PreparePlayback(BaseItem item, CancellationToken cancellationToken);

    /// <summary>Evicts disposable metadata/probe caches, never roots or bookmarks.</summary>
    void ClearCache();
}
