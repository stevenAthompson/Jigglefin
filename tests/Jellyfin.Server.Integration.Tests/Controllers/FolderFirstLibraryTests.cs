using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class FolderFirstLibraryTests
{
    [Fact]
    public async Task MovieLibraryWithTwoPhysicalRoots_KeepsBothPathsBrowseableAndStreamable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-multiple-roots-" + Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(testRoot, "First Root");
        var secondRoot = Path.Combine(testRoot, "Second Root");
        var firstMovieFolder = Path.Combine(firstRoot, "Action", "Same Movie");
        var secondMovieFolder = Path.Combine(secondRoot, "Action", "Same Movie");
        Directory.CreateDirectory(firstMovieFolder);
        Directory.CreateDirectory(secondMovieFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(firstMovieFolder, "Same Movie.mp4"), videoBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(secondMovieFolder, "Same Movie.mp4"), videoBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin multiple roots " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=movies&paths={Uri.EscapeDataString(firstRoot)}&paths={Uri.EscapeDataString(secondRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);

            var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;
            var pagedFolderIds = new HashSet<Guid>();
            for (var pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                var page = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?userId={userId}&parentId={library.Id}&includeItemTypes=BoxSet,Movie,MusicVideo,Series,Video,Folder,CollectionFolder&sortBy=SortName&sortOrder=Ascending&fields=MediaSources,ParentId,ChannelInfo&enableUserData=true&startIndex={pageIndex}&limit=1",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(page);
                Assert.Equal(2, page.TotalRecordCount);
                var action = Assert.Single(page.Items);
                Assert.Equal("Action", action.Name);
                Assert.Equal(BaseItemKind.Folder, action.Type);
                Assert.True(pagedFolderIds.Add(action.Id));
            }

            var toBrowse = new Queue<Guid>();
            var visited = new HashSet<Guid>();
            var physicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var movies = new List<BaseItemDto>();
            toBrowse.Enqueue(library.Id);
            while (toBrowse.Count > 0)
            {
                var parentId = toBrowse.Dequeue();
                if (!visited.Add(parentId))
                {
                    continue;
                }

                var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={parentId}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(children);
                foreach (var item in children.Items)
                {
                    if (item.Type == BaseItemKind.Movie)
                    {
                        movies.Add(item);
                    }
                    else if (item.IsFolder is true)
                    {
                        Assert.False(string.IsNullOrEmpty(item.Path), $"Browseable folder {item.Name} has no physical path.");
                        physicalPaths.Add(item.Path);
                        toBrowse.Enqueue(item.Id);
                    }
                }
            }

            Assert.Contains(Path.Combine(firstRoot, "Action"), physicalPaths);
            Assert.Contains(Path.Combine(secondRoot, "Action"), physicalPaths);
            Assert.Equal(2, movies.Count);
            Assert.Equal(2, movies.Select(item => item.Id).Distinct().Count());
            Assert.Contains(movies, item => item.Name == "Same Movie" && item.Path?.StartsWith(firstRoot, StringComparison.OrdinalIgnoreCase) is true);
            Assert.Contains(movies, item => item.Name == "Same Movie" && item.Path?.StartsWith(secondRoot, StringComparison.OrdinalIgnoreCase) is true);
            foreach (var movie in movies)
            {
                using var streamResponse = await client.GetAsync(
                    $"Videos/{movie.Id}/stream?static=true", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task TvLibraryWithTwoPhysicalRoots_KeepsSameNamedShowsSeparateAndStreamable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-tv-multiple-roots-" + Guid.NewGuid().ToString("N"));
        var roots = new[] { Path.Combine(testRoot, "First Root"), Path.Combine(testRoot, "Second Root") };
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        foreach (var root in roots)
        {
            var seasonFolder = Path.Combine(root, "Drama", "Same Show", "Season 1");
            Directory.CreateDirectory(seasonFolder);
            await File.WriteAllBytesAsync(
                Path.Combine(seasonFolder, "Same Show - S01E01.mp4"), videoBytes, TestContext.Current.CancellationToken);
        }

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin TV multiple roots " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=tvshows&paths={Uri.EscapeDataString(roots[0])}&paths={Uri.EscapeDataString(roots[1])}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=tvshows", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            var categories = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categories);

            var showIds = new HashSet<Guid>();
            var episodeIds = new HashSet<Guid>();
            foreach (var root in roots)
            {
                var category = Assert.Single(categories.Items, item => item.Name == "Drama" && item.Path == Path.Combine(root, "Drama"));
                Assert.Equal(BaseItemKind.Folder, category.Type);
                var shows = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={category.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(shows);
                var show = Assert.Single(shows.Items, item => item.Name == "Same Show");
                Assert.Equal(BaseItemKind.Series, show.Type);
                Assert.Equal(Path.Combine(root, "Drama", "Same Show"), show.Path);
                Assert.True(showIds.Add(show.Id));

                var seasons = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={show.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(seasons);
                var season = Assert.Single(seasons.Items, item => item.Name == "Season 1");
                Assert.Equal(BaseItemKind.Season, season.Type);
                Assert.Equal(Path.Combine(root, "Drama", "Same Show", "Season 1"), season.Path);
                var episodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={season.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(episodes);
                var episode = Assert.Single(episodes.Items, item => item.Type == BaseItemKind.Episode);
                Assert.Equal(Path.Combine(root, "Drama", "Same Show", "Season 1", "Same Show - S01E01.mp4"), episode.Path);
                Assert.True(episodeIds.Add(episode.Id));
                using var streamResponse = await client.GetAsync(
                    $"Videos/{episode.Id}/stream?static=true", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task MusicLibraryWithTwoPhysicalRoots_KeepsSameNamedAlbumsSeparateAndStreamable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-multiple-roots-" + Guid.NewGuid().ToString("N"));
        var roots = new[] { Path.Combine(testRoot, "First Root"), Path.Combine(testRoot, "Second Root") };
        var audioBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        foreach (var root in roots)
        {
            var albumFolder = Path.Combine(root, "Rock", "Same Artist", "Same Album");
            Directory.CreateDirectory(albumFolder);
            await File.WriteAllBytesAsync(
                Path.Combine(albumFolder, "Track 01.m4a"), audioBytes, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, "Rock", "Same Artist", "artist.nfo"),
                "<artist><name>Same Artist</name></artist>",
                TestContext.Current.CancellationToken);
        }

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music multiple roots " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=music&paths={Uri.EscapeDataString(roots[0])}&paths={Uri.EscapeDataString(roots[1])}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=music", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            var categories = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categories);

            var artistIds = new HashSet<Guid>();
            var albumIds = new HashSet<Guid>();
            var trackIds = new HashSet<Guid>();
            foreach (var root in roots)
            {
                var category = Assert.Single(categories.Items, item => item.Name == "Rock" && item.Path == Path.Combine(root, "Rock"));
                Assert.Equal(BaseItemKind.Folder, category.Type);
                var artists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={category.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(artists);
                var artist = Assert.Single(artists.Items, item => item.Name == "Same Artist");
                Assert.Equal(BaseItemKind.MusicArtist, artist.Type);
                Assert.Equal(Path.Combine(root, "Rock", "Same Artist"), artist.Path);
                Assert.True(artistIds.Add(artist.Id));

                var albums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={artist.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(albums);
                var album = Assert.Single(albums.Items, item => item.Name == "Same Album");
                Assert.Equal(BaseItemKind.MusicAlbum, album.Type);
                Assert.Equal(Path.Combine(root, "Rock", "Same Artist", "Same Album"), album.Path);
                Assert.True(albumIds.Add(album.Id));
                var tracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={album.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(tracks);
                var track = Assert.Single(tracks.Items, item => item.Type == BaseItemKind.Audio);
                Assert.Equal(Path.Combine(root, "Rock", "Same Artist", "Same Album", "Track 01.m4a"), track.Path);
                Assert.True(trackIds.Add(track.Id));
                using var streamResponse = await client.GetAsync(
                    $"Audio/{track.Id}/stream?static=true", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                Assert.Equal(audioBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task MusicAlbumWithBonusVideo_KeepsBothPhysicalFilesBrowseableAndStreamable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-bonus-video-" + Guid.NewGuid().ToString("N"));
        var albumFolder = Path.Combine(testRoot, "Rock", "Artist", "Album");
        Directory.CreateDirectory(albumFolder);
        var audioBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        var trackPath = Path.Combine(albumFolder, "Track 01.m4a");
        var videoPath = Path.Combine(albumFolder, "Bonus Clip.mp4");
        await File.WriteAllBytesAsync(trackPath, audioBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(videoPath, videoBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music bonus video " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=music&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=music", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var categories = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categories);
            var category = Assert.Single(categories.Items, item => item.Path == Path.Combine(testRoot, "Rock"));
            var artists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={category.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(artists);
            var artist = Assert.Single(artists.Items, item => item.Path == Path.Combine(testRoot, "Rock", "Artist"));
            var albums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(albums);
            var album = Assert.Single(albums.Items, item => item.Path == albumFolder);
            var items = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={album.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(items);
            var track = Assert.Single(items.Items, item => item.Path == trackPath && item.Type == BaseItemKind.Audio);
            var video = Assert.Single(items.Items, item => item.Path == videoPath && item.Type == BaseItemKind.MusicVideo);

            using var audioResponse = await client.GetAsync(
                $"Audio/{track.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, audioResponse.StatusCode);
            Assert.Equal(audioBytes, await audioResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            using var videoResponse = await client.GetAsync(
                $"Videos/{video.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, videoResponse.StatusCode);
            Assert.Equal(videoBytes, await videoResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task FolderFirstViews_DoNotExposeBlockedLibraryToAnotherUser()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-folder-permissions-" + Guid.NewGuid().ToString("N"));
        var allowedRoot = Path.Combine(testRoot, "Allowed");
        var blockedRoot = Path.Combine(testRoot, "Blocked");
        Directory.CreateDirectory(Path.Combine(allowedRoot, "Action"));
        Directory.CreateDirectory(Path.Combine(blockedRoot, "Action"));
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(allowedRoot, "Action", "Allowed Film.mp4"), videoBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(blockedRoot, "Action", "Blocked Film.mp4"), videoBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(adminClient));
        var allowedName = "Jigglefin allowed " + Guid.NewGuid().ToString("N");
        var blockedName = "Jigglefin blocked " + Guid.NewGuid().ToString("N");
        var createdNames = new List<string>();

        try
        {
            foreach (var (name, path) in new[] { (allowedName, allowedRoot), (blockedName, blockedRoot) })
            {
                using var createResponse = await adminClient.PostAsJsonAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(name)}&collectionType=movies&paths={Uri.EscapeDataString(path)}&refreshLibrary=false",
                    new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
                createdNames.Add(name);
            }

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var adminViews = await adminClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(adminViews);
            var allowedLibrary = Assert.Single(adminViews.Items, item => item.Name == allowedName);
            var blockedLibrary = Assert.Single(adminViews.Items, item => item.Name == blockedName);
            var blockedGroups = await adminClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={blockedLibrary.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(blockedGroups);
            var blockedGroup = Assert.Single(blockedGroups.Items, item => item.Name == "Action");
            var blockedFilms = await adminClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={blockedGroup.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(blockedFilms);
            var blockedFilm = Assert.Single(blockedFilms.Items, item => item.Name == "Blocked Film");

            var userManager = factory.Services.GetRequiredService<IUserManager>();
            var userName = "limited" + Guid.NewGuid().ToString("N");
            var password = Guid.NewGuid().ToString("N");
            var limitedUser = await userManager.CreateUserAsync(userName);
            var physicalBlockedLibrary = Assert.Single(
                libraryManager.GetUserRootFolder().Children, item => item.Name == blockedName);
            limitedUser.SetPreference(PreferenceKind.BlockedMediaFolders, [physicalBlockedLibrary.Id]);
            await userManager.UpdateUserAsync(limitedUser);
            await userManager.ChangePassword(limitedUser.Id, password);

            using var limitedClient = factory.CreateClient();
            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
            loginRequest.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
            loginRequest.Content = JsonContent.Create(
                new AuthenticateUserByName { Username = userName, Pw = password },
                options: JsonDefaults.Options);
            using var loginResponse = await limitedClient.SendAsync(loginRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            using var loginJson = JsonDocument.Parse(
                await loginResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            limitedClient.DefaultRequestHeaders.AddAuthHeader(loginJson.RootElement.GetProperty("AccessToken").GetString()!);

            var limitedViews = await limitedClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(limitedViews);
            Assert.Single(limitedViews.Items, item => item.Name == allowedName);
            Assert.DoesNotContain(limitedViews.Items, item => item.Name == blockedName);

            var allowedGroups = await limitedClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={allowedLibrary.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(allowedGroups);
            var allowedGroup = Assert.Single(allowedGroups.Items, item => item.Name == "Action");
            var allowedFilms = await limitedClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={allowedGroup.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(allowedFilms);
            var allowedFilm = Assert.Single(allowedFilms.Items, item => item.Name == "Allowed Film");
            using var allowedStream = await limitedClient.GetAsync(
                $"Videos/{allowedFilm.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, allowedStream.StatusCode);
            Assert.Equal(videoBytes, await allowedStream.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            foreach (var url in new[]
            {
                $"Items?ids={allowedFilm.Id}",
                "Items?searchTerm=Allowed%20Film&recursive=true"
            })
            {
                var results = await limitedClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    url, JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(results);
                Assert.Contains(results.Items, item => item.Id.Equals(allowedFilm.Id));
            }

            foreach (var url in new[]
            {
                $"Items?parentId={blockedLibrary.Id}",
                $"Items?parentId={blockedGroup.Id}"
            })
            {
                using var response = await limitedClient.GetAsync(url, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            foreach (var url in new[]
            {
                $"Items?ids={blockedFilm.Id}",
                "Items?searchTerm=Blocked%20Film&recursive=true"
            })
            {
                var results = await limitedClient.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    url, JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(results);
                Assert.False(
                    results.Items.Any(item => item.Id.Equals(blockedFilm.Id)),
                    $"Blocked item was exposed by {url}.");
            }

            foreach (var url in new[]
            {
                $"Items/{blockedFilm.Id}",
                $"Items/{blockedFilm.Id}/Download",
                $"Videos/{blockedFilm.Id}/stream?static=true"
            })
            {
                using var response = await limitedClient.GetAsync(url, TestContext.Current.CancellationToken);
                Assert.False(response.IsSuccessStatusCode, $"Blocked content was accessible at {url}.");
            }

            using var blockedPlaybackInfo = await limitedClient.PostAsJsonAsync(
                $"Items/{blockedFilm.Id}/PlaybackInfo",
                new { },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.False(blockedPlaybackInfo.IsSuccessStatusCode);

            using var anonymousClient = factory.CreateClient();
            using var anonymousStream = await anonymousClient.GetAsync(
                $"Videos/{blockedFilm.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.False(anonymousStream.IsSuccessStatusCode);

            var authenticationManager = factory.Services.GetRequiredService<IAuthenticationManager>();
            await authenticationManager.CreateApiKey("Jigglefin access-control test");
            var apiKey = Assert.Single(await authenticationManager.GetApiKeys());
            using var apiClient = factory.CreateClient();
            apiClient.DefaultRequestHeaders.AddAuthHeader(apiKey.AccessToken);
            using var apiStream = await apiClient.GetAsync(
                $"Videos/{blockedFilm.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, apiStream.StatusCode);
            Assert.Equal(videoBytes, await apiStream.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            foreach (var name in createdNames)
            {
                using var deleteResponse = await adminClient.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(name)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Theory]
    [InlineData("movies", "Root Movie.mp4", BaseItemKind.Movie)]
    [InlineData("tvshows", "Root Show - S01E01.mp4", BaseItemKind.Episode)]
    [InlineData("music", "Root Track.m4a", BaseItemKind.Audio)]
    [InlineData("books", "Root Book.pdf", BaseItemKind.Book)]
    [InlineData("books", "Root Audio.m4b", BaseItemKind.AudioBook)]
    [InlineData("homevideos", "Root Clip.mp4", BaseItemKind.Video)]
    [InlineData("homevideos", "Root Photo.png", BaseItemKind.Photo)]
    [InlineData("musicvideos", "Root Video.mp4", BaseItemKind.MusicVideo)]
    public async Task LibraryRoot_KeepsLooseMediaBesideNestedPhysicalFolder(
        string collectionType,
        string mediaFileName,
        BaseItemKind mediaKind)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-root-media-" + Guid.NewGuid().ToString("N"));
        var nestedFolder = Path.Combine(testRoot, "Physical Group", "Nested");
        Directory.CreateDirectory(nestedFolder);
        var rootMediaPath = Path.Combine(testRoot, mediaFileName);
        var sampleName = mediaFileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
            || mediaFileName.EndsWith(".m4b", StringComparison.OrdinalIgnoreCase)
            ? "JigglefinSample.m4b"
            : "JigglefinSample.mp4";
        byte[] mediaBytes;
        if (mediaKind == BaseItemKind.Book)
        {
            mediaBytes = [];
        }
        else if (mediaKind == BaseItemKind.Photo)
        {
            mediaBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII=");
        }
        else
        {
            mediaBytes = await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Test Data", sampleName),
                TestContext.Current.CancellationToken);
        }

        await File.WriteAllBytesAsync(rootMediaPath, mediaBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(nestedFolder, mediaFileName.Replace("Root", "Nested", StringComparison.Ordinal)),
            mediaBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin root media " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType={collectionType}&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"UserViews?presetViews={collectionType}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var rootMedia = Assert.Single(rootItems.Items, item => item.Path == rootMediaPath);
            Assert.Equal(mediaKind, rootMedia.Type);
            if (mediaKind == BaseItemKind.Photo)
            {
                using var imageResponse = await client.GetAsync(
                    $"Items/{rootMedia.Id}/Images/Primary", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
                Assert.Equal("image/png", imageResponse.Content.Headers.ContentType?.MediaType);
            }
            else if (mediaKind != BaseItemKind.Book)
            {
                var streamPath = mediaKind is BaseItemKind.Audio or BaseItemKind.AudioBook
                    ? $"Audio/{rootMedia.Id}/stream?static=true"
                    : $"Videos/{rootMedia.Id}/stream?static=true";
                using var streamResponse = await client.GetAsync(streamPath, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                Assert.Equal(mediaBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }

            var group = Assert.Single(rootItems.Items, item => item.Path == Path.Combine(testRoot, "Physical Group"));
            Assert.True(group.IsFolder);
            var webFolderItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&sortBy=IsFolder,SortName&fields=Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webFolderItems);
            Assert.Equal(rootItems.TotalRecordCount, webFolderItems.TotalRecordCount);
            Assert.Single(webFolderItems.Items, item => item.Id.Equals(rootMedia.Id));
            Assert.Single(webFolderItems.Items, item => item.Id.Equals(group.Id) && item.IsFolder == true);
            var groupItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={group.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groupItems);
            Assert.Single(groupItems.Items, item => item.Path == nestedFolder);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Theory]
    [InlineData("movies", "Named Item.mp4", BaseItemKind.Movie)]
    [InlineData("tvshows", "Named Item - S01E01.mp4", BaseItemKind.Series)]
    [InlineData("books", "Named Item.pdf", BaseItemKind.Book)]
    [InlineData("music", "Track 01.mp3", BaseItemKind.MusicAlbum)]
    public async Task NestedGroupingFolders_StayPhysicalBeforeTypedMedia(
        string collectionType,
        string mediaFileName,
        BaseItemKind mediaKind)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-nested-groups-" + Guid.NewGuid().ToString("N"));
        var mediaFolder = Path.Combine(testRoot, "Genre", "Decade", "Named Item");
        Directory.CreateDirectory(mediaFolder);
        await File.WriteAllBytesAsync(Path.Combine(mediaFolder, mediaFileName), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin nested groups " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType={collectionType}&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"UserViews?presetViews={collectionType}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            var parentId = library.Id;
            foreach (var (name, path) in new[]
            {
                ("Genre", Path.Combine(testRoot, "Genre")),
                ("Decade", Path.Combine(testRoot, "Genre", "Decade"))
            })
            {
                var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={parentId}&fields=Path",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(children);
                var folder = Assert.Single(children.Items, item => item.Name == name);
                Assert.Equal(BaseItemKind.Folder, folder.Type);
                Assert.Equal(path, folder.Path);
                var nativeChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={parentId}&includeItemTypes=Folder,CollectionFolder,Movie,Series,MusicAlbum,Book&sortBy=SortName",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(nativeChildren);
                Assert.Equal(folder.Id, Assert.Single(nativeChildren.Items, item => item.Name == name).Id);
                parentId = folder.Id;
            }

            var mediaEntries = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={parentId}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(mediaEntries);
            var media = Assert.Single(mediaEntries.Items);
            Assert.Equal(mediaKind, media.Type);
            var details = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{media.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(mediaKind, details?.Type);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Theory]
    [InlineData("movies", "Existing Movie.mp4")]
    [InlineData("tvshows", "Existing Show - S01E01.mkv")]
    [InlineData("music", "Existing Track.mp3")]
    [InlineData("books", "Existing Book.pdf")]
    [InlineData("homevideos", "Existing Clip.mp4")]
    [InlineData("musicvideos", "Existing Video.mp4")]
    public async Task EmptyPhysicalFolders_RemainBrowseableBesideMedia(string collectionType, string mediaFileName)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-empty-media-folders-" + Guid.NewGuid().ToString("N"));
        var emptyChild = Path.Combine(testRoot, "Coming Soon", "Unsorted");
        var populated = Path.Combine(testRoot, "Action", "Named Item");
        Directory.CreateDirectory(emptyChild);
        Directory.CreateDirectory(populated);
        await File.WriteAllBytesAsync(Path.Combine(populated, mediaFileName), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin empty media folders " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType={collectionType}&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"UserViews?presetViews={collectionType}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var categories = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categories);
            var emptyCategory = Assert.Single(categories.Items, item => item.Name == "Coming Soon");
            Assert.Equal(BaseItemKind.Folder, emptyCategory.Type);
            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={emptyCategory.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            var unsorted = Assert.Single(children.Items, item => item.Name == "Unsorted");
            Assert.Equal(BaseItemKind.Folder, unsorted.Type);
            var webChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={emptyCategory.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webChildren);
            Assert.Equal(unsorted.Id, Assert.Single(webChildren.Items).Id);
            var leaf = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={unsorted.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(leaf);
            Assert.Empty(leaf.Items);
            Assert.Single(categories.Items, item => item.Name == "Action" && item.Type == BaseItemKind.Folder);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Theory]
    [InlineData("Double Feature")]
    [InlineData("First Feature")]
    public async Task TwoDistinctMoviesInOneDirectory_KeepBothFilesBrowseableAndStreamable(string folderName)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-two-movies-one-folder-" + Guid.NewGuid().ToString("N"));
        var mixedFolder = Path.Combine(testRoot, "Action", folderName);
        Directory.CreateDirectory(mixedFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "First Feature.mp4"), videoBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "Second Feature.mp4"), videoBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin two movie folder " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=movies&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            var categoryItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categoryItems);
            var mixed = Assert.Single(categoryItems.Items, item => item.Name == folderName);
            Assert.Equal(BaseItemKind.Folder, mixed.Type);
            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            Assert.Equal(2, movies.Items.Count);
            Assert.All(movies.Items, item => Assert.Equal(BaseItemKind.Movie, item.Type));
            foreach (var name in new[] { "First Feature", "Second Feature" })
            {
                var movie = Assert.Single(movies.Items, item => item.Name == name);
                using var streamResponse = await client.GetAsync(
                    $"Videos/{movie.Id}/stream?static=true", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task HomeVideoPhotos_KeepMixedFoldersAndPhotoAlbumsBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-homevideo-photo-folders-" + Guid.NewGuid().ToString("N"));
        var holidays = Path.Combine(testRoot, "Holidays");
        var mixed = Path.Combine(holidays, "Mixed Day");
        var nested = Path.Combine(holidays, "Nested Day");
        var nestedChild = Path.Combine(nested, "More Photos");
        var photosOnly = Path.Combine(holidays, "Photos Only");
        Directory.CreateDirectory(mixed);
        Directory.CreateDirectory(nestedChild);
        Directory.CreateDirectory(photosOnly);
        var photoBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII=");
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(mixed, "Clip.mp4"), videoBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(mixed, "Snapshot.png"), photoBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(nested, "Snapshot.png"), photoBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(nestedChild, "Another.png"), photoBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(photosOnly, "Snapshot.png"), photoBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin homevideo photo folders " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=homevideos&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=homevideos", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var holidaysItem = Assert.Single(rootItems.Items, item => item.Name == "Holidays");
            Assert.Equal(BaseItemKind.Folder, holidaysItem.Type);

            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={holidaysItem.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            var mixedItem = Assert.Single(children.Items, item => item.Name == "Mixed Day");
            var nestedItem = Assert.Single(children.Items, item => item.Name == "Nested Day");
            var photosItem = Assert.Single(children.Items, item => item.Name == "Photos Only");
            Assert.Equal(BaseItemKind.Folder, mixedItem.Type);
            Assert.Equal(BaseItemKind.Folder, nestedItem.Type);
            Assert.Equal(BaseItemKind.PhotoAlbum, photosItem.Type);
            var photoAlbumDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{photosItem.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(BaseItemKind.PhotoAlbum, photoAlbumDetails?.Type);

            var webChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={holidaysItem.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webChildren);
            Assert.Equal(3, webChildren.Items.Count);
            Assert.All(webChildren.Items, item => Assert.Equal(BaseItemKind.Folder, item.Type));

            var mixedChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixedItem.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(mixedChildren);
            Assert.Single(mixedChildren.Items, item => item.Type == BaseItemKind.Video);
            Assert.Single(mixedChildren.Items, item => item.Type == BaseItemKind.Photo);
            var nestedChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={nestedItem.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(nestedChildren);
            Assert.Single(nestedChildren.Items, item => item.Type == BaseItemKind.Photo);
            var nestedAlbum = Assert.Single(nestedChildren.Items, item => item.Name == "More Photos");
            Assert.Equal(BaseItemKind.PhotoAlbum, nestedAlbum.Type);
            var webNestedChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={nestedItem.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webNestedChildren);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(webNestedChildren.Items, item => item.Id.Equals(nestedAlbum.Id)).Type);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task HomeVideoDiscRip_WithPhoto_KeepsBothPhysicalEntriesBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-homevideo-disc-photo-" + Guid.NewGuid().ToString("N"));
        var featureFolder = Path.Combine(testRoot, "Family", "Disc Feature");
        var videoTsFolder = Path.Combine(featureFolder, "VIDEO_TS");
        Directory.CreateDirectory(videoTsFolder);
        await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VIDEO_TS.IFO"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VTS_01_1.VOB"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(featureFolder, "Snapshot.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII="),
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin homevideo disc photo " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=homevideos&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=homevideos", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var family = Assert.Single(rootItems.Items, item => item.Name == "Family");
            Assert.Equal(BaseItemKind.Folder, family.Type);
            var familyItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={family.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(familyItems);
            var feature = Assert.Single(familyItems.Items, item => item.Name == "Disc Feature");
            Assert.True(feature.IsFolder);
            var featureItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={feature.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(featureItems);
            Assert.Single(featureItems.Items, item => item.Type == BaseItemKind.Video);
            Assert.Single(featureItems.Items, item => item.Type == BaseItemKind.Photo);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task MultiChapterAudioBook_KeepsEveryPhysicalAudioFileBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-audiobook-chapters-" + Guid.NewGuid().ToString("N"));
        var bookFolder = Path.Combine(testRoot, "Example Author", "Example Book");
        Directory.CreateDirectory(bookFolder);
        var audioBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        foreach (var chapterName in new[] { "Chapter 1.m4b", "Chapter 2.m4b", "Extra.m4b" })
        {
            await File.WriteAllBytesAsync(Path.Combine(bookFolder, chapterName), audioBytes, TestContext.Current.CancellationToken);
        }

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin audiobook chapters " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=books&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=books", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var authors = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(authors);
            var author = Assert.Single(authors.Items, item => item.Name == "Example Author");
            Assert.Equal(BaseItemKind.Folder, author.Type);
            var books = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={author.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(books);
            var book = Assert.Single(books.Items, item => item.Name == "Example Book");
            Assert.Equal(BaseItemKind.Folder, book.Type);
            var chapters = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={book.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(chapters);
            Assert.Equal(3, chapters.Items.Count);
            Assert.All(chapters.Items, item => Assert.Equal(BaseItemKind.AudioBook, item.Type));
            Assert.Contains(chapters.Items, item => item.Name == "Chapter 1");
            Assert.Contains(chapters.Items, item => item.Name == "Extra");
            var secondChapter = Assert.Single(chapters.Items, item => item.Name == "Chapter 2");
            using var streamResponse = await client.GetAsync(
                $"Audio/{secondChapter.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(audioBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            // Playback reports must bookmark the underlying AudioBook, regardless
            // of the client-facing Audio DTO used by Android TV.
            var configuration = factory.Services.GetRequiredService<IServerConfigurationManager>().Configuration;
            configuration.MinAudiobookResume = 0;
            configuration.MaxAudiobookResume = 0;
            var chapterItem = Assert.IsType<MediaBrowser.Controller.Entities.AudioBook>(libraryManager.GetItemById(secondChapter.Id));
            chapterItem.RunTimeTicks = TimeSpan.FromSeconds(1).Ticks;
            await libraryManager.UpdateItemAsync(
                chapterItem,
                chapterItem.GetParent(),
                ItemUpdateType.MetadataEdit,
                TestContext.Current.CancellationToken);
            using var startResponse = await client.PostAsJsonAsync(
                "Sessions/Playing",
                new PlaybackStartInfo { ItemId = secondChapter.Id, PositionTicks = 0 },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, startResponse.StatusCode);

            // The synthetic sample is one second long; stop before its end.
            var bookmarkTicks = TimeSpan.FromMilliseconds(250).Ticks;
            using var stopResponse = await client.PostAsJsonAsync(
                "Sessions/Playing/Stopped",
                new PlaybackStopInfo { ItemId = secondChapter.Id, PositionTicks = bookmarkTicks },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, stopResponse.StatusCode);

            var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;
            var bookmarkedChapter = await client.GetFromJsonAsync<BaseItemDto>(
                $"Users/{userId}/Items/{secondChapter.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(bookmarkedChapter);
            Assert.Equal(BaseItemKind.AudioBook, bookmarkedChapter.Type);
            Assert.Equal(bookmarkTicks, bookmarkedChapter.UserData?.PlaybackPositionTicks);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task Series_WithUnnumberedVideoSubfolder_KeepsPhysicalFolderBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-series-subfolder-" + Guid.NewGuid().ToString("N"));
        var seriesFolder = Path.Combine(testRoot, "Drama", "Example Show");
        var seasonFolder = Path.Combine(seriesFolder, "Season 1");
        var seasonBonusFolder = Path.Combine(seasonFolder, "Season Bonus");
        var bonusFolder = Path.Combine(seriesFolder, "Bonus Collection");
        Directory.CreateDirectory(seasonFolder);
        Directory.CreateDirectory(seasonBonusFolder);
        Directory.CreateDirectory(bonusFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(seasonFolder, "Example Show - S01E01.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(bonusFolder, "Bonus Clip.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(seasonBonusFolder, "Season Bonus Clip.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin series subfolder " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=tvshows&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=tvshows", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var drama = Assert.Single(groups.Items, item => item.Name == "Drama");
            var seriesItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seriesItems);
            var series = Assert.Single(seriesItems.Items, item => item.Name == "Example Show");
            Assert.Equal(BaseItemKind.Series, series.Type);
            var webFolderSeriesItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webFolderSeriesItems);
            var webFolderSeries = Assert.Single(webFolderSeriesItems.Items, item => item.Id.Equals(series.Id));
            Assert.Equal(BaseItemKind.Folder, webFolderSeries.Type);
            Assert.True(webFolderSeries.IsFolder);
            var nameSortedSeriesItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}&sortBy=SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nameSortedSeriesItems);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(nameSortedSeriesItems.Items).Type);
            var seriesDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(BaseItemKind.Series, seriesDetails?.Type);
            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            Assert.Equal(2, children.TotalRecordCount);
            Assert.DoesNotContain(children.Items, item => item.Name == "Season Unknown");
            var season = Assert.Single(children.Items, item => item.Name == "Season 1");
            Assert.Equal(BaseItemKind.Season, season.Type);
            var webFolderSeasonItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webFolderSeasonItems);
            var webFolderSeason = Assert.Single(webFolderSeasonItems.Items, item => item.Id.Equals(season.Id));
            Assert.Equal(BaseItemKind.Folder, webFolderSeason.Type);
            Assert.True(webFolderSeason.IsFolder);
            var nameSortedSeasonItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}&sortBy=SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nameSortedSeasonItems);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(nameSortedSeasonItems.Items, item => item.Id.Equals(season.Id)).Type);
            var nativeSeasonItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}&includeItemTypes=Folder,CollectionFolder,Series,Season,Episode,Video&sortBy=SortName",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nativeSeasonItems);
            Assert.Equal(2, nativeSeasonItems.Items.Count);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(nativeSeasonItems.Items, item => item.Id.Equals(season.Id)).Type);
            Assert.Single(nativeSeasonItems.Items, item => item.Name == "Bonus Collection" && item.Type == BaseItemKind.Folder);
            Assert.DoesNotContain(nativeSeasonItems.Items, item => item.Name == "Season Unknown");
            var swiftfinSeasonItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}&includeItemTypes=BoxSet,Movie,MusicVideo,Series,Video,Folder,CollectionFolder&sortBy=SortName&sortOrder=Ascending&fields=MediaSources,ParentId,ChannelInfo&enableUserData=true&startIndex=0&limit=2",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(swiftfinSeasonItems);
            Assert.Equal(2, swiftfinSeasonItems.TotalRecordCount);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(swiftfinSeasonItems.Items, item => item.Id.Equals(season.Id)).Type);
            Assert.Single(swiftfinSeasonItems.Items, item => item.Name == "Bonus Collection" && item.Type == BaseItemKind.Folder);
            Assert.DoesNotContain(swiftfinSeasonItems.Items, item => item.Name == "Season Unknown");
            Assert.Single(webFolderSeasonItems.Items, item => item.Name == "Bonus Collection" && item.Type == BaseItemKind.Folder);
            Assert.DoesNotContain(webFolderSeasonItems.Items, item => item.Name == "Season Unknown");
            var pagedWebSeasonItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount&limit=1",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(pagedWebSeasonItems);
            Assert.Equal(2, pagedWebSeasonItems.TotalRecordCount);
            Assert.Single(pagedWebSeasonItems.Items);
            var seasonDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{season.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(BaseItemKind.Season, seasonDetails?.Type);
            var seasonChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={season.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasonChildren);
            Assert.Single(seasonChildren.Items, item => item.Type == BaseItemKind.Episode);
            var seasonBonus = Assert.Single(seasonChildren.Items, item => item.Name == "Season Bonus");
            Assert.Equal(BaseItemKind.Folder, seasonBonus.Type);
            var webSeasonChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={season.Id}&sortBy=SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webSeasonChildren);
            Assert.Single(webSeasonChildren.Items, item => item.Type == BaseItemKind.Episode);
            Assert.Single(webSeasonChildren.Items, item => item.Id.Equals(seasonBonus.Id));
            var nativeSeasonChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={season.Id}&includeItemTypes=Folder,CollectionFolder,Series,Season,Episode,Video&sortBy=SortName",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nativeSeasonChildren);
            Assert.Single(nativeSeasonChildren.Items, item => item.Type == BaseItemKind.Episode);
            Assert.Single(nativeSeasonChildren.Items, item => item.Id.Equals(seasonBonus.Id) && item.Type == BaseItemKind.Folder);
            var seasonBonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={seasonBonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasonBonusChildren);
            var seasonBonusEpisode = Assert.Single(seasonBonusChildren.Items, item => item.Type == BaseItemKind.Episode);
            var episodeItem = libraryManager.GetItemById(seasonBonusEpisode.Id);
            Assert.Equal(1, episodeItem?.ParentIndexNumber);
            var bonus = Assert.Single(children.Items, item => item.Name == "Bonus Collection");
            Assert.Equal(BaseItemKind.Folder, bonus.Type);
            var bonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={bonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(bonusChildren);
            var bonusEpisode = Assert.Single(bonusChildren.Items, item => item.Type == BaseItemKind.Episode);
            using var streamResponse = await client.GetAsync(
                $"Videos/{bonusEpisode.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            var seasons = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Shows/{series.Id}/Seasons", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasons);
            Assert.Contains(seasons.Items, item => item.Name == "Season 1");
            Assert.All(seasons.Items, item => Assert.Equal(BaseItemKind.Season, item.Type));
            var groupedEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Shows/{series.Id}/Episodes?seasonId={season.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groupedEpisodes);
            Assert.Contains(groupedEpisodes.Items, item => item.Name == "Season Bonus Clip");
            var unknownSeason = Assert.Single(seasons.Items, item => item.Name == "Season Unknown");
            var unknownEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={unknownSeason.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(unknownEpisodes);
            Assert.Single(unknownEpisodes.Items, item => item.Type == BaseItemKind.Episode);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task MovieFileAndSameNamedDirectory_KeepBothPhysicalPathsBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-movie-name-collision-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var namedFolder = Path.Combine(categoryFolder, "Shared Movie");
        Directory.CreateDirectory(namedFolder);
        await File.WriteAllBytesAsync(Path.Combine(categoryFolder, "Shared Movie.mp4"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(namedFolder, "Shared Movie.mp4"), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin movie name collision " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=movies&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            var categoryItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categoryItems);
            Assert.Equal(2, categoryItems.TotalRecordCount);
            Assert.Single(categoryItems.Items, item => item.Name == "Shared Movie" && item.Type == BaseItemKind.Movie);
            var physicalFolder = Assert.Single(categoryItems.Items, item => item.Name == "Shared Movie" && item.Type == BaseItemKind.Folder);
            var webBrowse = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webBrowse);
            Assert.Equal(2, webBrowse.TotalRecordCount);
            Assert.Single(webBrowse.Items, item => item.Id.Equals(physicalFolder.Id) && item.Type == BaseItemKind.Folder);
            var folderItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={physicalFolder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(folderItems);
            Assert.Single(folderItems.Items, item => item.Name == "Shared Movie" && item.Type == BaseItemKind.Movie);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task BookAndAudioBookFilesBesideSameNamedDirectories_KeepPhysicalFoldersBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-book-name-collisions-" + Guid.NewGuid().ToString("N"));
        foreach (var (category, title, extension) in new[]
        {
            ("Reading", "Shared Book", ".pdf"),
            ("Listening", "Shared Audio", ".m4b")
        })
        {
            var categoryFolder = Path.Combine(testRoot, category);
            var namedFolder = Path.Combine(categoryFolder, title);
            Directory.CreateDirectory(namedFolder);
            await File.WriteAllBytesAsync(Path.Combine(categoryFolder, title + extension), [], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(namedFolder, title + extension), [], TestContext.Current.CancellationToken);
        }

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin book name collisions " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=books&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=books", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var categories = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categories);

            foreach (var (category, title, kind) in new[]
            {
                ("Reading", "Shared Book", BaseItemKind.Book),
                ("Listening", "Shared Audio", BaseItemKind.AudioBook)
            })
            {
                var categoryItem = Assert.Single(categories.Items, item => item.Name == category);
                var categoryItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={categoryItem.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(categoryItems);
                Assert.Equal(2, categoryItems.TotalRecordCount);
                Assert.True(
                    categoryItems.Items.Any(item => item.Name == title && item.Type == kind),
                    $"{category}: {string.Join(", ", categoryItems.Items.Select(item => item.Name + ":" + item.Type))}");
                var physicalFolder = Assert.Single(categoryItems.Items, item => item.Name == title && item.Type == BaseItemKind.Folder);
                var webBrowse = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={categoryItem.Id}&sortBy=IsFolder,SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(webBrowse);
                Assert.Equal(2, webBrowse.TotalRecordCount);
                Assert.Single(webBrowse.Items, item => item.Id.Equals(physicalFolder.Id) && item.Type == BaseItemKind.Folder);
                var folderItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={physicalFolder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(folderItems);
                Assert.Single(folderItems.Items, item => item.Name == title && item.Type == kind);
            }
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task MovieWithExtrasFolder_KeepsPhysicalExtrasBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-movie-extras-" + Guid.NewGuid().ToString("N"));
        var movieFolder = Path.Combine(testRoot, "Action", "Named Movie");
        var trailerOnlyFolder = Path.Combine(testRoot, "Action", "Trailer Only Film");
        var extrasFolder = Path.Combine(movieFolder, "Extras");
        var trailersFolder = Path.Combine(movieFolder, "Trailers");
        Directory.CreateDirectory(extrasFolder);
        Directory.CreateDirectory(trailersFolder);
        Directory.CreateDirectory(trailerOnlyFolder);
        await File.WriteAllBytesAsync(Path.Combine(movieFolder, "Named Movie.mp4"), [], TestContext.Current.CancellationToken);
        var looseTrailerPath = Path.Combine(movieFolder, "Named Movie-trailer.mp4");
        var trailerBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(looseTrailerPath, trailerBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(trailerOnlyFolder, "Trailer Only Film.mp4"), trailerBytes, TestContext.Current.CancellationToken);
        var trailerOnlyPath = Path.Combine(trailerOnlyFolder, "Trailer Only Film-trailer.mp4");
        await File.WriteAllBytesAsync(trailerOnlyPath, trailerBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(extrasFolder, "Featurette.mp4"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(trailersFolder, "Preview.mp4"), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin movie extras " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=movies&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            var actionItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(actionItems);
            var movie = Assert.Single(actionItems.Items, item => item.Name == "Named Movie");
            Assert.Equal(BaseItemKind.Folder, movie.Type);
            var trailerOnlyMovie = Assert.Single(actionItems.Items, item => item.Name == "Trailer Only Film");
            Assert.Equal(BaseItemKind.Folder, trailerOnlyMovie.Type);
            var trailerOnlyItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={trailerOnlyMovie.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(trailerOnlyItems);
            var trailerOnlyMain = Assert.Single(trailerOnlyItems.Items, item => item.Path == Path.Combine(trailerOnlyFolder, "Trailer Only Film.mp4"));
            var trailerOnlyFile = Assert.Single(trailerOnlyItems.Items, item => item.Path == trailerOnlyPath);
            var trailerOnlyEntity = libraryManager.GetItemById(trailerOnlyMain.Id);
            Assert.NotNull(trailerOnlyEntity);
            Assert.False(trailerOnlyEntity.IsInMixedFolder, $"Name: {trailerOnlyEntity.Name}; parent: {trailerOnlyEntity.GetParent()?.Path}");
            var trailerOnlyMetadata = await client.GetFromJsonAsync<BaseItemDto[]>(
                $"Items/{trailerOnlyMain.Id}/LocalTrailers", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(trailerOnlyMetadata);
            var ownedTrailer = Assert.Single(trailerOnlyMetadata, item => item.Type == BaseItemKind.Trailer && !item.Id.Equals(trailerOnlyFile.Id));
            using (var trailerOnlyResponse = await client.GetAsync(
                $"Videos/{trailerOnlyFile.Id}/stream?static=true", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, trailerOnlyResponse.StatusCode);
                Assert.Equal(trailerBytes, await trailerOnlyResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }

            var movieItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={movie.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(movieItems);
            var mainMovie = Assert.Single(movieItems.Items, item => item.Name == "Named Movie" && item.Type == BaseItemKind.Movie);
            var looseTrailer = Assert.Single(movieItems.Items, item => item.Path == looseTrailerPath);
            Assert.Equal("Named Movie-trailer", looseTrailer.Name);
            var trailerMetadata = await client.GetFromJsonAsync<BaseItemDto[]>(
                $"Items/{mainMovie.Id}/LocalTrailers", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(trailerMetadata);
            Assert.Single(trailerMetadata, item => item.Type == BaseItemKind.Trailer && !item.Id.Equals(looseTrailer.Id));
            using (var trailerResponse = await client.GetAsync(
                $"Videos/{looseTrailer.Id}/stream?static=true", TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, trailerResponse.StatusCode);
                Assert.Equal(trailerBytes, await trailerResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }

            var extras = Assert.Single(movieItems.Items, item => item.Name == "Extras");
            Assert.Equal(BaseItemKind.Folder, extras.Type);
            var extraItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={extras.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(extraItems);
            Assert.Single(extraItems.Items, item => item.Name == "Featurette" && item.Type == BaseItemKind.Movie);
            var trailers = Assert.Single(movieItems.Items, item => item.Name == "Trailers");
            Assert.Equal(BaseItemKind.Folder, trailers.Type);
            var trailerItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={trailers.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(trailerItems);
            Assert.Single(trailerItems.Items, item => item.Name == "Preview" && item.Type == BaseItemKind.Movie);

            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var refreshedMovieItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={movie.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(refreshedMovieItems);
            Assert.Single(refreshedMovieItems.Items, item => item.Name == "Named Movie" && item.Type == BaseItemKind.Movie);
            Assert.Single(refreshedMovieItems.Items, item => item.Path == looseTrailerPath);
            Assert.Single(refreshedMovieItems.Items, item => item.Id.Equals(extras.Id));
            Assert.Single(refreshedMovieItems.Items, item => item.Id.Equals(trailers.Id));
            var refreshedTrailerOnlyItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={trailerOnlyMovie.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(refreshedTrailerOnlyItems);
            Assert.Single(refreshedTrailerOnlyItems.Items, item => item.Path == trailerOnlyPath && item.Id.Equals(trailerOnlyFile.Id));
            var refreshedLocalTrailers = await client.GetFromJsonAsync<BaseItemDto[]>(
                $"Items/{trailerOnlyMain.Id}/LocalTrailers", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(refreshedLocalTrailers);
            Assert.Single(refreshedLocalTrailers, item => item.Type == BaseItemKind.Trailer);

            File.Delete(trailerOnlyPath);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            Assert.Null(libraryManager.GetItemById(trailerOnlyFile.Id));
            Assert.Null(libraryManager.GetItemById(ownedTrailer.Id));
            var actionAfterRemoval = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(actionAfterRemoval);
            Assert.Single(actionAfterRemoval.Items, item => item.Name == "Trailer Only Film" && item.Type == BaseItemKind.Movie);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task MovieDiscRips_PreservePhysicalCategoryAndSiblingFolders()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-movie-disc-subfolders-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var directRipFolder = Path.Combine(categoryFolder, "Disc Feature");
        var directRipVideoTs = Path.Combine(directRipFolder, "VIDEO_TS");
        var stackedRipFolder = Path.Combine(categoryFolder, "Disc Collection");
        var stackedRipVideoTs = Path.Combine(stackedRipFolder, "Disc 1", "VIDEO_TS");
        var standaloneRipVideoTs = Path.Combine(categoryFolder, "Standalone DVD", "VIDEO_TS");
        var soloRipVideoTs = Path.Combine(testRoot, "Solo Category", "Only DVD", "VIDEO_TS");
        var singleDiscVideoTs = Path.Combine(testRoot, "Single Disc Category", "Disc 1", "VIDEO_TS");
        Directory.CreateDirectory(directRipVideoTs);
        Directory.CreateDirectory(stackedRipVideoTs);
        Directory.CreateDirectory(standaloneRipVideoTs);
        Directory.CreateDirectory(soloRipVideoTs);
        Directory.CreateDirectory(singleDiscVideoTs);
        Directory.CreateDirectory(Path.Combine(directRipFolder, "Bonus Film"));
        Directory.CreateDirectory(Path.Combine(stackedRipFolder, "Other Film"));
        foreach (var videoTsFolder in new[] { directRipVideoTs, stackedRipVideoTs, standaloneRipVideoTs, soloRipVideoTs, singleDiscVideoTs })
        {
            await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VIDEO_TS.IFO"), [], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VTS_01_1.VOB"), [], TestContext.Current.CancellationToken);
        }

        await File.WriteAllBytesAsync(Path.Combine(directRipFolder, "Bonus Film", "Bonus Film.mp4"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directRipFolder, "Disc Feature.mp4"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(stackedRipFolder, "Other Film", "Other Film.mp4"), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin movie disc subfolders " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=movies&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            var soloCategory = Assert.Single(rootItems.Items, item => item.Name == "Solo Category");
            Assert.Equal(BaseItemKind.Folder, soloCategory.Type);
            var soloChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={soloCategory.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(soloChildren);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(soloChildren.Items, item => item.Name == "Only DVD").Type);
            var singleDiscCategory = Assert.Single(rootItems.Items, item => item.Name == "Single Disc Category");
            Assert.Equal(BaseItemKind.Folder, singleDiscCategory.Type);
            var singleDiscChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={singleDiscCategory.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(singleDiscChildren);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(singleDiscChildren.Items, item => item.Name == "Disc 1").Type);
            var collections = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(collections);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(collections.Items, item => item.Name == "Standalone DVD").Type);

            foreach (var (folderName, siblingName) in new[]
            {
                ("Disc Feature", "Bonus Film"),
                ("Disc Collection", "Other Film")
            })
            {
                var folder = Assert.Single(collections.Items, item => item.Name == folderName);
                Assert.Equal(BaseItemKind.Folder, folder.Type);
                var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={folder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(children);
                Assert.Single(children.Items, item => item.Name == siblingName);
                var disc = Assert.Single(children.Items, item => item.Name == (folderName == "Disc Feature" ? "VIDEO_TS" : "Disc 1"));
                Assert.Equal(BaseItemKind.Movie, disc.Type);
                if (folderName == "Disc Feature")
                {
                    Assert.Equal(BaseItemKind.Movie, Assert.Single(children.Items, item => item.Name == "Disc Feature").Type);
                }
            }
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task MusicAlbum_WithOtherPhysicalSubfolder_RemainsBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-subfolder-" + Guid.NewGuid().ToString("N"));
        var mixedFolder = Path.Combine(testRoot, "Action", "Mixed Album");
        var bonusFolder = Path.Combine(mixedFolder, "Bonus Material");
        var discFolder = Path.Combine(testRoot, "Action", "Multi Disc Album", "Disc 1");
        Directory.CreateDirectory(bonusFolder);
        Directory.CreateDirectory(discFolder);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "Track 01.mp3"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(bonusFolder, "Bonus Track.mp3"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(discFolder, "Track 01.mp3"), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music subfolder " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=music&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=music", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            var actionItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(actionItems);
            var mixed = Assert.Single(actionItems.Items, item => item.Name == "Mixed Album");
            Assert.Equal(BaseItemKind.Folder, mixed.Type);
            var multiDiscAlbum = Assert.Single(actionItems.Items, item => item.Name == "Multi Disc Album");
            Assert.Equal(BaseItemKind.MusicAlbum, multiDiscAlbum.Type);
            var albumItem = Assert.IsType<MediaBrowser.Controller.Entities.Audio.MusicAlbum>(libraryManager.GetItemById(multiDiscAlbum.Id));
            Assert.Contains(albumItem.Children, item => item.Name == "Disc 1");
            var discItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(discItems);
            var disc = Assert.Single(discItems.Items, item => item.Name == "Disc 1");
            Assert.True(disc.IsFolder);
            var webFolderDiscItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&sortBy=IsFolder,SortName",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(webFolderDiscItems);
            Assert.Single(webFolderDiscItems.Items, item => item.Id.Equals(disc.Id));
            var nameSortedDiscItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&sortBy=SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nameSortedDiscItems);
            Assert.Single(nameSortedDiscItems.Items, item => item.Id.Equals(disc.Id));
            var nativeFolderDiscItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&includeItemTypes=Folder,CollectionFolder,MusicAlbum,Audio&sortBy=SortName&sortOrder=Ascending",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nativeFolderDiscItems);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(nativeFolderDiscItems.Items, item => item.Id.Equals(disc.Id)).Type);
            var discTracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={disc.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(discTracks);
            Assert.Single(discTracks.Items, item => item.Type == BaseItemKind.Audio);
            var filteredTracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&includeItemTypes=Audio", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(filteredTracks);
            Assert.Single(filteredTracks.Items, item => item.Type == BaseItemKind.Audio);
            var albumDetailsTracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&sortBy=ParentIndexNumber,IndexNumber,SortName&fields=ItemCounts,PrimaryImageAspectRatio,CanDelete,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(albumDetailsTracks);
            Assert.Single(albumDetailsTracks.Items, item => item.Type == BaseItemKind.Audio);
            var trackSortWithFolderFields = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&sortBy=ParentIndexNumber,IndexNumber,SortName&fields=Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(trackSortWithFolderFields);
            Assert.Single(trackSortWithFolderFields.Items, item => item.Type == BaseItemKind.Audio);

            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            Assert.Single(children.Items, item => item.Type == BaseItemKind.Audio);
            var bonus = Assert.Single(children.Items, item => item.Name == "Bonus Material");
            Assert.True(bonus.IsFolder);
            var bonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={bonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(bonusChildren);
            Assert.Single(bonusChildren.Items, item => item.Type == BaseItemKind.Audio);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task BookAndAudioBook_WithChildFolders_KeepAllPhysicalEntriesBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-book-subfolders-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var bookFolder = Path.Combine(categoryFolder, "Book Collection");
        var audioBookFolder = Path.Combine(categoryFolder, "Audio Collection");
        var dualFormatFolder = Path.Combine(categoryFolder, "Dual Format Title");
        Directory.CreateDirectory(Path.Combine(bookFolder, "Bonus Books"));
        Directory.CreateDirectory(Path.Combine(audioBookFolder, "Bonus Audio"));
        Directory.CreateDirectory(dualFormatFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(bookFolder, "First Book.pdf"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(bookFolder, "Bonus Books", "Second Book.pdf"),
            [],
            TestContext.Current.CancellationToken);
        var audiobookBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(audioBookFolder, "First Audio.m4b"),
            audiobookBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(audioBookFolder, "Bonus Audio", "Second Audio.m4b"),
            audiobookBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(dualFormatFolder, "Dual Format Title.epub"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(dualFormatFolder, "Dual Format Title.m4b"),
            audiobookBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin book subfolders " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=books&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=books", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var collections = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(collections);
            foreach (var (folderName, fileKind, bonusName) in new[]
            {
                ("Book Collection", BaseItemKind.Book, "Bonus Books"),
                ("Audio Collection", BaseItemKind.AudioBook, "Bonus Audio")
            })
            {
                var folder = Assert.Single(collections.Items, item => item.Name == folderName);
                Assert.Equal(BaseItemKind.Folder, folder.Type);
                var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={folder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(children);
                var mediaFile = Assert.Single(children.Items, item => item.Type == fileKind);
                if (fileKind == BaseItemKind.AudioBook)
                {
                    using var streamResponse = await client.GetAsync(
                        $"Audio/{mediaFile.Id}/stream?static=true", TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                    Assert.Equal(audiobookBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                }

                var bonus = Assert.Single(children.Items, item => item.Name == bonusName);
                Assert.Equal(BaseItemKind.Folder, bonus.Type);
                var bonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={bonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(bonusChildren);
                Assert.Single(bonusChildren.Items, item => item.Type == fileKind);
            }

            var dualFormat = Assert.Single(collections.Items, item => item.Name == "Dual Format Title");
            Assert.Equal(BaseItemKind.Folder, dualFormat.Type);
            var dualFormatItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={dualFormat.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(dualFormatItems);
            Assert.Single(dualFormatItems.Items, item => item.Type == BaseItemKind.Book);
            Assert.Single(dualFormatItems.Items, item => item.Type == BaseItemKind.AudioBook);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task LibraryViews_ListPhysicalGroupsBeforeMedia()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-folder-view-" + Guid.NewGuid().ToString("N"));
        var libraries = new[]
        {
            (CollectionType: "movies", ItemFolder: "Example Movie (2020)", FileName: "Example Movie (2020).mkv", ExpectedKind: BaseItemKind.Movie),
            (CollectionType: "tvshows", ItemFolder: "Example Show", FileName: "Example Show - S01E01.mkv", ExpectedKind: BaseItemKind.Series),
            (CollectionType: "books", ItemFolder: "Example Book", FileName: "Example Book.pdf", ExpectedKind: BaseItemKind.Book),
            (CollectionType: "books", ItemFolder: "Example Audio Book", FileName: "Example Audio Book.m4b", ExpectedKind: BaseItemKind.AudioBook),
            (CollectionType: "music", ItemFolder: "Example Album", FileName: "Track 01.mp3", ExpectedKind: BaseItemKind.MusicAlbum),
            (CollectionType: "homevideos", ItemFolder: "Home Clip", FileName: "Home Clip.mp4", ExpectedKind: BaseItemKind.Video),
            (CollectionType: "musicvideos", ItemFolder: "Music Clip", FileName: "Music Clip.mp4", ExpectedKind: BaseItemKind.MusicVideo)
        };
        var audiobookBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        foreach (var library in libraries)
        {
            var mediaFolder = Path.Combine(testRoot, library.ExpectedKind.ToString(), "Action", library.ItemFolder);
            Directory.CreateDirectory(mediaFolder);
            await File.WriteAllBytesAsync(
                Path.Combine(mediaFolder, library.FileName),
                library.ExpectedKind == BaseItemKind.AudioBook ? audiobookBytes : [],
                TestContext.Current.CancellationToken);
        }

        var looseEpisodeFolder = Path.Combine(testRoot, nameof(BaseItemKind.Series), "Drama");
        Directory.CreateDirectory(looseEpisodeFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(looseEpisodeFolder, "Another Show - S01E01.mkv"),
            [],
            TestContext.Current.CancellationToken);
        var snapshotBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(
            Path.Combine(testRoot, nameof(BaseItemKind.Video), "Action", "Home Clip", "Snapshot.png"),
            snapshotBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var createdLibraries = new List<string>();

        try
        {
            foreach (var library in libraries)
            {
                var libraryName = "Jigglefin" + library.ExpectedKind;
                var mediaRoot = Path.Combine(testRoot, library.ExpectedKind.ToString());
                var createUrl = $"Library/VirtualFolders?name={libraryName}&collectionType={library.CollectionType}&paths={Uri.EscapeDataString(mediaRoot)}&refreshLibrary=false";
                using var createResponse = await client.PostAsJsonAsync(
                    createUrl,
                    new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
                createdLibraries.Add(libraryName);
            }

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(
                new Progress<double>(),
                TestContext.Current.CancellationToken);

            var defaultViews = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(defaultViews);
            foreach (var library in libraries)
            {
                var defaultView = Assert.Single(defaultViews.Items, item => item.Name == "Jigglefin" + library.ExpectedKind);
                Assert.Equal(BaseItemKind.Folder, defaultView.Type);
                Assert.Null(defaultView.CollectionType);
                Assert.True(defaultView.IsFolder);
            }

            // Swiftfin 1.6.1 asks for its video-oriented supported kinds plus Folder and
            // CollectionFolder, even when browsing a folder-style music or book library.
            // Its paged Items request must keep physical children in the folder route.
            var userId = (await AuthHelper.GetUserDtoAsync(client)).Id;
            var folderBrowseTypes = string.Join(',', new[]
            {
                BaseItemKind.BoxSet, BaseItemKind.Movie, BaseItemKind.MusicVideo,
                BaseItemKind.Series, BaseItemKind.Video, BaseItemKind.Folder,
                BaseItemKind.CollectionFolder
            });
            var legacyViews = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Users/{userId}/Views", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(legacyViews);

            foreach (var library in libraries)
            {
                var libraryName = "Jigglefin" + library.ExpectedKind;
                var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"UserViews?presetViews={library.CollectionType}",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(views);
                var libraryView = Assert.Single(views.Items, item => item.Name == libraryName);
                Assert.Equal(BaseItemKind.Folder, libraryView.Type);
                Assert.Null(libraryView.CollectionType);
                Assert.True(libraryView.IsFolder);
                Assert.Equal(BaseItemKind.Folder, Assert.Single(legacyViews.Items, item => item.Name == libraryName).Type);

                var firstLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={libraryView.Id}",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(firstLevel);
                Assert.True(
                    firstLevel.Items.Any(item => item.Name == "Action"),
                    $"No Action folder for {library.CollectionType}; first level: {string.Join(", ", firstLevel.Items.Select(item => item.Name + ":" + item.Type))}");
                var actionFolder = Assert.Single(firstLevel.Items, item => item.Name == "Action");
                Assert.Equal(BaseItemKind.Folder, actionFolder.Type);
                // Android TV's folder grid sends a parent-only items request with
                // these fields and uses the returned folder path and child count.
                var androidTvFirstLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={libraryView.Id}&fields=CanDelete,ChildCount,DateCreated,Genres,MediaSourceCount,MediaSources,MediaStreams,Overview,Path,PrimaryImageAspectRatio",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(androidTvFirstLevel);
                var androidTvAction = Assert.Single(androidTvFirstLevel.Items, item => item.Id.Equals(actionFolder.Id));
                Assert.Equal(BaseItemKind.Folder, androidTvAction.Type);
                Assert.True(androidTvAction.IsFolder);
                Assert.Equal(Path.Combine(testRoot, library.ExpectedKind.ToString(), "Action"), androidTvAction.Path);
                Assert.True(androidTvAction.ChildCount > 0);
                var nativeFirstLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Users/{userId}/Items?parentId={libraryView.Id}&includeItemTypes={folderBrowseTypes}&sortBy=SortName&sortOrder=Ascending",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(nativeFirstLevel);
                Assert.Equal(firstLevel.Items.Count, nativeFirstLevel.Items.Count);
                Assert.All(nativeFirstLevel.Items, item => Assert.Contains(firstLevel.Items, child => child.Id.Equals(item.Id)));
                Assert.Equal(BaseItemKind.Folder, Assert.Single(nativeFirstLevel.Items, item => item.Id.Equals(actionFolder.Id)).Type);

                var swiftfinPageItems = new List<Guid>();
                for (var pageIndex = 0; pageIndex < firstLevel.Items.Count; pageIndex++)
                {
                    var swiftfinPage = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?userId={userId}&parentId={libraryView.Id}&includeItemTypes={folderBrowseTypes}&sortBy=SortName&sortOrder=Ascending&fields=MediaSources,ParentId,ChannelInfo&enableUserData=true&startIndex={pageIndex}&limit=1",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(swiftfinPage);
                    Assert.Equal(firstLevel.Items.Count, swiftfinPage.TotalRecordCount);
                    swiftfinPageItems.Add(Assert.Single(swiftfinPage.Items).Id);
                }

                Assert.Equal(firstLevel.Items.Count, swiftfinPageItems.Distinct().Count());
                Assert.All(swiftfinPageItems, id => Assert.Contains(firstLevel.Items, child => child.Id.Equals(id)));

                if (library.ExpectedKind == BaseItemKind.Series)
                {
                    var dramaFolder = Assert.Single(firstLevel.Items, item => item.Name == "Drama");
                    Assert.Equal(BaseItemKind.Folder, dramaFolder.Type);
                    var looseEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?parentId={dramaFolder.Id}",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(looseEpisodes);
                    Assert.Single(looseEpisodes.Items, item => item.Type == BaseItemKind.Episode);
                }

                var secondLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={actionFolder.Id}",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(secondLevel);
                var mediaEntry = Assert.Single(secondLevel.Items, item => item.Name.Contains(library.ItemFolder.Split('(')[0].Trim(), StringComparison.Ordinal));
                if (library.ExpectedKind == BaseItemKind.Movie)
                {
                    var typedMovies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?parentId={libraryView.Id}&includeItemTypes=Movie&recursive=true",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(typedMovies);
                    Assert.Equal(mediaEntry.Id, Assert.Single(typedMovies.Items).Id);

                    var explicitlyRecursiveFolders = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?parentId={libraryView.Id}&includeItemTypes={folderBrowseTypes}&recursive=true",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(explicitlyRecursiveFolders);
                    Assert.Contains(explicitlyRecursiveFolders.Items, item => item.Id.Equals(mediaEntry.Id));
                }

                if (library.ExpectedKind is BaseItemKind.Series or BaseItemKind.MusicAlbum)
                {
                    var nativeSecondLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Users/{userId}/Items?parentId={actionFolder.Id}&includeItemTypes={folderBrowseTypes}&sortBy=SortName&sortOrder=Ascending",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(nativeSecondLevel);
                    Assert.Equal(BaseItemKind.Folder, Assert.Single(nativeSecondLevel.Items, item => item.Id.Equals(mediaEntry.Id)).Type);
                    var swiftfinSecondLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?userId={userId}&parentId={actionFolder.Id}&includeItemTypes={folderBrowseTypes}&sortBy=SortName&sortOrder=Ascending&fields=MediaSources,ParentId,ChannelInfo&enableUserData=true&startIndex=0&limit=1",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(swiftfinSecondLevel);
                    Assert.Equal(BaseItemKind.Folder, Assert.Single(swiftfinSecondLevel.Items, item => item.Id.Equals(mediaEntry.Id)).Type);
                    var itemDetails = await client.GetFromJsonAsync<BaseItemDto>(
                        $"Items/{mediaEntry.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                    Assert.Equal(library.ExpectedKind, itemDetails?.Type);
                }

                if ((library.CollectionType is "homevideos" or "musicvideos")
                    && (mediaEntry.Type is BaseItemKind.Folder or BaseItemKind.PhotoAlbum))
                {
                    var files = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?parentId={mediaEntry.Id}",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(files);
                    Assert.Single(files.Items, item => item.Type == library.ExpectedKind);
                    if (library.CollectionType == "homevideos")
                    {
                        var photo = Assert.Single(files.Items, item => item.Type == BaseItemKind.Photo);
                        using var imageResponse = await client.GetAsync(
                            $"Items/{photo.Id}/Images/Primary", TestContext.Current.CancellationToken);
                        Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
                        Assert.Equal("image/png", imageResponse.Content.Headers.ContentType?.MediaType);
                        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                        Assert.True(imageBytes.Length > 8);
                        Assert.True(imageBytes.AsSpan(0, 8).SequenceEqual(snapshotBytes.AsSpan(0, 8)));
                    }
                }
                else
                {
                    Assert.Equal(library.ExpectedKind, mediaEntry.Type);
                }

                if (library.ExpectedKind == BaseItemKind.AudioBook)
                {
                    using var streamResponse = await client.GetAsync(
                        $"Audio/{mediaEntry.Id}/stream?static=true",
                        TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                    Assert.Equal(audiobookBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

                    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"Audio/{mediaEntry.Id}/stream?static=true");
                    rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);
                    using var rangeResponse = await client.SendAsync(rangeRequest, TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
                    Assert.Equal(audiobookBytes[100..200], await rangeResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                }
            }
        }
        finally
        {
            foreach (var libraryName in createdLibraries)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={libraryName}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }
}
