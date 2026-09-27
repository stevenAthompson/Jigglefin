using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Api.Models.LiveFolderDtos;

/// <summary>A transient, explicitly requested folder queue. Never a media catalog.</summary>
public sealed class FolderQueueDto
{
    /// <summary>Gets or sets the ordered media addresses, including intentional playlist duplicates.</summary>
    public List<BaseItemDto> Items { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether any safety budget was exhausted.</summary>
    public bool LimitReached { get; set; }

    /// <summary>Gets or sets the effective maximum number of queue entries.</summary>
    public int ItemLimit { get; set; }

    /// <summary>Gets or sets the number of raw filesystem entries examined.</summary>
    public int EntriesExamined { get; set; }

    /// <summary>Gets or sets the number of attempted directory reads.</summary>
    public int FoldersRead { get; set; }

    /// <summary>Gets or sets the count of inaccessible folders or unreadable playlists skipped.</summary>
    public int SkippedEntries { get; set; }
}
