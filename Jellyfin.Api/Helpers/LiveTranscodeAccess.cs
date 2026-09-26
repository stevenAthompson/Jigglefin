using System;
using System.Security.Claims;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Api.Helpers;

/// <summary>Playback-session IDs and output filenames are locators, never authorization.</summary>
public static class LiveTranscodeAccess
{
    /// <summary>Checks ownership and, when supplied, the independently authorized item.</summary>
    /// <param name="job">The server-created job.</param>
    /// <param name="user">The authenticated request principal.</param>
    /// <param name="itemId">The requested item, or null for owner-only stop/ping operations.</param>
    /// <returns>Whether this request may use this exact job.</returns>
    public static bool CanUse(TranscodingJob? job, ClaimsPrincipal user, Guid? itemId = null)
    {
        if (job is null || (!user.GetIsApiKey() && (user.GetUserId() == Guid.Empty || job.UserId != user.GetUserId())))
        {
            return false;
        }

        return !itemId.HasValue || (itemId.Value != Guid.Empty && job.ItemId == itemId.Value
            && Guid.TryParse(job.MediaSource?.Id, out var sourceId) && sourceId == itemId.Value);
    }
}
