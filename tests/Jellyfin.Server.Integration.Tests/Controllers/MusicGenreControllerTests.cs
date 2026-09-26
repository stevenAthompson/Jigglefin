using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class MusicGenreControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private static string? _accessToken;

    public MusicGenreControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MusicGenres_CatalogUnavailable_ReturnsFolderNotFakeGenre()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.GetAsync("MusicGenres/Fake-MusicGenre", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var folder = await response.Content.ReadFromJsonAsync<BaseItemDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(folder);
        Assert.Equal("Use Folder View", folder.Name);
        Assert.Equal(BaseItemKind.Folder, folder.Type);
        Assert.True(folder.IsFolder);
    }
}
