using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Database.Implementations;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LiveFolderBrowsingTests
{
    [Fact]
    public async Task AuthenticatedFolderFlow_IsLiveBoundedAndDoesNotPopulateCatalog()
    {
        var fixture = Directory.CreateTempSubdirectory("jigglefin-live-http-");
        try
        {
            var deep = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Unvisited", "Deep"));
            for (var index = 0; index < 1000; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(deep.FullName, $"{index}.mp3"), "must not be discovered", TestContext.Current.CancellationToken);
            }

            var media = Path.Combine(fixture.FullName, "Chapter.mp3");
            var sidecar = Path.Combine(fixture.FullName, "Chapter.nfo");
            await File.WriteAllTextAsync(media, "not media: browsing must not probe", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(sidecar, "not XML: browsing must not parse", TestContext.Current.CancellationToken);
            var beforeMedia = (new FileInfo(media).Length, File.GetLastWriteTimeUtc(media));
            var beforeSidecar = (new FileInfo(sidecar).Length, File.GetLastWriteTimeUtc(sidecar));
            var reader = new TrackingReader();
            using var factory = new JellyfinApplicationFactory();
            using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILiveDirectoryReader>();
                services.AddSingleton<ILiveDirectoryReader>(reader);
            }));
            using var client = configured.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
            var user = await AuthHelper.GetUserDtoAsync(client);
            var databaseFactory = configured.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
            await using var database = await databaseFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
            var initialCatalogCount = await database.BaseItems.CountAsync(TestContext.Current.CancellationToken);
            Guid libraryId;
            Guid mediaId;

            using (var mediaLock = File.Open(media, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var sidecarLock = File.Open(sidecar, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var create = await client.PostAsJsonAsync(
                    "Library/VirtualFolders?name=Live&refreshLibrary=true",
                    new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { PathInfos = [new MediaPathInfo(fixture.FullName)] } },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, create.StatusCode);
                Assert.Empty(reader.Enumerations);
                Assert.Equal(new[] { fixture.FullName }, reader.Stats);

                var views = await Query(client, "UserViews");
                libraryId = Assert.Single(views.Items).Id;
                Assert.Empty(reader.Enumerations);
                var items = await Query(client, $"Items?parentId={libraryId}&recursive=true");
                Assert.Equal(3, items.Items.Count);
                Assert.DoesNotContain(items.Items, item => item.Name == "Deep");
                Assert.Equal(new[] { fixture.FullName }, reader.Enumerations);
                Assert.DoesNotContain(deep.FullName, reader.Stats);
                mediaId = Assert.Single(items.Items, item => item.Name == "Chapter.mp3").Id;

                var legacyItems = await Query(client, $"Users/{user.Id}/Items?parentId={libraryId}");
                Assert.Equal(items.Items.Select(item => item.Id), legacyItems.Items.Select(item => item.Id));

                using var refresh = await client.PostAsync("Library/Refresh", null, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, refresh.StatusCode);
                Assert.Equal(2, reader.Enumerations.Count);
                Assert.Equal(initialCatalogCount, await database.BaseItems.CountAsync(TestContext.Current.CancellationToken));

                var previousStats = reader.Stats.Count;
                foreach (var target in new[] { $"Items/{mediaId}", $"Items/{mediaId}/Images/Primary", $"Videos/{mediaId}/Subtitles/0", $"Audio/{mediaId}/Lyrics" })
                {
                    using var denied = await client.DeleteAsync(target, TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
                }

                foreach (var target in new[] { $"Items/{mediaId}/RemoteImages/Download?imageUrl=https%3A%2F%2Fexample.invalid%2Fimage.jpg&type=Primary", "Packages/Installed/Test", "Repositories" })
                {
                    using var denied = await client.PostAsJsonAsync(target, Array.Empty<object>(), TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
                }

                using var itemRefresh = await client.PostAsync($"Items/{mediaId}/Refresh?replaceAllMetadata=true&replaceAllImages=true&recursive=true", null, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, itemRefresh.StatusCode);
                using var plugins = await client.GetAsync("Plugins", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, plugins.StatusCode);
                Assert.Equal("[]", await plugins.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                using var subtitles = await client.GetAsync($"Items/{mediaId}/RemoteSearch/Subtitles/en", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, subtitles.StatusCode);
                Assert.Equal("[]", await subtitles.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(previousStats, reader.Stats.Count);
                Assert.Equal(2, reader.Enumerations.Count);
            }

            Assert.Equal(beforeMedia, (new FileInfo(media).Length, File.GetLastWriteTimeUtc(media)));
            Assert.Equal(beforeSidecar, (new FileInfo(sidecar).Length, File.GetLastWriteTimeUtc(sidecar)));

            File.Delete(media);
            await File.WriteAllTextAsync(Path.Combine(fixture.FullName, "New.mp3"), "new file", TestContext.Current.CancellationToken);
            var changed = await Query(client, $"Items?parentId={libraryId}");
            Assert.DoesNotContain(changed.Items, item => item.Id.Equals(mediaId));
            Assert.Contains(changed.Items, item => item.Name == "New.mp3");
            using var stale = await client.GetAsync($"Items/{mediaId}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
            Assert.Equal(initialCatalogCount, await database.BaseItems.CountAsync(TestContext.Current.CancellationToken));
            Assert.All(reader.Enumerations, path => Assert.Equal(fixture.FullName, path));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    private static async Task<QueryResult<BaseItemDto>> Query(HttpClient client, string url)
        => await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(url, JsonDefaults.Options, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Missing query result.");

    private sealed class TrackingReader : ILiveDirectoryReader
    {
        private readonly PhysicalLiveDirectoryReader _physical = new();

        public List<string> Stats { get; } = [];

        public List<string> Enumerations { get; } = [];

        public LiveFileInfo Stat(string path)
        {
            Stats.Add(path);
            return _physical.Stat(path);
        }

        public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
        {
            Enumerations.Add(path);
            return _physical.EnumerateDirectory(path, cancellationToken);
        }
    }
}
