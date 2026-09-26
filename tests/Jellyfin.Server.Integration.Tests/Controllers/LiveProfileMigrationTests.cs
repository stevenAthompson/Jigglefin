using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Updates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LiveProfileMigrationTests
{
    [Fact]
    public async Task Upgrade_PreservesConfigurationPermissionsAndSavedPlacesWithoutTouchingMedia()
    {
        var fixture = Directory.CreateTempSubdirectory("jigglefin-live-upgrade-");
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media & Books"));
            var novel = Directory.CreateDirectory(Path.Combine(media.FullName, "Novel"));
            var chapter = Path.Combine(novel.FullName, "Chapter.m4b");
            var source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"), TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(chapter, source, TestContext.Current.CancellationToken);
            var deep = Directory.CreateDirectory(Path.Combine(media.FullName, "Unvisited", "Deep"));
            for (var index = 0; index < 1000; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(deep.FullName, index + ".mp3"), "must not be read", TestContext.Current.CancellationToken);
            }

            var offlinePath = Path.Combine(fixture.FullName, "OfflineDrive");
            var disabledPath = Path.Combine(fixture.FullName, "DisabledDrive");
            var booksId = Guid.NewGuid();
            var offlineId = Guid.NewGuid();
            var disabledId = Guid.NewGuid();
            var oldChapterId = Guid.NewGuid();
            var oldOfflineItemId = Guid.NewGuid();
            var timestamp = File.GetLastWriteTimeUtc(chapter);
            var duration = TimeSpan.FromSeconds(2).Ticks;
            var bookmark = TimeSpan.FromMilliseconds(650).Ticks;
            var ffmpeg = Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG");
            Guid adminId;
            Guid limitedId;
            string token;
            string stateDirectory;
            string optionsPath;
            string originalOptions;
            using (var legacy = new JellyfinApplicationFactory { TestProfilePath = profile })
            using (var client = legacy.CreateClient())
            {
                token = await AuthHelper.CompleteStartupAsync(client);
                client.DefaultRequestHeaders.AddAuthHeader(token);
                adminId = (await AuthHelper.GetUserDtoAsync(client)).Id;
                var paths = legacy.Services.GetRequiredService<IServerApplicationPaths>();
                stateDirectory = Path.Combine(paths.DataPath, "live-folders");
                var books = await WriteGroup(paths.DefaultUserViewsPath, "Books", media.FullName, true);
                var offline = await WriteGroup(paths.DefaultUserViewsPath, "Offline", offlinePath, true);
                var disabled = await WriteGroup(paths.DefaultUserViewsPath, "Disabled", disabledPath, false);
                optionsPath = Path.Combine(books, "options.xml");
                originalOptions = await File.ReadAllTextAsync(optionsPath, TestContext.Current.CancellationToken);
                var users = legacy.Services.GetRequiredService<IUserManager>();
                var limited = await users.CreateUserAsync("Limited reader");
                limited.SetPermission(PermissionKind.EnableAllFolders, false);
                limited.SetPreference(PreferenceKind.EnabledFolders, [booksId]);
                await users.UpdateUserAsync(limited);
                limitedId = limited.Id;

                await using var database = await legacy.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
                database.BaseItems.AddRange(
                    Item(booksId, typeof(CollectionFolder).FullName!, books),
                    Item(offlineId, typeof(CollectionFolder).FullName!, offline),
                    Item(disabledId, typeof(CollectionFolder).FullName!, disabled),
                    Item(oldChapterId, "MediaBrowser.Controller.Entities.AudioBook", chapter, duration),
                    Item(oldOfflineItemId, "MediaBrowser.Controller.Entities.Audio.Audio", Path.Combine(offlinePath, "Missing.mp3"), duration),
                    Item(Guid.NewGuid(), "MediaBrowser.Controller.Playlists.Playlist", deep.FullName));
                // Existing catalog records without saved state must not become live addresses.
                for (var index = 0; index < 1000; index++)
                {
                    database.BaseItems.Add(Item(Guid.NewGuid(), "MediaBrowser.Controller.Entities.Audio.Audio", Path.Combine(deep.FullName, index + ".mp3")));
                }

                database.UserData.AddRange(
                    State(adminId, oldChapterId, bookmark, "chapter"),
                    State(limitedId, oldChapterId, bookmark / 2, "limited"),
                    State(adminId, oldOfflineItemId, bookmark, "missing"));
                await database.SaveChangesAsync(TestContext.Current.CancellationToken);
                await database.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_MigrateLiveFolders' OR MigrationId LIKE '%_RestorePlaylistChildrenFromMetadata'", TestContext.Current.CancellationToken);
                // Old online settings are private configuration, not active features.
                var config = legacy.Services.GetRequiredService<IServerConfigurationManager>();
                config.Configuration.PluginRepositories = [new RepositoryInfo { Name = "Obsolete remote repository", Url = "https://example.invalid/must-not-contact" }];
                config.SaveConfiguration();
            }

            var reader = new GuardedReader();
            Guid liveChapterId;
            using (var upgraded = new JellyfinApplicationFactory { TestProfilePath = profile })
            using (var configured = upgraded.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILiveDirectoryReader>();
                services.AddSingleton<ILiveDirectoryReader>(reader);
            })))
            using (var mediaLock = File.Open(chapter, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var client = configured.CreateClient())
            {
                client.DefaultRequestHeaders.AddAuthHeader(token);
                // Host startup and migration have completed while all media access throws.
                Assert.Empty(reader.Stats);
                Assert.Empty(reader.Enumerations);
                var folders = configured.Services.GetRequiredService<ILiveLibrary>();
                Assert.Equal(3, folders.GetLibraries().Count);
                Assert.False(folders.GetLibraries().Single(group => group.Id.Equals(disabledId)).Enabled);
                var saved = configured.Services.GetRequiredService<ILiveUserDataStore>();
                Assert.Equal(2, saved.GetSaved(adminId).Count);
                var root = folders.GetLibraries().Single(group => group.Id.Equals(booksId)).Roots.Single();
                liveChapterId = LiveDirectoryBrowser.EntryId(root, Path.Combine("Novel", "Chapter.m4b"));
                var state = saved.Get(adminId, liveChapterId);
                Assert.Equal(bookmark, state.PlaybackPositionTicks);
                Assert.Equal(duration, state.LastKnownRunTimeTicks);
                Assert.True(state.IsFavorite);
                Assert.Equal(2, state.PlayCount);
                Assert.Equal(7.5, state.Rating);
                Assert.Equal(1, state.AudioStreamIndex);
                Assert.Equal(-1, state.SubtitleStreamIndex);
                Assert.Equal(bookmark / 2, saved.Get(limitedId, liveChapterId).PlaybackPositionTicks);
                using (var addresses = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(stateDirectory, "live-folders.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    await addresses.OpenAsync(TestContext.Current.CancellationToken);
                    using var count = addresses.CreateCommand();
                    count.CommandText = "SELECT COUNT(*) FROM Addresses";
                    Assert.Equal(3L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
                }

                var views = await Get<QueryResult<BaseItemDto>>(client, "UserViews");
                Assert.Equal(new[] { booksId, offlineId }.Order(), views.Items.Select(item => item.Id).Order());
                var restricted = await Get<QueryResult<BaseItemDto>>(client, $"UserViews?userId={limitedId}");
                Assert.Equal(booksId, Assert.Single(restricted.Items).Id);
                using var denied = await client.GetAsync($"Items/{disabledId}", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                using var enabled = await client.PostAsync($"Jigglefin/Folders/{disabledId}/Enabled?enabled=true", null, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
                Assert.Contains((await Get<QueryResult<BaseItemDto>>(client, "UserViews")).Items, item => item.Id.Equals(disabledId));
                Assert.Equal(booksId, Assert.Single((await Get<QueryResult<BaseItemDto>>(client, $"UserViews?userId={limitedId}")).Items).Id);
                using var disabled = await client.PostAsync($"Jigglefin/Folders/{disabledId}/Enabled?enabled=false", null, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
                Assert.Empty(reader.Stats);
                Assert.Empty(reader.Enumerations);
                Assert.Empty(configured.Services.GetRequiredService<IServerConfigurationManager>().Configuration.PluginRepositories);
                await using var database = await configured.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
                Assert.Equal(1006, await database.BaseItems.CountAsync(item => item.Path != null, TestContext.Current.CancellationToken));
                Assert.Equal(3, await database.UserData.CountAsync(TestContext.Current.CancellationToken));
                Assert.Contains(await database.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken), id => id.EndsWith("_RestorePlaylistChildrenFromMetadata", StringComparison.Ordinal));
            }

            reader.AllowReads = true;
            using (var restarted = new JellyfinApplicationFactory { TestProfilePath = profile, FfmpegPath = ffmpeg })
            using (var client = restarted.CreateClient())
            {
                client.DefaultRequestHeaders.AddAuthHeader(token);
                var resume = await Get<QueryResult<BaseItemDto>>(client, "UserItems/Resume");
                var item = Assert.Single(resume.Items);
                Assert.Equal(liveChapterId, item.Id);
                Assert.Equal(bookmark, item.UserData.PlaybackPositionTicks);
                var children = await Get<QueryResult<BaseItemDto>>(client, $"Items?parentId={booksId}");
                var selected = children.Items.Single(entry => entry.Name == "Novel");
                var files = await Get<QueryResult<BaseItemDto>>(client, $"Items?parentId={selected.Id}");
                Assert.Equal(liveChapterId, Assert.Single(files.Items).Id);
                using var staleClientId = await client.GetAsync($"Items/{oldChapterId}", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, staleClientId.StatusCode);
                if (!string.IsNullOrEmpty(ffmpeg))
                {
                    var playback = await Get<PlaybackInfoResponse>(client, $"Items/{liveChapterId}/PlaybackInfo");
                    Assert.Equal(chapter, Assert.Single(playback.MediaSources).Path);
                    Assert.Equal(bookmark, (await Get<BaseItemDto>(client, $"Items/{liveChapterId}")).UserData.PlaybackPositionTicks);
                    using var stream = await client.GetAsync($"Audio/{liveChapterId}/stream?static=true", TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
                    Assert.Equal(source, await stream.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                }

                var store = restarted.Services.GetRequiredService<ILiveUserDataStore>();
                Assert.Equal(2, store.GetSaved(adminId).Count); // missing-drive bookmark is retained
                var state = store.Get(adminId, liveChapterId);
                state.PlaybackPositionTicks = bookmark + 100;
                store.Save(adminId, liveChapterId, state);
                restarted.Services.GetRequiredService<ILiveLibrary>().RemoveLibrary("Offline");
            }

            using (var final = new JellyfinApplicationFactory { TestProfilePath = profile })
            using (var client = final.CreateClient())
            {
                client.DefaultRequestHeaders.AddAuthHeader(token);
                Assert.DoesNotContain(final.Services.GetRequiredService<ILiveLibrary>().GetLibraries(), group => group.Id.Equals(offlineId));
                Assert.Equal(bookmark + 100, (await Get<BaseItemDto>(client, $"Items/{liveChapterId}")).UserData.PlaybackPositionTicks);
            }

            Assert.Equal(originalOptions, await File.ReadAllTextAsync(optionsPath, TestContext.Current.CancellationToken));
            Assert.Equal(source, await File.ReadAllBytesAsync(chapter, TestContext.Current.CancellationToken));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(chapter));
            Assert.False(Directory.Exists(offlinePath));
            Assert.False(Directory.Exists(disabledPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            fixture.Delete(true);
        }
    }

    private static BaseItemEntity Item(Guid id, string type, string path, long? duration = null)
        => new() { Id = id, Type = type, Path = path, Name = Path.GetFileName(path), RunTimeTicks = duration };

    private static UserData State(Guid user, Guid item, long position, string key)
        => new()
        {
            UserId = user, ItemId = item, Item = null, User = null, CustomDataKey = key,
            PlaybackPositionTicks = position, LastPlayedDate = DateTime.UtcNow, PlayCount = 2,
            IsFavorite = true, Rating = 7.5, AudioStreamIndex = 1, SubtitleStreamIndex = -1
        };

    private static async Task<string> WriteGroup(string views, string name, string path, bool enabled)
    {
        var directory = Directory.CreateDirectory(Path.Combine(views, name));
        var document = new XDocument(new XElement(
            "LibraryOptions",
            new XElement("Enabled", enabled),
            new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", path))),
            new XElement("EnableRealtimeMonitor", true),
            new XElement("SaveLocalMetadata", true)));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "options.xml"), document.ToString(), TestContext.Current.CancellationToken);
        return directory.FullName;
    }

    private static async Task<T> Get<T>(HttpClient client, string path)
        => await client.GetFromJsonAsync<T>(path, JsonDefaults.Options, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Missing response.");

    private sealed class GuardedReader : ILiveDirectoryReader
    {
        private readonly PhysicalLiveDirectoryReader _physical = new();

        public bool AllowReads { get; set; }

        public List<string> Stats { get; } = [];

        public List<string> Enumerations { get; } = [];

        public LiveFileInfo Stat(string path)
        {
            Stats.Add(path);
            if (!AllowReads)
            {
                throw new InvalidOperationException("Migration must not stat a media path: " + path);
            }

            return _physical.Stat(path);
        }

        public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
        {
            Enumerations.Add(path);
            if (!AllowReads)
            {
                throw new InvalidOperationException("Migration must not enumerate media: " + path);
            }

            return _physical.EnumerateDirectory(path, cancellationToken);
        }
    }
}
