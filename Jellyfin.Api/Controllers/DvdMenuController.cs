using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Authenticated, on-demand DVD-Video menu navigation for the folder web UI.</summary>
[Authorize]
[Route("Jigglefin/Dvd")]
public sealed class DvdMenuController : BaseJellyfinApiController
{
    private readonly ILiveLibrary _library;
    private readonly IUserManager _users;
    private readonly DvdMenuSessionManager _sessions;

    /// <summary>Initializes a new instance of the <see cref="DvdMenuController"/> class.</summary>
    public DvdMenuController(ILiveLibrary library, IUserManager users, DvdMenuSessionManager sessions)
    {
        _library = library;
        _users = users;
        _sessions = sessions;
    }

    /// <summary>Starts a bounded, private menu playback session for one selected ISO.</summary>
    [HttpPost("{itemId:guid}/Sessions")]
    public async Task<ActionResult<object>> Start([FromRoute] Guid itemId)
    {
        var userId = User.GetUserId();
        var user = _users.GetUserById(userId);
        var library = _library.FindLibrary(itemId);
        if (userId == Guid.Empty || user is null || library is null || !LiveLibraryAccess.CanAccess(user, library)) return NotFound();
        var entry = _library.GetEntry(itemId);
        if (entry is null || entry.File.IsDirectory || entry.File.IsLink || entry.IsUnavailable || !entry.Name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return NotFound();
        try
        {
            var session = await _sessions.StartAsync(itemId, userId, User.GetDeviceId(), entry.File.ReadPath ?? entry.File.FullPath, HttpContext.RequestAborted).ConfigureAwait(false);
            return new { SessionId = session.Id, PlaylistUrl = $"Jigglefin/Dvd/{session.Id:N}/stream.m3u8" };
        }
        catch (InvalidOperationException error)
        {
            return Conflict(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return BadRequest("DVD menu playback could not start: " + error.Message);
        }
    }

    /// <summary>Returns a live playlist with authenticated segment addresses.</summary>
    [HttpGet("{sessionId:guid}/stream.m3u8")]
    public ActionResult Playlist([FromRoute] Guid sessionId)
    {
        var session = Owned(sessionId);
        if (session is null) return NotFound();
        var path = Path.Combine(session.HlsDirectory, "stream.m3u8");
        if (!System.IO.File.Exists(path)) return NotFound();
        var token = User.GetToken();
        if (string.IsNullOrEmpty(token)) return Unauthorized();
        var lines = System.IO.File.ReadAllLines(path).Select(line => line.StartsWith("segment-", StringComparison.Ordinal) && line.EndsWith(".ts", StringComparison.Ordinal)
            ? line + "?ApiKey=" + Uri.EscapeDataString(token)
            : line);
        Response.Headers.CacheControl = "no-store";
        return Content(string.Join('\n', lines) + "\n", "application/vnd.apple.mpegurl");
    }

    /// <summary>Returns one validated segment from this user's private HLS cache.</summary>
    [HttpGet("{sessionId:guid}/segment-{number:int}.ts")]
    public ActionResult Segment([FromRoute] Guid sessionId, [FromRoute] int number)
    {
        var session = Owned(sessionId);
        if (session is null || number < 0 || number > 99999) return NotFound();
        var path = Path.Combine(session.HlsDirectory, $"segment-{number:00000}.ts");
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, "video/mp2t", enableRangeProcessing: false);
    }

    /// <summary>Sends a DVD remote-control action.</summary>
    [HttpPost("{sessionId:guid}/Commands/{command}")]
    public async Task<ActionResult> Command([FromRoute] Guid sessionId, [FromRoute] string command)
    {
        var session = Owned(sessionId);
        if (session is null) return NotFound();
        if (command is not ("up" or "down" or "left" or "right" or "select" or "menu" or "pause" or "resume")) return BadRequest("Unknown DVD menu command.");
        try
        {
            await session.SendAsync(command, HttpContext.RequestAborted).ConfigureAwait(false);
            return NoContent();
        }
        catch (IOException)
        {
            return Conflict("The DVD menu worker stopped.");
        }
    }

    /// <summary>Stops playback and deletes only this session's temporary HLS cache.</summary>
    [HttpDelete("{sessionId:guid}")]
    public async Task<ActionResult> Stop([FromRoute] Guid sessionId)
        => await _sessions.StopAsync(sessionId, User.GetUserId(), User.GetDeviceId()).ConfigureAwait(false) ? NoContent() : NotFound();

    private DvdMenuSession? Owned(Guid sessionId)
    {
        var userId = User.GetUserId();
        var session = _sessions.Get(sessionId, userId, User.GetDeviceId());
        if (session is null) return null;
        var user = _users.GetUserById(userId);
        var library = _library.FindLibrary(session.ItemId);
        if (user is not null && library is not null && LiveLibraryAccess.CanAccess(user, library)) return session;
        _ = _sessions.StopAsync(sessionId, userId, User.GetDeviceId());
        return null;
    }
}
