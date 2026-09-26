using System;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LibraryDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LibraryControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private static string? _accessToken;

    public LibraryControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("Items/{0}/Ancestors")]
    public async Task Get_NonexistentItemId_NotFound(string format)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.GetAsync(string.Format(CultureInfo.InvariantCulture, format, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Items/{0}/Collections")]
    [InlineData("Artists/{0}/Similar")]
    [InlineData("Items/{0}/Similar")]
    [InlineData("Albums/{0}/Similar")]
    [InlineData("Shows/{0}/Similar")]
    [InlineData("Movies/{0}/Similar")]
    [InlineData("Trailers/{0}/Similar")]
    public async Task CatalogRecommendations_ReturnEmptyItemsWithoutResolvingTheId(string format)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.GetAsync(string.Format(CultureInfo.InvariantCulture, format, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.Empty(items.Items);
        Assert.Equal(0, items.TotalRecordCount);
    }

    [Theory]
    [InlineData("Items/{0}/ThemeSongs")]
    [InlineData("Items/{0}/ThemeVideos")]
    public async Task AutomaticThemePlayback_HasNoMedia(string format)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        var response = await client.GetAsync(string.Format(CultureInfo.InvariantCulture, format, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var themes = await response.Content.ReadFromJsonAsync<ThemeMediaResult>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(themes);
        Assert.Empty(themes.Items);
    }

    [Fact]
    public async Task AllAutomaticThemePlayback_HasNoMedia()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        var themes = await client.GetFromJsonAsync<AllThemeMediaResult>($"Items/{Guid.NewGuid()}/ThemeMedia", JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(themes);
        Assert.Empty(themes.ThemeSongsResult.Items);
        Assert.Empty(themes.ThemeVideosResult.Items);
        Assert.Empty(themes.SoundtrackSongsResult.Items);
    }

    [Theory]
    [InlineData("Items/{0}/File")]
    [InlineData("Items/{0}/Download")]
    public async Task UnrestrictedFileDownloads_AreUnavailable(string format)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        var response = await client.GetAsync(string.Format(CultureInfo.InvariantCulture, format, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Items/{0}")]
    [InlineData("Items?ids={0}")]
    public async Task Delete_NonexistentItemId_Unauthorised(string format)
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync(string.Format(CultureInfo.InvariantCulture, format, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Items/{0}")]
    [InlineData("Items?ids={0}")]
    public async Task Delete_MediaWritesAreUnavailableEvenToAdministrators(string format)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.DeleteAsync(string.Format(CultureInfo.InvariantCulture, format, Guid.NewGuid()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_NewLibraryOptions_OffersNoMetadataOrImageProviders()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.GetAsync(
            "Libraries/AvailableOptions?libraryContentType=movies&isNewLibrary=true",
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var options = await response.Content.ReadFromJsonAsync<LibraryOptionsResultDto>(
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);

        Assert.NotNull(options);
        Assert.Empty(options.MetadataReaders);
        Assert.Empty(options.TypeOptions);
    }
}
