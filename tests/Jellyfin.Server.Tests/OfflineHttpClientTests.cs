using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace Jellyfin.Server.Tests;

public sealed class OfflineHttpClientTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Default")]
    [InlineData("MusicBrainz")]
    [InlineData("DirectIp")]
    [InlineData("UnexpectedFutureClient")]
    public async Task EveryFactoryClient_IsDeniedBeforeTransport(string name)
    {
        var transport = new TrapTransport();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, OfflineHttpClientFilter>();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => transport);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid/never-contact", TestContext.Current.CancellationToken));
        Assert.Contains("offline", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, transport.RequestCount);
    }

    [Fact]
    public void SynchronousRequests_AreAlsoDeniedBeforeTransport()
    {
        var transport = new TrapTransport();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, OfflineHttpClientFilter>();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => transport);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/also-denied");

        Assert.Throws<HttpRequestException>(() => client.Send(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.RequestCount);
    }

    private sealed class TrapTransport : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
