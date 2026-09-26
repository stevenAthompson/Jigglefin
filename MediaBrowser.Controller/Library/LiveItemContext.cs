using System.Collections.Generic;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>Transient playback context; never a catalog row or directory-membership cache.</summary>
/// <param name="Library">The owning configured folder group.</param>
/// <param name="Entry">The current physical entry, or null for a virtual group.</param>
public sealed record LiveItemContext(LiveLibraryDefinition Library, LiveDirectoryEntry? Entry)
{
    /// <summary>Gets or sets the selected file's on-demand probe result.</summary>
    public MediaSourceInfo? Source { get; set; }

    /// <summary>Gets or sets chapters from the selected file.</summary>
    public ChapterInfo[] Chapters { get; set; } = [];

    /// <summary>Gets selected local artwork cache tags; never used to enrich folder listings.</summary>
    public Dictionary<ImageType, string> ImageTags { get; } = [];
}
