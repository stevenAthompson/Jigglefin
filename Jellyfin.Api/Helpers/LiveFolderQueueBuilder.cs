using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Api.Models.LiveFolderDtos;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Api.Helpers;

/// <summary>Builds a bounded transient queue only after an explicit Play Folder request.</summary>
public sealed class LiveFolderQueueBuilder
{
    /// <summary>The hard queue limit, including duplicate playlist references.</summary>
    public const int MaximumItems = 500;

    private const int MaximumEntries = 10_000;
    private const int MaximumFolders = 100;
    private const int MaximumDepth = 16;
    private static readonly IComparer<string> _names = Comparer<string>.Create((left, right) => CultureInfo.CurrentCulture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));
    private readonly ILiveLibrary _library;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<Guid, IReadOnlyList<LiveDirectoryEntry>> _directories = [];
    private readonly Dictionary<Guid, int> _depths = [];
    private readonly HashSet<Guid> _visited = [];
    private readonly HashSet<Guid> _included = [];
    private readonly FolderQueueDto _result;
    private readonly string _sort;

    /// <summary>Initializes a new instance of the <see cref="LiveFolderQueueBuilder"/> class.</summary>
    /// <param name="library">Live directories. The caller must authorize the selected library first.</param>
    /// <param name="limit">Requested queue size, capped at the server maximum.</param>
    /// <param name="sort">The folder client's selected ordering.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    public LiveFolderQueueBuilder(ILiveLibrary library, int limit, string sort, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (sort is not ("playlist" or "asc" or "desc" or "newest" or "oldest" or "largest" or "smallest" or "type"))
        {
            throw new ArgumentException("Unknown folder sort order.", nameof(sort));
        }

        _library = library;
        _cancellationToken = cancellationToken;
        _sort = sort;
        _result = new FolderQueueDto { ItemLimit = Math.Min(limit, MaximumItems) };
    }

    /// <summary>Reads only enough directories to build this explicit queue. No metadata probing or background work.</summary>
    /// <param name="id">An authorized folder or folder-group address.</param>
    /// <returns>The bounded queue and any truncation notice.</returns>
    public FolderQueueDto Build(Guid id)
    {
        _depths[id] = 0;
        Visit(id);
        return _result;
    }

    private bool Full()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_result.Items.Count < _result.ItemLimit)
        {
            return false;
        }

        _result.LimitReached = true;
        return true;
    }

    private IReadOnlyList<LiveDirectoryEntry> Browse(Guid id)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_directories.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (Full() || _result.FoldersRead >= MaximumFolders || _result.EntriesExamined >= MaximumEntries
            || !_depths.TryGetValue(id, out var depth) || depth > MaximumDepth)
        {
            _result.LimitReached = true;
            return [];
        }

        _result.FoldersRead++;
        IReadOnlyList<LiveDirectoryEntry> entries;
        try
        {
            var remaining = MaximumEntries - _result.EntriesExamined;
            entries = _library.BrowseLimited(id, remaining, _cancellationToken);
            _result.EntriesExamined += entries.Count;
            _result.LimitReached |= entries.Count >= remaining;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A failing iterator may already have read thousands of entries.
            // Those still spend the shared budget; retries cannot reset it.
            _result.EntriesExamined += (error as LiveDirectoryReadException)?.EntriesExamined ?? 0;
            _result.LimitReached |= _result.EntriesExamined >= MaximumEntries;
            _result.SkippedEntries++;
            entries = [];
        }

        _directories[id] = entries;
        foreach (var entry in entries)
        {
            if (entry.IsUnavailable)
            {
                _result.SkippedEntries++;
            }

            if (entry.File.IsDirectory)
            {
                _depths.TryAdd(entry.Id, depth + 1);
            }

            // A single-root virtual group enumerates the physical root directly.
            // Alias its parent ID so playlist resolution reuses this same read.
            if (entry.ParentId.HasValue && !entry.ParentId.Value.Equals(id))
            {
                _depths.TryAdd(entry.ParentId.Value, depth);
                _directories.TryAdd(entry.ParentId.Value, entries);
            }
        }

        return entries;
    }

    private void Visit(Guid id)
    {
        if (Full() || !_visited.Add(id))
        {
            return;
        }

        var entries = Browse(id).Where(entry => !entry.File.IsLink && !entry.IsUnavailable).ToArray();
        if (_sort == "playlist")
        {
            var playlist = entries.Where(entry => !entry.File.IsDirectory && LivePlaylistReader.IsPlaylist(entry.Name)).OrderBy(entry => entry.Name, _names).FirstOrDefault();
            if (playlist is not null)
            {
                try
                {
                    foreach (var path in LivePlaylistReader.Read(playlist))
                    {
                        if (Full())
                        {
                            return;
                        }

                        var file = LivePlaylistResolver.Resolve(playlist, path, Browse, _cancellationToken);
                        if (file is not null)
                        {
                            Add(file);
                        }
                    }
                }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    _result.SkippedEntries++;
                }
            }
        }

        var files = entries.Where(entry => !entry.File.IsDirectory && LiveMediaClassifier.Classify(entry.Name).MediaType is MediaType.Audio or MediaType.Video);
        var ordered = _sort switch
        {
            "desc" => files.OrderByDescending(entry => entry.Name, _names),
            "newest" => files.OrderByDescending(entry => entry.File.LastWriteTimeUtc).ThenBy(entry => entry.Name, _names),
            "oldest" => files.OrderBy(entry => entry.File.LastWriteTimeUtc).ThenBy(entry => entry.Name, _names),
            "largest" => files.OrderByDescending(entry => entry.File.Length).ThenBy(entry => entry.Name, _names),
            "smallest" => files.OrderBy(entry => entry.File.Length).ThenBy(entry => entry.Name, _names),
            "type" => files.OrderBy(entry => Path.GetExtension(entry.Name), _names).ThenBy(entry => entry.Name, _names),
            _ => files.OrderBy(entry => entry.Name, _names)
        };
        foreach (var file in ordered)
        {
            if (Full())
            {
                return;
            }

            if (!_included.Contains(file.Id))
            {
                Add(file);
            }
        }

        foreach (var folder in entries.Where(entry => entry.File.IsDirectory).OrderBy(entry => entry.Name, _names))
        {
            if (Full())
            {
                return;
            }

            Visit(folder.Id);
        }
    }

    private void Add(LiveDirectoryEntry file)
    {
        _included.Add(file.Id);
        _result.Items.Add(new BaseItemDto { Id = file.Id, Name = file.Name, ParentId = file.ParentId, MediaType = LiveMediaClassifier.Classify(file.Name).MediaType, IsFolder = false });
    }
}
