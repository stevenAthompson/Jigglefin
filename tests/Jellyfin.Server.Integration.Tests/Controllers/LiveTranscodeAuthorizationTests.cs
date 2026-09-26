using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Middleware;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LiveTranscodeAuthorizationTests
{
    [Theory]
    [InlineData("ts")]
    [InlineData("mp4")]
    public async Task RealLiveHls_AuthenticatesSegmentsAndRejectsForeignSessions(string container)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG")))
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_FFMPEG for real live-HLS playback.");
        }

        using var fixture = new LiveFolderFixture(enableEncoder: true);
        var source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"), TestContext.Current.CancellationToken);
        var media = fixture.Write("Movie.mp4", source);
        fixture.Write("Another.mp4", source);
        using var readOnly = File.Open(media, FileMode.Open, FileAccess.Read, FileShare.Read);
        var errors = new Mock<ILogger<ExceptionMiddleware>>();
        await fixture.Start(services => services.AddSingleton(errors.Object));
        var group = await fixture.AddGroup();
        var item = await fixture.Navigate(group.Id, "Movie.mp4");
        var other = await fixture.Navigate(group.Id, "Another.mp4");
        using var limited = await LimitedClient(fixture, group.Id);
        using var assetClient = fixture.NewClient();
        var session = Guid.NewGuid().ToString("N");
        var url = $"Videos/{item.Id}/live.m3u8?videoCodec=h264&audioCodec=aac&allowVideoStreamCopy=false&allowAudioStreamCopy=false&segmentContainer={container}&segmentLength=1&minSegments=1&playSessionId={session}&deviceId=hls-test";
        using var response = await fixture.Client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, string.Join('\n', errors.Invocations.Select(call => string.Join(' ', call.Arguments))));
        var playlist = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var assets = lines.Where(line => !line.StartsWith('#')).ToList();
        if (container == "mp4")
        {
            assets.Add(Assert.Single(lines, line => line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal)).Split('"')[1]);
        }

        Assert.NotEmpty(assets);
        var manager = fixture.Services.GetRequiredService<ITranscodeManager>();
        var ownJob = manager.GetTranscodingJob(session);
        Assert.NotNull(ownJob);
        var foreignUser = (await AuthHelper.GetUserDtoAsync(limited)).Id;
        // Exercise the manager's locked owner selection independently of the
        // HTTP filter, including its device-only fallback.
        ownJob.IsUserPaused = true;
        manager.PingTranscodingJob(session, false, foreignUser);
        Assert.True(ownJob.IsUserPaused);
        await manager.KillTranscodingJobs("hls-test", session, _ => true, foreignUser);
        await manager.KillTranscodingJobs("hls-test", null, _ => true, foreignUser);
        Assert.Same(ownJob, manager.GetTranscodingJob(session));
        foreach (var asset in assets)
        {
            Assert.Contains("?ApiKey=", asset, StringComparison.Ordinal);
            var address = new Uri(new Uri(fixture.Client.BaseAddress!, url), asset);
            using var segment = await assetClient.GetAsync(address, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
            var bytes = await segment.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            Assert.True(bytes.Length >= 8);
            if (container == "ts")
            {
                Assert.Equal(0x47, bytes[0]);
            }

            using var anonymous = await assetClient.GetAsync(address.AbsolutePath, TestContext.Current.CancellationToken);
            Assert.Contains(anonymous.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
            using var foreign = await limited.GetAsync(address.AbsolutePath, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            using var wrongItem = await fixture.Client.GetAsync(address.AbsolutePath.Replace(item.Id.ToString(), other.Id.ToString(), StringComparison.Ordinal), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, wrongItem.StatusCode);
        }

        using var hijack = await limited.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, hijack.StatusCode);
        using var ping = await limited.PostAsync($"Sessions/Playing/Ping?playSessionId={session}", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ping.StatusCode);
        using var stopForeign = await limited.DeleteAsync($"Videos/ActiveEncodings?deviceId=hls-test&playSessionId={session}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, stopForeign.StatusCode);
        using var pingOwner = await fixture.Client.PostAsync($"Sessions/Playing/Ping?playSessionId={session}", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, pingOwner.StatusCode);
        using var stop = await fixture.Client.DeleteAsync($"Videos/ActiveEncodings?deviceId=hls-test&playSessionId={session}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        using var expired = await assetClient.GetAsync(new Uri(new Uri(fixture.Client.BaseAddress!, url), assets[0]), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        // Reusing caller-chosen session/device IDs after the previous job ended
        // must produce account-specific output, not resurrect its cached files.
        using var nextOwner = await limited.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextOwner.StatusCode);
        var nextPlaylist = await nextOwner.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var nextAsset = nextPlaylist.Split('\n', StringSplitOptions.RemoveEmptyEntries).First(line => !line.StartsWith('#'));
        Assert.NotEqual(assets[0].Split('?')[0], nextAsset.Split('?')[0]);
        using var nextSegment = await assetClient.GetAsync(new Uri(new Uri(fixture.Client.BaseAddress!, url), nextAsset), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, nextSegment.StatusCode);
        using var nextStop = await limited.DeleteAsync($"Videos/ActiveEncodings?deviceId=hls-test&playSessionId={session}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, nextStop.StatusCode);
        Assert.Equal(source, await File.ReadAllBytesAsync(media, TestContext.Current.CancellationToken));
        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task LegacyAudioCache_CannotBeReadAnonymously()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Chapter.mp3", [1, 2, 3]);
        var manager = new Mock<ITranscodeManager>();
        await fixture.Start(services =>
        {
            services.RemoveAll<ITranscodeManager>();
            services.AddSingleton(manager.Object);
        });
        var group = await fixture.AddGroup();
        var item = await fixture.Navigate(group.Id, "Chapter.mp3");
        var directory = fixture.Services.GetRequiredService<IServerConfigurationManager>().GetTranscodePath();
        Directory.CreateDirectory(directory);
        var cacheId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, cacheId + ".mp3");
        await File.WriteAllBytesAsync(path, [11, 22, 33], TestContext.Current.CancellationToken);
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Path = path, Type = TranscodingJobType.Progressive, UserId = (await AuthHelper.GetUserDtoAsync(fixture.Client)).Id, ItemId = item.Id,
            MediaSource = new MediaSourceInfo { Id = item.Id.ToString("N") }
        };
        manager.Setup(value => value.GetTranscodingJob(path, TranscodingJobType.Progressive)).Returns(job);
        manager.Setup(value => value.OnTranscodeBeginRequest(path, TranscodingJobType.Progressive)).Returns(job);
        using var owner = await fixture.Client.GetAsync($"Audio/{item.Id}/hls/{cacheId}/stream.mp3", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        using var anonymous = fixture.NewClient();
        using var response = await anonymous.GetAsync($"Audio/{item.Id}/hls/{cacheId}/stream.mp3", TestContext.Current.CancellationToken);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
    }

    [Fact]
    public async Task LegacyVideoCache_CannotBypassFolderPermissions()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Allowed/Chapter.mp3", [1, 2, 3]);
        fixture.Write("Blocked/Movie.mp4", [4, 5, 6]);
        var manager = new Mock<ITranscodeManager>();
        await fixture.Start(services =>
        {
            services.RemoveAll<ITranscodeManager>();
            services.AddSingleton(manager.Object);
        });
        var allowed = await fixture.AddGroup("Allowed", paths: [fixture.PathFor("Allowed")]);
        var blocked = await fixture.AddGroup("Blocked", paths: [fixture.PathFor("Blocked")]);
        var item = Assert.Single((await fixture.Query($"Items?parentId={blocked.Id}")).Items);
        using var limited = await LimitedClient(fixture, allowed.Id);
        var directory = fixture.Services.GetRequiredService<IServerConfigurationManager>().GetTranscodePath();
        Directory.CreateDirectory(directory);
        var cacheId = Guid.NewGuid().ToString("N");
        var playlist = Path.Combine(directory, cacheId + ".m3u8");
        await File.WriteAllTextAsync(playlist, "#EXTM3U\n" + cacheId + "0.ts\n", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, cacheId + "0.ts"), [11, 22, 33], TestContext.Current.CancellationToken);
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Path = playlist, Type = TranscodingJobType.Hls, UserId = (await AuthHelper.GetUserDtoAsync(fixture.Client)).Id, ItemId = item.Id,
            MediaSource = new MediaSourceInfo { Id = item.Id.ToString("N") }
        };
        manager.Setup(value => value.GetTranscodingJob(playlist, TranscodingJobType.Hls)).Returns(job);
        manager.Setup(value => value.OnTranscodeBeginRequest(playlist, TranscodingJobType.Hls)).Returns(job);
        var beforeStats = fixture.Reader.Stats.Count;
        foreach (var url in new[] { $"Videos/{item.Id}/hls/{cacheId}/stream.m3u8", $"Videos/{item.Id}/hls/{cacheId}/{cacheId}0.ts" })
        {
            using var owner = await fixture.Client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
            using var response = await limited.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(beforeStats, fixture.Reader.Stats.Count);
    }

    [Fact]
    public async Task CachedSession_CannotSubstituteAnotherItemsSource()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Allowed/Chapter.wav", [1, 2, 3]);
        var secret = fixture.Write("Blocked/Secret.wav", [11, 22, 33]);
        var manager = new Mock<ITranscodeManager>();
        await fixture.Start(services =>
        {
            services.RemoveAll<ITranscodeManager>();
            services.AddSingleton(manager.Object);
        });
        var allowed = await fixture.AddGroup("Allowed", paths: [fixture.PathFor("Allowed")]);
        var blocked = await fixture.AddGroup("Blocked", paths: [fixture.PathFor("Blocked")]);
        var selected = Assert.Single((await fixture.Query($"Items?parentId={allowed.Id}")).Items);
        var hidden = Assert.Single((await fixture.Query($"Items?parentId={blocked.Id}")).Items);
        using var limited = await LimitedClient(fixture, allowed.Id);
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            ItemId = hidden.Id, UserId = (await AuthHelper.GetUserDtoAsync(limited)).Id,
            MediaSource = new MediaSourceInfo
            {
                Id = hidden.Id.ToString("N"), Path = secret, Protocol = MediaProtocol.File, Container = "wav",
                MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "pcm_s16le", Channels = 1, SampleRate = 8000 }]
            }
        };
        manager.Setup(value => value.GetTranscodingJob("another-session")).Returns(job);
        using var response = await limited.GetAsync($"Audio/{selected.Id}/stream.wav?static=true&playSessionId=another-session", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<HttpClient> LimitedClient(LiveFolderFixture fixture, Guid allowedGroup)
    {
        var users = fixture.Services.GetRequiredService<IUserManager>();
        var limited = await users.CreateUserAsync("Limited");
        limited.SetPermission(PermissionKind.EnableAllFolders, false);
        limited.SetPreference(PreferenceKind.EnabledFolders, [allowedGroup]);
        await users.UpdateUserAsync(limited);
        await users.ChangePassword(limited.Id, "Temporary-test-only-Password1!");
        var client = fixture.NewClient();
        using var login = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        login.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        login.Content = JsonContent.Create(new AuthenticateUserByName { Username = limited.Username, Pw = "Temporary-test-only-Password1!" }, options: JsonDefaults.Options);
        using var response = await client.SendAsync(login, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        client.DefaultRequestHeaders.AddAuthHeader(result.RootElement.GetProperty("AccessToken").GetString()!);
        return client;
    }
}
