using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The hls segment controller.
/// </summary>
[Route("")]
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
public class HlsSegmentController : BaseJellyfinApiController
{
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ITranscodeManager _transcodeManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsSegmentController"/> class.
    /// </summary>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    public HlsSegmentController(
        IServerConfigurationManager serverConfigurationManager,
        ITranscodeManager transcodeManager)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _transcodeManager = transcodeManager;
    }

    /// <summary>
    /// Gets the specified audio segment for an audio item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <response code="200">Hls audio segment returned.</response>
    /// <returns>A <see cref="FileStreamResult"/> containing the audio stream.</returns>
    [HttpGet("Audio/{itemId}/hls/{segmentId}/stream.mp3", Name = "GetHlsAudioSegmentLegacyMp3")]
    [HttpGet("Audio/{itemId}/hls/{segmentId}/stream.aac", Name = "GetHlsAudioSegmentLegacyAac")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesAudioFile]
    public ActionResult GetHlsAudioSegmentLegacy([FromRoute, Required] string itemId, [FromRoute, Required] string segmentId)
    {
        var file = ValidateTranscodePath(string.Concat(segmentId, Path.GetExtension(Request.Path.Value.AsSpan())));
        if (file is null)
        {
            return BadRequest("Invalid segment.");
        }

        var job = _transcodeManager.GetTranscodingJob(file, TranscodingJobType.Progressive)
            ?? _transcodeManager.GetTranscodingJob(file, TranscodingJobType.Hls);
        if (job is null && segmentId.Length > 32 && HlsHelpers.IsLiveSegmentName(segmentId[..32], segmentId))
        {
            var playlist = ValidateTranscodePath(segmentId[..32] + ".m3u8");
            job = playlist is null ? null : _transcodeManager.GetTranscodingJob(playlist, TranscodingJobType.Hls);
        }

        return GetFileResult(itemId, file, job);
    }

    /// <summary>
    /// Gets a hls video playlist.
    /// </summary>
    /// <param name="itemId">The video id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <response code="200">Hls video playlist returned.</response>
    /// <returns>A <see cref="FileStreamResult"/> containing the playlist.</returns>
    [HttpGet("Videos/{itemId}/hls/{playlistId}/stream.m3u8")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    public ActionResult GetHlsPlaylistLegacy([FromRoute, Required] string itemId, [FromRoute, Required] string playlistId)
    {
        var file = ValidateTranscodePath(string.Concat(playlistId, Path.GetExtension(Request.Path.Value.AsSpan())));
        if (file is null
            || !Path.GetExtension(file.AsSpan()).Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid segment.");
        }

        var job = _transcodeManager.GetTranscodingJob(file, TranscodingJobType.Hls);
        if (!CanUse(itemId, job))
        {
            return NotFound();
        }

        // Native players do not reliably forward request headers to segments.
        // Every generated asset URL carries this request's own token instead.
        return Content(HlsHelpers.AuthenticateLivePlaylist(System.IO.File.ReadAllText(file), playlistId, User.GetToken()!, nested: true), MimeTypes.GetMimeType(file));
    }

    /// <summary>
    /// Stops an active encoding.
    /// </summary>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <response code="204">Encoding stopped successfully.</response>
    /// <returns>A <see cref="NoContentResult"/> indicating success.</returns>
    [HttpDelete("Videos/ActiveEncodings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> StopEncodingProcess(
        [FromQuery, Required] string deviceId,
        [FromQuery, Required] string playSessionId)
    {
        var job = _transcodeManager.GetTranscodingJob(playSessionId);
        if (job is not null && !LiveTranscodeAccess.CanUse(job, User))
        {
            return NotFound();
        }

        await _transcodeManager.KillTranscodingJobs(deviceId, playSessionId, _ => true, User.GetIsApiKey() ? null : User.GetUserId()).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets a hls video segment.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <response code="200">Hls video segment returned.</response>
    /// <response code="404">Hls segment not found.</response>
    /// <returns>A <see cref="FileStreamResult"/> containing the video segment.</returns>
    [HttpGet("Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesVideoFile]
    public ActionResult GetHlsVideoSegmentLegacy(
        [FromRoute, Required] string itemId,
        [FromRoute, Required] string playlistId,
        [FromRoute, Required] string segmentId,
        [FromRoute, Required] string segmentContainer)
    {
        var file = ValidateTranscodePath(string.Concat(segmentId, Path.GetExtension(Request.Path.Value.AsSpan())));
        if (file is null || !HlsHelpers.IsLiveSegmentName(playlistId, segmentId)
            || segmentContainer is not ("ts" or "mp4" or "m4s" or "aac" or "mp3"))
        {
            return BadRequest("Invalid segment.");
        }

        var playlistPath = ValidateTranscodePath(playlistId + ".m3u8");
        return GetFileResult(itemId, file, playlistPath is null ? null : _transcodeManager.GetTranscodingJob(playlistPath, TranscodingJobType.Hls));
    }

    private string? ValidateTranscodePath(string filename)
    {
        if (string.IsNullOrEmpty(filename) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || filename != Path.GetFileName(filename))
        {
            return null;
        }

        var transcodePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_serverConfigurationManager.GetTranscodePath()));
        var file = Path.GetFullPath(filename, transcodePath);
        // Require a separator after the transcode path so a sibling like "<transcodePath>-evil" can't pass.
        if (!file.StartsWith(transcodePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return file;
    }

    private bool CanUse(string itemId, TranscodingJob? job)
        => Guid.TryParse(itemId, out var id) && LiveTranscodeAccess.CanUse(job, User, id);

    private ActionResult GetFileResult(string itemId, string path, TranscodingJob? job)
    {
        if (!CanUse(itemId, job))
        {
            return NotFound();
        }

        var transcodingJob = _transcodeManager.OnTranscodeBeginRequest(job!.Path!, job.Type);
        if (!ReferenceEquals(job, transcodingJob))
        {
            if (transcodingJob is not null)
            {
                _transcodeManager.OnTranscodeEndRequest(transcodingJob);
            }

            return NotFound();
        }

        Response.OnCompleted(() =>
        {
            if (transcodingJob is not null)
            {
                _transcodeManager.OnTranscodeEndRequest(transcodingJob);
            }

            return Task.CompletedTask;
        });

        return FileStreamResponseHelpers.GetStaticFileResult(path, MimeTypes.GetMimeType(path));
    }
}
