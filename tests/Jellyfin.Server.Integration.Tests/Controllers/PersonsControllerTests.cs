using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public class PersonsControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private static string? _accessToken;

    public PersonsControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetPerson_CatalogUnavailable_ReturnsNavigableFolderInsteadOfFakePerson()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        using var response = await client.GetAsync($"Persons/DoesntExist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var folder = await response.Content.ReadFromJsonAsync<BaseItemDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(folder);
        Assert.Equal("Use Folder View", folder.Name);
        Assert.Equal(BaseItemKind.Folder, folder.Type);
        Assert.True(folder.IsFolder);
        var listing = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>($"Items?parentId={folder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(listing);
        Assert.Empty(listing.Items);
    }
}
