using System;

namespace MediaBrowser.Controller.Library;

/// <summary>A configured filesystem location, not an imported media library.</summary>
/// <param name="Id">The stable root identity.</param>
/// <param name="Name">The user-visible root name.</param>
/// <param name="FullPath">The canonical absolute root path.</param>
public sealed record LiveMediaRoot(Guid Id, string Name, string FullPath);
