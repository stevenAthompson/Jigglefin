using System.IO;
using Jellyfin.Data.Enums;

namespace MediaBrowser.Controller.Library;

/// <summary>Extension-only presentation; never opens files or treats playlists/URLs as media.</summary>
public static class LiveMediaClassifier
{
    /// <summary>Classifies a filename without probing or loading metadata.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The compatible item kind and media type.</returns>
    public static (BaseItemKind Kind, MediaType MediaType) Classify(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".m4b" or ".aa" or ".aax" => (BaseItemKind.AudioBook, MediaType.Audio),
            ".mp3" or ".m4a" or ".aac" or ".flac" or ".wav" or ".wave" or ".ogg" or ".oga" or ".opus" or ".wma" or ".aif" or ".aiff" or ".alac" or ".ape" or ".mka" or ".ac3" or ".dts" or ".dsf" => (BaseItemKind.Audio, MediaType.Audio),
            ".mkv" or ".mp4" or ".m4v" or ".avi" or ".mov" or ".webm" or ".wmv" or ".mpg" or ".mpeg" or ".ts" or ".m2ts" or ".mts" or ".vob" or ".ogv" or ".flv" or ".3gp" => (BaseItemKind.Video, MediaType.Video),
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" => (BaseItemKind.Photo, MediaType.Photo),
            ".epub" or ".pdf" or ".mobi" or ".cbz" or ".cbr" => (BaseItemKind.Book, MediaType.Book),
            // Jellyfin has no generic File DTO kind. These remain visible but non-playable.
            _ => (BaseItemKind.Book, MediaType.Unknown)
        };
    }
}
