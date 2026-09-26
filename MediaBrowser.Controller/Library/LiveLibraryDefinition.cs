using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Library;

/// <summary>A configured folder group, not an indexed media collection.</summary>
/// <param name="Id">The stable configuration and permission identifier.</param>
/// <param name="Name">The display name.</param>
/// <param name="Roots">Explicitly configured filesystem roots.</param>
public sealed record LiveLibraryDefinition(Guid Id, string Name, IReadOnlyList<LiveMediaRoot> Roots)
{
    /// <summary>Gets whether this group is enabled. Disabled legacy groups stay inaccessible.</summary>
    public bool Enabled { get; init; } = true;
}
