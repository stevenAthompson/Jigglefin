using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Selected-item hydration only. No catalog, metadata providers, remote artwork or savers.</summary>
public sealed class LiveItemService : ILiveItemService, IDisposable
{
    private readonly ILiveLibrary _library;
    private readonly LiveDirectoryBrowser _browser;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<LiveItemService> _logger;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="LiveItemService"/> class.</summary>
    /// <param name="library">Root configuration and known addresses.</param>
    /// <param name="browser">The read-only path validator.</param>
    /// <param name="encoder">The upstream media prober.</param>
    /// <param name="logger">The logger.</param>
    public LiveItemService(ILiveLibrary library, LiveDirectoryBrowser browser, IMediaEncoder encoder, ILogger<LiveItemService> logger)
    {
        _library = library;
        _browser = browser;
        _encoder = encoder;
        _logger = logger;
    }

    /// <inheritdoc />
    public LiveLibraryDefinition? FindLibrary(Guid id) => _library.FindLibrary(id);

    /// <inheritdoc />
    public BaseItem? Resolve(Guid id, User? user = null)
    {
        var library = _library.FindLibrary(id);
        if (library is null || !LiveLibraryAccess.CanAccess(user, library))
        {
            return null;
        }

        if (library.Id.Equals(id))
        {
            return new Folder { Id = id, Name = library.Name, LiveContext = new LiveItemContext(library, null) };
        }

        LiveDirectoryEntry? entry;
        try
        {
            entry = _library.GetEntry(id);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (entry is null || entry.File.IsLink)
        {
            return null;
        }

        var kind = LiveMediaClassifier.Classify(entry.Name).Kind;
        BaseItem item = entry.File.IsDirectory ? new Folder() : kind switch
        {
            BaseItemKind.AudioBook => new AudioBook(),
            BaseItemKind.Audio => new Audio(),
            BaseItemKind.Video => new Video(),
            BaseItemKind.Photo => new Photo(),
            _ => new Book()
        };
        item.Id = entry.Id;
        item.Name = entry.Name;
        item.Path = entry.File.FullPath;
        item.DateModified = entry.File.LastWriteTimeUtc;
        item.Size = entry.File.Length;
        item.ParentId = entry.ParentId is null || (library.Roots.Count == 1 && entry.ParentId.Value.Equals(entry.RootId)) ? library.Id : entry.ParentId.Value;
        item.LiveContext = new LiveItemContext(library, entry);
        if (_cache.TryGetValue<MediaInfo>(ProbeKey(entry), out var probe) && probe is not null)
        {
            ApplyProbe(item, Clone(probe));
        }

        return item;
    }

    /// <inheritdoc />
    public void LoadLocalMetadata(BaseItem item)
    {
        var context = item.LiveContext ?? throw new ArgumentException("A live item is required.", nameof(item));
        item.Name = context.Entry?.Name ?? context.Library.Name;
        item.Overview = null;
        item.OriginalTitle = null;
        item.ProductionYear = null;
        item.Genres = [];
        item.ImageInfos = [];
        context.ImageTags.Clear();
        if (item is Audio audio)
        {
            audio.Album = null;
            audio.Artists = [];
        }

        var entry = context.Entry;
        if (entry is null)
        {
            if (context.Library.Roots.Count != 1)
            {
                return;
            }

            entry = _browser.GetEntry(context.Library.Roots[0], string.Empty);
        }

        var root = context.Library.Roots.Single(candidate => candidate.Id.Equals(entry.RootId));
        var directory = entry.File.IsDirectory ? entry.RelativePath : Path.GetDirectoryName(entry.RelativePath) ?? string.Empty;
        LoadArtwork(item, root, entry, directory);
        bool? unambiguous = null;
        foreach (var candidate in MetadataCandidates(entry, directory).DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var sidecar = _browser.GetEntry(root, candidate.Path);
                if (sidecar.File.IsDirectory || sidecar.File.Length > 1024 * 1024)
                {
                    continue;
                }

                if (candidate.Shared && !(unambiguous ??= IsUnambiguousFile(root, entry, directory)))
                {
                    continue;
                }

                var key = "nfo:" + ProbeKey(sidecar);
                if (!_cache.TryGetValue<XDocument>(key, out var document))
                {
                    using var stream = LivePathLease.OpenRead(sidecar.File.FullPath);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = 1024 * 1024
                    });
                    document = XDocument.Load(reader);
                    _cache.Set(key, document, CacheOptions());
                }

                if (ApplyMetadata(item, document!))
                {
                    return;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not read local sidecar {Sidecar}", candidate.Path);
            }
        }
    }

    /// <inheritdoc />
    public async Task<MediaSourceInfo> PreparePlayback(BaseItem item, CancellationToken cancellationToken)
    {
        if (item.LiveContext is null || item.IsFolder || item.MediaType is not (MediaType.Audio or MediaType.Video))
        {
            throw new ArgumentException("Select a playable local audio or video file.", nameof(item));
        }

        var entry = _library.GetEntry(item.Id) ?? throw new FileNotFoundException("The media file no longer exists.");
        if (entry.File.IsLink || entry.File.IsDirectory || LiveMediaClassifier.Classify(entry.Name).MediaType is not (MediaType.Audio or MediaType.Video))
        {
            throw new UnauthorizedAccessException("The selected entry is not a playable local file.");
        }

        var key = ProbeKey(entry);
        using var inputLease = LivePathLease.Acquire(entry.File.FullPath);
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_cache.TryGetValue<MediaInfo>(key, out var probe) || probe is null)
            {
                probe = await _encoder.GetMediaInfo(new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo { Path = inputLease.ReadPath, Protocol = MediaProtocol.File, VideoType = VideoType.VideoFile },
                    MediaType = item.MediaType == MediaType.Audio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                    ExtractChapters = true
                }, cancellationToken).ConfigureAwait(false);
                probe.Id = item.Id.ToString("N");
                probe.Path = entry.File.FullPath;
                probe.Protocol = MediaProtocol.File;
                probe.IsRemote = false;
                probe.Type = MediaSourceType.Default;
                probe.Size = entry.File.Length;
                probe.Name = entry.Name;
                probe.ETag = key;
                probe.RequiredHttpHeaders.Clear();
                _cache.Set(key, probe, CacheOptions());
            }

            var independent = Clone(probe);
            LoadSubtitles(item.LiveContext.Library, entry, independent, cancellationToken);
            ApplyProbe(item, independent);
            LoadLocalMetadata(item);
            return independent;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <inheritdoc />
    public void ClearCache() => _cache.Clear();

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
        _probeGate.Dispose();
    }

    private static string ProbeKey(LiveDirectoryEntry entry)
        => string.Create(CultureInfo.InvariantCulture, $"{entry.Id:N}:{entry.File.Length}:{entry.File.LastWriteTimeUtc.Ticks}");

    private static MemoryCacheEntryOptions CacheOptions()
        => new() { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) };

    private static MediaInfo Clone(MediaInfo probe)
        => JsonSerializer.Deserialize<MediaInfo>(JsonSerializer.SerializeToUtf8Bytes(probe))!;

    private static IEnumerable<(string Path, bool Shared)> MetadataCandidates(LiveDirectoryEntry entry, string directory)
    {
        if (entry.File.IsDirectory)
        {
            // These describe the selected folder itself, never replace it with media.
            foreach (var name in new[] { "folder.nfo", "movie.nfo", "tvshow.nfo", "season.nfo", "artist.nfo", "album.nfo", "book.nfo", "audiobook.nfo", "metadata.opf", "content.opf", "folder.xml", "movie.xml", "series.xml", "season.xml", "artist.xml", "album.xml", "book.xml", "audiobook.xml" })
            {
                yield return (Path.Combine(directory, name), false);
            }

            yield break;
        }

        yield return (Path.ChangeExtension(entry.RelativePath, ".nfo"), false);
        var type = LiveMediaClassifier.Classify(entry.Name).MediaType;
        if (type == MediaType.Video)
        {
            yield return (Path.Combine(directory, "movie.nfo"), true);
        }

        if (type == MediaType.Book)
        {
            yield return (Path.ChangeExtension(entry.RelativePath, ".opf"), false);
            yield return (Path.Combine(directory, "metadata.opf"), true);
            yield return (Path.Combine(directory, "content.opf"), true);
        }

        yield return (Path.ChangeExtension(entry.RelativePath, ".xml"), false);
        if (type == MediaType.Video)
        {
            yield return (Path.Combine(directory, "movie.xml"), true);
        }
        else if (type is MediaType.Book or MediaType.Audio)
        {
            if (type == MediaType.Audio)
            {
                yield return (Path.Combine(directory, "audiobook.xml"), true);
            }

            yield return (Path.Combine(directory, "book.xml"), true);
        }
    }

    private bool IsUnambiguousFile(LiveMediaRoot root, LiveDirectoryEntry selected, string directory)
    {
        var type = LiveMediaClassifier.Classify(selected.Name).MediaType;
        var candidates = _browser.Browse(root, directory).Where(entry => !entry.File.IsDirectory && !entry.File.IsLink)
            .Where(entry => type == MediaType.Video
                ? LiveMediaClassifier.Classify(entry.Name).MediaType == MediaType.Video
                : LiveMediaClassifier.Classify(entry.Name).MediaType is MediaType.Book or MediaType.Audio)
            .Take(2).ToArray();
        // Only an existing shared sidecar warrants one immediate-directory check.
        // No content reads, probes, cached membership or descendant traversal.
        return candidates.Length == 1 && candidates[0].Id.Equals(selected.Id);
    }

    private void LoadArtwork(BaseItem item, LiveMediaRoot root, LiveDirectoryEntry entry, string directory)
    {
        // Exact candidates only: no artwork search, directory walk, URL resolution or image writes.
        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(entry.Name));
        string[] primary = entry.File.IsDirectory
            ? [Path.Combine(directory, "folder"), Path.Combine(directory, "poster"), Path.Combine(directory, "cover")]
            : [stem + "-poster", stem, Path.Combine(directory, "poster"), Path.Combine(directory, "folder"), Path.Combine(directory, "cover")];
        foreach (var type in new[] { ImageType.Primary, ImageType.Backdrop })
        {
            var stems = type == ImageType.Primary ? primary : new[] { stem + "-fanart", Path.Combine(directory, "fanart"), Path.Combine(directory, "backdrop") };
            foreach (var candidate in stems.SelectMany(name => new[] { ".jpg", ".png", ".jpeg", ".webp" }.Select(extension => name + extension)))
            {
                try
                {
                    var artwork = _browser.GetEntry(root, candidate);
                    if (artwork.File.IsDirectory || artwork.File.IsLink || artwork.File.Length > 32 * 1024 * 1024)
                    {
                        continue;
                    }

                    item.ImageInfos = [.. item.ImageInfos, new ItemImageInfo { Path = artwork.File.FullPath, Type = type, DateModified = artwork.File.LastWriteTimeUtc }];
                    item.LiveContext.ImageTags[type] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ProbeKey(artwork))));
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Missing/inaccessible/link sidecars do not hide the selected media.
                }
            }
        }
    }

    private void LoadSubtitles(LiveLibraryDefinition library, LiveDirectoryEntry entry, MediaInfo probe, CancellationToken cancellationToken)
    {
        if (LiveMediaClassifier.Classify(entry.Name).MediaType != MediaType.Video)
        {
            return;
        }

        var root = library.Roots.Single(candidate => candidate.Id.Equals(entry.RootId));
        var directory = Path.GetDirectoryName(entry.RelativePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(entry.Name);
        var index = probe.MediaStreams.Select(stream => stream.Index).DefaultIfEmpty(-1).Max() + 1;
        // Only the selected video's immediate siblings, and no content reads/probes.
        // This is intentionally refreshed even when the media probe is cached.
        foreach (var sibling in _browser.Browse(root, directory, cancellationToken).OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(sibling.Name).ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(sibling.Name);
            if (sibling.File.IsDirectory || sibling.File.IsLink || sibling.File.Length > 16 * 1024 * 1024 || extension is not (".srt" or ".vtt" or ".ass" or ".ssa")
                || !(name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var parts = name.Length > stem.Length ? name[(stem.Length + 1)..].Split('.') : [];
            var language = parts.FirstOrDefault(part => part.Length is 2 or 3 && !part.Equals("sdh", StringComparison.OrdinalIgnoreCase));
            probe.MediaStreams = [.. probe.MediaStreams, new MediaStream
            {
                Index = index++,
                Type = MediaStreamType.Subtitle,
                Codec = extension[1..],
                Path = sibling.File.FullPath,
                IsExternal = true,
                IsForced = parts.Contains("forced", StringComparer.OrdinalIgnoreCase),
                IsDefault = parts.Contains("default", StringComparer.OrdinalIgnoreCase),
                IsHearingImpaired = parts.Contains("sdh", StringComparer.OrdinalIgnoreCase) || parts.Contains("hi", StringComparer.OrdinalIgnoreCase),
                Language = language,
                Title = sibling.Name
            }];
        }
    }

    private static void ApplyProbe(BaseItem item, MediaInfo probe)
    {
        item.RunTimeTicks = probe.RunTimeTicks;
        item.Container = probe.Container;
        item.LiveContext.Source = probe;
        item.LiveContext.Chapters = probe.Chapters;
        if (item is Video video)
        {
            video.VideoType = VideoType.VideoFile;
            video.Width = probe.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video)?.Width ?? 0;
            video.Height = probe.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video)?.Height ?? 0;
        }
    }

    private static bool ApplyMetadata(BaseItem item, XDocument document)
    {
        var root = document.Root;
        if (root is null || !new[] { "movie", "episodedetails", "tvshow", "season", "artist", "album", "musicvideo", "book", "audiobook", "item", "series", "video", "audio", "folder", "package" }.Contains(root.Name.LocalName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var fields = root.Name.LocalName.Equals("package", StringComparison.OrdinalIgnoreCase)
            ? root.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("metadata", StringComparison.OrdinalIgnoreCase))?.Elements().ToArray() ?? []
            : root.Elements().ToArray();
        string? Field(params string[] names) => fields.FirstOrDefault(element => names.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))?.Value.Trim();
        item.Name = Field("title", "LocalTitle", "name") is { Length: > 0 } title ? title : item.Name;
        item.Overview = Field("plot", "overview", "Description");
        item.OriginalTitle = Field("originaltitle");
        if (int.TryParse(Field("year", "ProductionYear"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            item.ProductionYear = year;
        }
        else if (DateTime.TryParse(Field("date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            item.ProductionYear = date.Year;
        }

        item.Genres = fields.Where(element => element.Name.LocalName.Equals("genres", StringComparison.OrdinalIgnoreCase)).SelectMany(element => element.Elements())
            .Concat(fields).Where(element => element.Name.LocalName.Equals("genre", StringComparison.OrdinalIgnoreCase) || element.Name.LocalName.Equals("subject", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (item is Audio audio)
        {
            audio.Album = Field("album");
            audio.Artists = fields.Where(element => string.Equals(element.Name.LocalName, "artist", StringComparison.OrdinalIgnoreCase)).Select(element => element.Value.Trim()).ToArray();
        }

        // IDs, URLs, trailers, scraper blocks, embedded network images and NFO playback
        // state are deliberately ignored. They never select a source or overwrite a bookmark.
        return true;
    }
}
