using System;
using System.IO;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Small folder-only administrative operations; no media writes.</summary>
[Authorize(Policy = Policies.RequiresElevation)]
public class JigglefinController : BaseJellyfinApiController
{
    private readonly ILiveItemService _items;
    private readonly ILiveLibrary _folders;

    /// <summary>Initializes a new instance of the <see cref="JigglefinController"/> class.</summary>
    /// <param name="items">Disposable selection cache.</param>
    /// <param name="folders">Configured folder groups.</param>
    public JigglefinController(ILiveItemService items, ILiveLibrary folders)
    {
        _items = items;
        _folders = folders;
    }

    /// <summary>Clears on-demand metadata/probe caches without touching bookmarks or media.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Cache/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearSelectionCache()
    {
        _items.ClearCache();
        return NoContent();
    }

    /// <summary>Changes availability without rewriting permission IDs, bookmarks or media.</summary>
    /// <param name="id">The configured group.</param>
    /// <param name="enabled">Whether to enable the group.</param>
    /// <returns>No content, or an invalid/unknown configuration response.</returns>
    [HttpPost("Folders/{id}/Enabled")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetFolderEnabled([FromRoute] Guid id, [FromQuery] bool? enabled)
    {
        if (!enabled.HasValue)
        {
            return BadRequest("Specify enabled=true or enabled=false.");
        }

        try
        {
            _folders.SetEnabled(id, enabled.Value);
            return NoContent();
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound();
        }
    }
}
