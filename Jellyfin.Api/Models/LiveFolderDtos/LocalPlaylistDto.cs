using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Api.Models.LiveFolderDtos;

/// <summary>A bounded selection of local media references, never a saved catalog.</summary>
public sealed class LocalPlaylistDto
{
    /// <summary>Gets or sets the filesystem playlist name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the ordered references; duplicate tracks are intentional.</summary>
    public List<BaseItemDto> Items { get; set; } = [];

    /// <summary>Gets or sets the count of unavailable, unsupported or nonlocal entries.</summary>
    public int IgnoredEntries { get; set; }
}
