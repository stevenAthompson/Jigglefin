using System;

namespace MediaBrowser.Controller.Library;

/// <summary>A filesystem-backed listing entry. It contains no inferred media metadata.</summary>
/// <param name="Id">The stable path identity.</param>
/// <param name="RootId">The configured root identity.</param>
/// <param name="ParentId">The parent identity, or null for the configured root.</param>
/// <param name="Name">The filesystem name (the configured name for the root).</param>
/// <param name="RelativePath">The path relative to its configured root.</param>
/// <param name="File">The filesystem attributes.</param>
public sealed record LiveDirectoryEntry(Guid Id, Guid RootId, Guid? ParentId, string Name, string RelativePath, LiveFileInfo File);
