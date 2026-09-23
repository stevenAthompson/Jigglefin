using System;
using System.Collections.Generic;
using Jellyfin.Api.Extensions;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Api.Filters;

/// <summary>
/// Presents audiobooks as playable audio tracks to Jellyfin Android TV.
/// The Android TV client does not provide playback actions for AudioBook items.
/// This changes only outgoing DTOs; the library item remains an audiobook.
/// </summary>
public sealed class AndroidTvAudioBookCompatibilityFilter : IResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var client = context.HttpContext.User.GetClient();
        if (!string.Equals(client, "Jellyfin Android TV", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(client, "Jellyfin Android TV (debug)", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (context.Result is not ObjectResult result)
        {
            return;
        }

        switch (result.Value)
        {
            case BaseItemDto item:
                Adapt(item);
                break;
            case QueryResult<BaseItemDto> query:
                Adapt(query.Items);
                break;
            case IEnumerable<BaseItemDto> items:
                Adapt(items);
                break;
        }
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static void Adapt(IEnumerable<BaseItemDto> items)
    {
        foreach (var item in items)
        {
            Adapt(item);
        }
    }

    private static void Adapt(BaseItemDto item)
    {
        if (item.Type == BaseItemKind.AudioBook && item.IsFolder != true)
        {
            item.Type = BaseItemKind.Audio;
        }
    }
}
