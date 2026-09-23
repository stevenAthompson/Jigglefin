using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Filters;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Api.Tests.Filters;

public class AndroidTvAudioBookCompatibilityFilterTests
{
    [Theory]
    [InlineData("Jellyfin Android TV")]
    [InlineData("Jellyfin Android TV (debug)")]
    public void AndroidTvGetsPlayableAudioKind(string client)
    {
        var book = new BaseItemDto { Type = BaseItemKind.AudioBook, IsFolder = false };
        Apply(client, book);

        Assert.Equal(BaseItemKind.Audio, book.Type);
        Assert.False(book.IsFolder);
    }

    [Fact]
    public void AndroidTvQueryOnlyChangesPlayableAudiobooks()
    {
        var book = new BaseItemDto { Type = BaseItemKind.AudioBook, IsFolder = false };
        var folder = new BaseItemDto { Type = BaseItemKind.AudioBook, IsFolder = true };
        var movie = new BaseItemDto { Type = BaseItemKind.Movie, IsFolder = false };
        var query = new QueryResult<BaseItemDto>([book, folder, movie]);

        Apply("Jellyfin Android TV", query);

        Assert.Equal(BaseItemKind.Audio, book.Type);
        Assert.Equal(BaseItemKind.AudioBook, folder.Type);
        Assert.Equal(BaseItemKind.Movie, movie.Type);
        Assert.Equal(3, query.TotalRecordCount);
    }

    [Fact]
    public void OtherClientsKeepAudiobookKind()
    {
        var book = new BaseItemDto { Type = BaseItemKind.AudioBook, IsFolder = false };

        Apply("Jellyfin Web", book);

        Assert.Equal(BaseItemKind.AudioBook, book.Type);
    }

    private static void Apply(string client, object value)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(InternalClaimTypes.Client, client)]))
        };
        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new ObjectResult(value),
            new object());

        new AndroidTvAudioBookCompatibilityFilter().OnResultExecuting(context);
    }
}
