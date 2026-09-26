using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LiveTvControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
    private static string? _accessToken;

    public LiveTvControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AddTunerHost_Unauthorized_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var body = new TunerHostInfo()
        {
            Type = "m3u",
            Url = "Test Data/dummy.m3u8"
        };

        var response = await client.PostAsJsonAsync("/LiveTv/TunerHosts", body, _jsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddTunerHost_LocalPlaylist_IsUnavailableAndCannotBeSaved()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var body = new TunerHostInfo()
        {
            Type = "m3u",
            Url = "Test Data/dummy.m3u8"
        };

        var response = await client.PostAsJsonAsync("/LiveTv/TunerHosts", body, _jsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var options = (LiveTvOptions)_factory.Services.GetRequiredService<IServerConfigurationManager>().GetConfiguration("livetv");
        Assert.Empty(options.TunerHosts);
    }

    [Fact]
    public async Task AddTunerHost_InvalidType_IsUnavailable()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var body = new TunerHostInfo()
        {
            Type = "invalid",
            Url = "Test Data/dummy.m3u8"
        };

        var response = await client.PostAsJsonAsync("/LiveTv/TunerHosts", body, _jsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddTunerHost_InvalidUrl_IsUnavailable()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var body = new TunerHostInfo()
        {
            Type = "m3u",
            Url = "thisgoesnowhere"
        };

        var response = await client.PostAsJsonAsync("/LiveTv/TunerHosts", body, _jsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
