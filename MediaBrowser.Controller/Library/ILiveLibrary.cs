using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Configured roots and an opaque ID address book. Directory membership is always read live.
/// Callers must authorize the owning library before reading an item or browsing it.
/// </summary>
public interface ILiveLibrary
{
    /// <summary>Reads configuration only; never accesses media paths.</summary>
    /// <returns>The configured folder groups.</returns>
    IReadOnlyList<LiveLibraryDefinition> GetLibraries();

    /// <summary>Registers roots after checking only their own attributes.</summary>
    /// <param name="name">The group name.</param>
    /// <param name="paths">Explicit filesystem roots.</param>
    /// <param name="id">An existing configuration ID during migration, otherwise null.</param>
    /// <returns>The new configuration.</returns>
    LiveLibraryDefinition AddLibrary(string name, IReadOnlyList<string> paths, Guid? id = null);

    /// <summary>Changes a group's display name without changing identities.</summary>
    /// <param name="name">The current name.</param>
    /// <param name="newName">The new name.</param>
    void RenameLibrary(string name, string newName);

    /// <summary>Removes configuration only, never media files.</summary>
    /// <param name="name">The configured group name.</param>
    void RemoveLibrary(string name);

    /// <summary>Adds an explicitly configured path without enumerating it.</summary>
    /// <param name="name">The group name.</param>
    /// <param name="path">The additional root.</param>
    void AddPath(string name, string path);

    /// <summary>Removes a configured path without accessing it.</summary>
    /// <param name="name">The group name.</param>
    /// <param name="path">The root to remove.</param>
    void RemovePath(string name, string path);

    /// <summary>Looks up ownership without accessing media paths.</summary>
    /// <param name="itemId">A group, root, or previously encountered entry ID.</param>
    /// <returns>The owning configuration, or null if unknown or removed.</returns>
    LiveLibraryDefinition? FindLibrary(Guid itemId);

    /// <summary>Reads current attributes of one known filesystem entry.</summary>
    /// <param name="itemId">The entry ID.</param>
    /// <returns>The current entry, or null for unknown or virtual group IDs.</returns>
    LiveDirectoryEntry? GetEntry(Guid itemId);

    /// <summary>Lists only the selected directory's immediate contents.</summary>
    /// <param name="parentId">An authorized group or directory ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Current directory contents, never an address-book query.</returns>
    IReadOnlyList<LiveDirectoryEntry> Browse(Guid parentId, CancellationToken cancellationToken = default);
}
