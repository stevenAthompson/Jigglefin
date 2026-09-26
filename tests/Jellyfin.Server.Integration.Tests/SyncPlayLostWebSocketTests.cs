using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Session;
using Jellyfin.Api.Models.SyncPlayDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests;

public sealed class SyncPlayLostWebSocketTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public SyncPlayLostWebSocketTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LostWebSocket_EndsSession_WhileUnsupportedSyncPlayCannotCreateGroups()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = _factory.CreateClient();
        var accessToken = await AuthHelper.CompleteStartupAsync(client);
        client.DefaultRequestHeaders.AddAuthHeader(accessToken);

        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request =>
            request.Headers.Authorization = AuthHelper.DummyAuthHeader + $", Token={accessToken}";

        using var webSocket = await wsClient.ConnectAsync(
            new UriBuilder(_factory.Server.BaseAddress)
            {
                Scheme = "ws",
                Path = "websocket"
            }.Uri,
            cancellationToken);

        var drain = DrainAsync(webSocket, cancellationToken);

        var watched = await WaitForWatchedWebSocketsAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var connection = Assert.Single(watched);

        using var createResponse = await client.PostAsync(
            "SyncPlay/New",
            JsonContent.Create(new NewGroupRequestDto { GroupName = "ZombieGroupRepro" }, options: JsonDefaults.Options),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        Assert.Equal(0, await WaitForGroupCountAsync(client, 0, TimeSpan.FromSeconds(10), cancellationToken));

        var sessions = _factory.Services.GetRequiredService<ISessionManager>();
        var sessionId = Assert.Single(sessions.Sessions).Id;

        connection.LastKeepAliveDate = DateTime.UtcNow - TimeSpan.FromSeconds(180);

        await drain.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
        var timeout = Stopwatch.StartNew();
        while (sessions.Sessions.Any(session => session.Id == sessionId) && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100, cancellationToken);
        }

        Assert.DoesNotContain(sessions.Sessions, session => session.Id == sessionId);
    }

    private static async Task DrainAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                await webSocket.ReceiveAsync(buffer, cancellationToken);
            }
        }
        catch
        {
            // The server tears the connection down once the watchdog gives up on it.
        }
    }

    private async Task<IReadOnlyList<IWebSocketConnection>> WaitForWatchedWebSocketsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var listener = _factory.Services.GetRequiredService<IEnumerable<IWebSocketListener>>()
            .OfType<SessionWebSocketListener>()
            .Single();
        var watchlistField = typeof(SessionWebSocketListener)
            .GetField("_webSockets", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(watchlistField);
        var watchlist = (IEnumerable<IWebSocketConnection>)watchlistField.GetValue(listener)!;

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var snapshot = watchlist.ToArray();
                if (snapshot.Length > 0 || stopwatch.Elapsed >= timeout)
                {
                    return snapshot;
                }
            }
            catch (InvalidOperationException)
            {
                // The watchdog mutated the set during enumeration; retry.
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private static async Task<int> WaitForGroupCountAsync(HttpClient client, int expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var count = -1;
        while (stopwatch.Elapsed < timeout)
        {
            using var response = await client.GetAsync("SyncPlay/List", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            count = document.RootElement.GetArrayLength();
            if (count == expected)
            {
                return count;
            }

            await Task.Delay(500, cancellationToken);
        }

        return count;
    }
}
