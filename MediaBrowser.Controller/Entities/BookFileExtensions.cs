using System;
using System.IO;
using System.Linq;
using Emby.Naming.Audio;
using Emby.Naming.Common;

namespace MediaBrowser.Controller.Entities;

/// <summary>
/// File extensions that the book resolver can turn into book items.
/// </summary>
public static class BookFileExtensions
{
    private static readonly string[] _supportedExtensions = [".azw", ".azw3", ".cb7", ".cbr", ".cbt", ".cbz", ".epub", ".mobi", ".pdf"];

    /// <summary>
    /// Checks whether a path can be resolved as a book.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>Whether the path has a supported book extension.</returns>
    public static bool IsBookFile(string path)
        => _supportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks whether a path can become a book or audiobook item in a books library.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="namingOptions">The media naming options.</param>
    /// <returns>Whether the file is book or audiobook media.</returns>
    public static bool IsBookOrAudioBookFile(string path, NamingOptions namingOptions)
        => IsBookFile(path)
            || (AudioFileParser.IsAudioFile(path, namingOptions)
                && !Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase));
}
