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

    /// <summary>Initializes a new instance of the <see cref="JigglefinController"/> class.</summary>
    /// <param name="items">Disposable selection cache.</param>
    public JigglefinController(ILiveItemService items)
    {
        _items = items;
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
}
