using System;
using System.Text.Json.Serialization;

namespace MediaBrowser.Controller.Library;

/// <summary>Basic attributes obtained without reading a file's contents.</summary>
/// <param name="FullPath">The absolute path.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="IsLink">Whether the entry is a link/reparse point, which must not be followed.</param>
/// <param name="Length">The file length, or null for directories and links.</param>
/// <param name="LastWriteTimeUtc">The entry's modification time.</param>
public sealed record LiveFileInfo(string FullPath, bool IsDirectory, bool IsLink, long? Length, DateTime LastWriteTimeUtc)
{
    /// <summary>Gets the resolved read address; FullPath remains the logical identity/display address.</summary>
    [JsonIgnore]
    public string? ReadPath { get; init; }
}
