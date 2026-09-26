using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>Durable user state, independent of all disposable metadata and probe caches.</summary>
public interface ILiveUserDataStore
{
    /// <summary>Reads progress for one user and stable file ID.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The file.</param>
    /// <returns>The saved data or a new empty value.</returns>
    UserItemData Get(Guid userId, Guid itemId);

    /// <summary>Commits progress synchronously before acknowledging a playback report.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The file.</param>
    /// <param name="data">The state.</param>
    void Save(Guid userId, Guid itemId, UserItemData data);

    /// <summary>Lists saved user state by last playback, never media-directory contents.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>Stable IDs and saved state.</returns>
    IReadOnlyList<(Guid ItemId, UserItemData Data)> GetSaved(Guid userId);
}
