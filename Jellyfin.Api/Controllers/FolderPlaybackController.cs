using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.LiveFolderDtos;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Personal folder-player operations. No media mutation or recursive discovery.</summary>
[Authorize]
[Route("Jigglefin")]
public sealed class FolderPlaybackController : BaseJellyfinApiController
{
    private readonly ILiveLibrary _library;
    private readonly ILiveUserDataStore _state;
    private readonly IUserManager _users;

    /// <summary>Initializes a new instance of the <see cref="FolderPlaybackController"/> class.</summary>
    /// <param name="library">Live folder addresses.</param>
    /// <param name="state">Personal bookmarks.</param>
    /// <param name="users">Authenticated accounts.</param>
    public FolderPlaybackController(ILiveLibrary library, ILiveUserDataStore state, IUserManager users)
    {
        _library = library;
        _state = state;
        _users = users;
    }

    /// <summary>Hides personal Continue shortcuts while preserving bookmarks and favorites.</summary>
    /// <param name="itemId">One saved item, or null for all saved items.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Continue")]
    public ActionResult DismissContinue([FromQuery] Guid? itemId = null)
    {
        var userId = User.GetUserId();
        if (userId == Guid.Empty)
        {
            return NotFound();
        }

        foreach (var saved in _state.GetSaved(userId).Where(saved => !itemId.HasValue || saved.ItemId == itemId.Value))
        {
            saved.Data.HideFromResume = true;
            _state.Save(userId, saved.ItemId, saved.Data);
        }

        return NoContent();
    }

    /// <summary>Resolves an explicitly selected local playlist, not a catalog playlist.</summary>
    /// <param name="id">The encountered playlist file.</param>
    /// <returns>Playable local references and the number of ignored entries.</returns>
    [HttpGet("Playlists/{id}")]
    public ActionResult<LocalPlaylistDto> ReadPlaylist([FromRoute] Guid id)
    {
        var userId = User.GetUserId();
        if (userId == Guid.Empty)
        {
            return NotFound();
        }

        var user = _users.GetUserById(userId);
        var library = _library.FindLibrary(id);
        if (user is null || library is null || !LiveLibraryAccess.CanAccess(user, library))
        {
            return NotFound();
        }

        try
        {
            var playlist = _library.GetEntry(id);
            if (playlist is null)
            {
                return NotFound();
            }

            var result = new LocalPlaylistDto { Name = playlist.Name };
            var directories = new Dictionary<Guid, IReadOnlyList<LiveDirectoryEntry>>();
            foreach (var path in LivePlaylistReader.Read(playlist))
            {
                HttpContext.RequestAborted.ThrowIfCancellationRequested();
                var normalized = path.Replace('\\', '/');
                var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
                // Never resolve URLs, absolute/device paths or parent escapes.
                if (normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal) || parts.Length is 0 or > 16
                    || parts.Any(part => part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    result.IgnoredEntries++;
                    continue;
                }

                var parent = playlist.ParentId ?? playlist.RootId;
                LiveDirectoryEntry? found = null;
                foreach (var part in parts)
                {
                    if (found is not null && !found.File.IsDirectory)
                    {
                        found = null;
                        break;
                    }

                    if (!directories.TryGetValue(parent, out var children))
                    {
                        if (directories.Count >= 128)
                        {
                            found = null;
                            break;
                        }

                        try
                        {
                            children = _library.Browse(parent, HttpContext.RequestAborted);
                        }
                        catch (IOException)
                        {
                            children = [];
                        }
                        catch (UnauthorizedAccessException)
                        {
                            children = [];
                        }

                        directories.Add(parent, children);
                    }

                    found = children.FirstOrDefault(child => string.Equals(child.Name, part, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                    if (found is null || found.File.IsLink || found.IsUnavailable)
                    {
                        found = null;
                        break;
                    }

                    parent = found.Id;
                }

                var media = found is null ? MediaType.Unknown : LiveMediaClassifier.Classify(found.Name).MediaType;
                if (found is null || found.File.IsDirectory || media is not (MediaType.Audio or MediaType.Video))
                {
                    result.IgnoredEntries++;
                    continue;
                }

                result.Items.Add(new BaseItemDto { Id = found.Id, Name = found.Name, ParentId = found.ParentId, MediaType = media, IsFolder = false });
            }

            return result;
        }
        catch (ArgumentException error)
        {
            return BadRequest(error.Message);
        }
        catch (IOException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }
}
