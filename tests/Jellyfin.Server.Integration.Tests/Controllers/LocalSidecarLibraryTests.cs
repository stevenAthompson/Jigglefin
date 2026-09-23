using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LocalSidecarLibraryTests
{
    [Theory]
    [InlineData("movie.xml", "<Item><LocalTitle>Unclosed")]
    [InlineData("movie.nfo", "<movie><title>Unclosed")]
    public async Task MalformedMovieSidecar_DoesNotHidePhysicalMovie(string sidecarFileName, string sidecarContents)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-malformed-movie-xml-" + Guid.NewGuid().ToString("N"));
        var movieFolder = Path.Combine(testRoot, "Action", "Broken Sidecar Movie");
        Directory.CreateDirectory(movieFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(movieFolder, "Broken Sidecar Movie.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(movieFolder, sidecarFileName),
            sidecarContents,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin malformed XML " + Guid.NewGuid().ToString("N");
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
            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            var movie = Assert.Single(movies.Items);
            Assert.Equal(BaseItemKind.Movie, movie.Type);
            Assert.Equal("Broken Sidecar Movie", movie.Name);
            using var streamResponse = await client.GetAsync(
                $"Videos/{movie.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
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
    public async Task BookOpf_UsesSpecificSidecarsWithoutLeakingSharedMetadataAcrossMixedFolder()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-book-opf-" + Guid.NewGuid().ToString("N"));
        var authorFolder = Path.Combine(testRoot, "Example Author");
        var mixedFolder = Path.Combine(authorFolder, "Mixed Shelf");
        var dedicatedFolder = Path.Combine(authorFolder, "Dedicated Book");
        var alternateFolder = Path.Combine(authorFolder, "Archive Shelf");
        Directory.CreateDirectory(mixedFolder);
        Directory.CreateDirectory(dedicatedFolder);
        Directory.CreateDirectory(alternateFolder);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "First Book.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "Second Book.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(dedicatedFolder, "Dedicated Book.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(alternateFolder, "Third Book.pdf"), [], TestContext.Current.CancellationToken);

        static string Opf(string title, string overview)
            => $"<package xmlns='http://www.idpf.org/2007/opf'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>{title}</dc:title><dc:description>{overview}</dc:description></metadata></package>";

        await File.WriteAllTextAsync(
            Path.Combine(mixedFolder, "metadata.opf"), Opf("Wrong Shared Title", "Must not apply to either mixed book."), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(mixedFolder, "content.opf"), Opf("Wrong Standard Title", "Also ambiguous in a mixed folder."), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(mixedFolder, "First Book.opf"), Opf("Specific Local Title", "First book only."), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(dedicatedFolder, "metadata.opf"), Opf("Dedicated Local Title", "Dedicated book only."), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(alternateFolder, "metadata.opf"), Opf("Archive Local Title", "One book in this folder."), TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin book OPF test " + Guid.NewGuid().ToString("N");
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
            var shelves = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={author.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(shelves);
            var mixed = Assert.Single(shelves.Items, item => item.Name == "Mixed Shelf");
            Assert.Equal(BaseItemKind.Folder, mixed.Type);
            var dedicated = Assert.Single(shelves.Items, item => item.Name == "Dedicated Local Title");
            Assert.Equal(BaseItemKind.Book, dedicated.Type);
            var archive = Assert.Single(shelves.Items, item => item.Name == "Archive Shelf");
            Assert.Equal(BaseItemKind.Folder, archive.Type);
            var archiveBooks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={archive.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(archiveBooks);
            Assert.Single(archiveBooks.Items, item => item.Name == "Archive Local Title" && item.Type == BaseItemKind.Book);

            var books = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(books);
            Assert.Equal(2, books.Items.Count);
            var first = Assert.Single(books.Items, item => item.Name == "Specific Local Title");
            Assert.Equal(BaseItemKind.Book, first.Type);
            Assert.Single(books.Items, item => item.Name == "Second Book" && item.Type == BaseItemKind.Book);
            var firstDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{first.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("First book only.", firstDetails?.Overview);

            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var refreshedBooks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(refreshedBooks);
            Assert.Single(refreshedBooks.Items, item => item.Name == "Specific Local Title");
            Assert.Single(refreshedBooks.Items, item => item.Name == "Second Book");

            File.Delete(Path.Combine(mixedFolder, "First Book.opf"));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var withoutSpecificOpf = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(withoutSpecificOpf);
            Assert.Single(withoutSpecificOpf.Items, item => item.Name == "First Book" && item.Type == BaseItemKind.Book);
            Assert.Single(withoutSpecificOpf.Items, item => item.Name == "Second Book" && item.Type == BaseItemKind.Book);
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
    public async Task BookXml_UsesSpecificOrUnambiguousSidecarsAfterOpf()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-book-xml-" + Guid.NewGuid().ToString("N"));
        var authorFolder = Path.Combine(testRoot, "Example Author");
        var mixedFolder = Path.Combine(authorFolder, "Mixed Shelf");
        var dedicatedFolder = Path.Combine(authorFolder, "Dedicated Book");
        var opfFolder = Path.Combine(authorFolder, "With OPF");
        Directory.CreateDirectory(mixedFolder);
        Directory.CreateDirectory(dedicatedFolder);
        Directory.CreateDirectory(opfFolder);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "First Book.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "Second Book.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(dedicatedFolder, "Dedicated Book.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(opfFolder, "With OPF.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(mixedFolder, "First Book.xml"),
            "<Item><LocalTitle>Specific XML Title</LocalTitle><ProductionYear>2021</ProductionYear><Overview>First book only.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(mixedFolder, "book.xml"),
            "<Item><LocalTitle>Wrong Shared Title</LocalTitle></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(dedicatedFolder, "book.xml"),
            "<Item><LocalTitle>Dedicated XML Title</LocalTitle><Overview>One book in this folder.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(opfFolder, "With OPF.xml"),
            "<Item><LocalTitle>Secondary XML Title</LocalTitle></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(opfFolder, "With OPF.opf"),
            "<package xmlns='http://www.idpf.org/2007/opf'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Preferred OPF Title</dc:title></metadata></package>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin book XML test " + Guid.NewGuid().ToString("N");
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
            var shelves = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={author.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(shelves);
            var mixed = Assert.Single(shelves.Items, item => item.Name == "Mixed Shelf");
            var dedicated = Assert.Single(shelves.Items, item => item.Name == "Dedicated XML Title");
            Assert.Equal(BaseItemKind.Book, dedicated.Type);
            Assert.Single(shelves.Items, item => item.Name == "Preferred OPF Title" && item.Type == BaseItemKind.Book);

            var mixedBooks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(mixedBooks);
            var first = Assert.Single(mixedBooks.Items, item => item.Name == "Specific XML Title");
            Assert.Equal(2021, first.ProductionYear);
            Assert.Single(mixedBooks.Items, item => item.Name == "Second Book" && item.Type == BaseItemKind.Book);
            var firstDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{first.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("First book only.", firstDetails?.Overview);
            var dedicatedDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{dedicated.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("One book in this folder.", dedicatedDetails?.Overview);
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
    public async Task AudioBookXml_UsesSpecificOrUnambiguousSidecarsWithoutLeakingMetadata()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-audiobook-xml-" + Guid.NewGuid().ToString("N"));
        var category = Path.Combine(testRoot, "Listening");
        var mixedFolder = Path.Combine(category, "Mixed Shelf");
        var dedicatedFolder = Path.Combine(category, "Dedicated Audio");
        var alternateFolder = Path.Combine(category, "Archive Shelf");
        var crossFormatFolder = Path.Combine(category, "Cross Format");
        foreach (var folder in new[] { mixedFolder, dedicatedFolder, alternateFolder, crossFormatFolder })
        {
            Directory.CreateDirectory(folder);
        }

        var sample = Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b");
        File.Copy(sample, Path.Combine(mixedFolder, "First Audio.m4b"));
        File.Copy(sample, Path.Combine(mixedFolder, "Second Audio.m4b"));
        File.Copy(sample, Path.Combine(dedicatedFolder, "Dedicated Audio.m4b"));
        File.Copy(sample, Path.Combine(alternateFolder, "Third Audio.m4b"));
        File.Copy(sample, Path.Combine(crossFormatFolder, "Fourth Audio.m4b"));
        await File.WriteAllBytesAsync(Path.Combine(crossFormatFolder, "Fourth Book.pdf"), [], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(mixedFolder, "First Audio.xml"),
            "<Item><LocalTitle>Specific Audio Title</LocalTitle><ProductionYear>2022</ProductionYear><Overview>First audio only.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(mixedFolder, "audiobook.xml"),
            "<Item><LocalTitle>Wrong Shared Title</LocalTitle></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(dedicatedFolder, "book.xml"),
            "<Item><LocalTitle>Dedicated Audio Title</LocalTitle><Overview>One audiobook in this folder.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(alternateFolder, "audiobook.xml"),
            "<Item><LocalTitle>Archive Audio Title</LocalTitle><Overview>One audiobook in this archive.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(crossFormatFolder, "book.xml"),
            "<Item><LocalTitle>Wrong Cross Format Title</LocalTitle></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(crossFormatFolder, "metadata.opf"),
            "<package xmlns='http://www.idpf.org/2007/opf'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Wrong Cross Format OPF Title</dc:title></metadata></package>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        var accessToken = await AuthHelper.CompleteStartupAsync(client);
        client.DefaultRequestHeaders.AddAuthHeader(accessToken);
        var libraryName = "Jigglefin audiobook XML test " + Guid.NewGuid().ToString("N");
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
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var listening = Assert.Single(groups.Items, item => item.Name == "Listening");
            var shelves = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={listening.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(shelves);
            var mixed = Assert.Single(shelves.Items, item => item.Name == "Mixed Shelf");
            var dedicated = Assert.Single(shelves.Items, item => item.Name == "Dedicated Audio Title");
            Assert.Equal(BaseItemKind.AudioBook, dedicated.Type);
            var archive = Assert.Single(shelves.Items, item => item.Name == "Archive Shelf");
            var crossFormat = Assert.Single(shelves.Items, item => item.Name == "Cross Format");

            var mixedItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(mixedItems);
            var first = Assert.Single(mixedItems.Items, item => item.Name == "Specific Audio Title");
            Assert.Equal(BaseItemKind.AudioBook, first.Type);
            Assert.Equal(2022, first.ProductionYear);
            Assert.Single(mixedItems.Items, item => item.Name == "Second Audio" && item.Type == BaseItemKind.AudioBook);
            var firstDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{first.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("First audio only.", firstDetails?.Overview);
            Assert.Equal(BaseItemKind.AudioBook, firstDetails?.Type);

            // The Android TV client lacks an AudioBook playback action. The API
            // presents the same item as playable audio only for that client.
            var androidTvAuthorization = $"MediaBrowser Client=\"Jellyfin Android TV\", DeviceId=\"jigglefin-audiobook-test\", Device=\"Android TV\", Version=\"0.19.10\", Token={accessToken}";
            using var androidTvRequest = new HttpRequestMessage(HttpMethod.Get, $"Items/{first.Id}");
            androidTvRequest.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, androidTvAuthorization);
            using var androidTvResponse = await client.SendAsync(androidTvRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, androidTvResponse.StatusCode);
            var androidTvDetails = await androidTvResponse.Content.ReadFromJsonAsync<BaseItemDto>(
                JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(BaseItemKind.Audio, androidTvDetails?.Type);
            Assert.Equal(first.Id, androidTvDetails?.Id);
            Assert.Equal("First audio only.", androidTvDetails?.Overview);
            using var androidTvListRequest = new HttpRequestMessage(HttpMethod.Get, $"Items?parentId={mixed.Id}");
            androidTvListRequest.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, androidTvAuthorization);
            using var androidTvListResponse = await client.SendAsync(androidTvListRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, androidTvListResponse.StatusCode);
            var androidTvItems = await androidTvListResponse.Content.ReadFromJsonAsync<QueryResult<BaseItemDto>>(
                JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(androidTvItems);
            Assert.Equal(2, androidTvItems.Items.Count);
            Assert.All(androidTvItems.Items, item => Assert.Equal(BaseItemKind.Audio, item.Type));
            var dedicatedDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{dedicated.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("One audiobook in this folder.", dedicatedDetails?.Overview);

            var archiveItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={archive.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(archiveItems);
            Assert.Single(archiveItems.Items, item => item.Name == "Archive Audio Title" && item.Type == BaseItemKind.AudioBook);
            var crossFormatItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={crossFormat.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(crossFormatItems);
            Assert.Single(crossFormatItems.Items, item => item.Name == "Fourth Audio" && item.Type == BaseItemKind.AudioBook);
            Assert.Single(crossFormatItems.Items, item => item.Name == "Fourth Book" && item.Type == BaseItemKind.Book);
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
    public async Task MovieLocalArtwork_IsExposedThroughStandardImageApi()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-movie-artwork-" + Guid.NewGuid().ToString("N"));
        var movieFolder = Path.Combine(testRoot, "Action", "Artwork Movie");
        Directory.CreateDirectory(movieFolder);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            Path.Combine(movieFolder, "Artwork Movie.mp4"));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6ZAy0AAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(Path.Combine(movieFolder, "poster.png"), png, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(movieFolder, "fanart.png"), png, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin movie artwork test " + Guid.NewGuid().ToString("N");
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
                "UserViews", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var categories = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(categories);
            var action = Assert.Single(categories.Items, item => item.Name == "Action");
            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            var movie = Assert.Single(movies.Items);
            Assert.Equal(BaseItemKind.Movie, movie.Type);

            var details = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(details);
            Assert.NotNull(details.ImageTags);
            Assert.True(details.ImageTags.ContainsKey(ImageType.Primary));
            Assert.NotEmpty(details.BackdropImageTags);
            var storedMovie = libraryManager.GetItemById<BaseItem>(movie.Id);
            Assert.NotNull(storedMovie);
            Assert.Equal("poster.png", Path.GetFileName(storedMovie.GetImageInfo(ImageType.Primary, 0)?.Path));
            Assert.Equal("fanart.png", Path.GetFileName(storedMovie.GetImageInfo(ImageType.Backdrop, 0)?.Path));
            using var imageResponse = await client.GetAsync(
                $"Items/{movie.Id}/Images/Primary", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
            Assert.NotEmpty(await imageResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            var originalPrimaryTag = details.ImageTags[ImageType.Primary];
            var posterPath = Path.Combine(movieFolder, "poster.png");
            File.SetLastWriteTimeUtc(posterPath, storedMovie.DateLastSaved.AddSeconds(2));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var updatedArtwork = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(updatedArtwork);
            Assert.NotNull(updatedArtwork.ImageTags);
            Assert.NotEqual(originalPrimaryTag, updatedArtwork.ImageTags[ImageType.Primary]);

            File.Delete(posterPath);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var withoutPoster = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(withoutPoster);
            Assert.False(withoutPoster.ImageTags?.ContainsKey(ImageType.Primary));
            Assert.NotEmpty(withoutPoster.BackdropImageTags);
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
    [InlineData("homevideos", BaseItemKind.Video)]
    [InlineData("musicvideos", BaseItemKind.MusicVideo)]
    public async Task VideoXml_UsesBasenameSidecarsAndLeavesNfoInControl(string collectionType, BaseItemKind expectedKind)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-video-xml-" + Guid.NewGuid().ToString("N"));
        var category = Path.Combine(testRoot, "Category");
        Directory.CreateDirectory(category);
        var sample = Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4");
        foreach (var name in new[] { "XML Clip", "NFO Clip", "Plain Clip" })
        {
            File.Copy(sample, Path.Combine(category, name + ".mp4"));
        }

        await File.WriteAllTextAsync(
            Path.Combine(category, "XML Clip.xml"),
            "<Item><LocalTitle>Local XML Clip</LocalTitle><ProductionYear>2024</ProductionYear><Overview>Local XML overview.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(category, "NFO Clip.xml"),
            "<Item><LocalTitle>Wrong XML Title</LocalTitle><ProductionYear>2001</ProductionYear><Overview>Wrong XML overview.</Overview></Item>",
            TestContext.Current.CancellationToken);
        var nfoRoot = expectedKind == BaseItemKind.MusicVideo ? "musicvideo" : "movie";
        await File.WriteAllTextAsync(
            Path.Combine(category, "NFO Clip.nfo"),
            $"<{nfoRoot}><title>Local NFO Clip</title><year>2025</year><plot>Local NFO overview.</plot></{nfoRoot}>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin video XML test " + Guid.NewGuid().ToString("N");
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
                "UserViews", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var categoryDto = Assert.Single(groups.Items, item => item.Name == "Category");
            Assert.True(categoryDto.IsFolder);

            var videos = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={categoryDto.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(videos);
            Assert.Equal(3, videos.Items.Count);
            var xml = Assert.Single(videos.Items, item => item.Name == "Local XML Clip");
            Assert.Equal(expectedKind, xml.Type);
            Assert.Equal(2024, xml.ProductionYear);
            var xmlDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{xml.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Local XML overview.", xmlDetails?.Overview);
            var nfo = Assert.Single(videos.Items, item => item.Name == "Local NFO Clip");
            Assert.Equal(expectedKind, nfo.Type);
            Assert.Equal(2025, nfo.ProductionYear);
            var nfoDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{nfo.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Local NFO overview.", nfoDetails?.Overview);
            Assert.Single(videos.Items, item => item.Name == "Plain Clip" && item.Type == expectedKind);
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
    public async Task EmbyMusicXml_ProvidesArtistAndAlbumMetadataThroughPhysicalFolders()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-xml-" + Guid.NewGuid().ToString("N"));
        var artistFolder = Path.Combine(testRoot, "Genres", "Physical Artist");
        var albumFolder = Path.Combine(artistFolder, "Physical Album");
        Directory.CreateDirectory(albumFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(albumFolder, "Track 01.mp3"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(artistFolder, "artist.xml"),
            "<Artist><LocalTitle>Local XML Artist</LocalTitle><Overview>Artist from XML.</Overview></Artist>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(albumFolder, "album.xml"),
            "<Item><LocalTitle>Local XML Album</LocalTitle><ProductionYear>2023</ProductionYear><Overview>Album from XML.</Overview></Item>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music XML test " + Guid.NewGuid().ToString("N");
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
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var genres = Assert.Single(groups.Items, item => item.Name == "Genres");
            Assert.Equal(BaseItemKind.Folder, genres.Type);
            var artists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(artists);
            var artist = Assert.Single(artists.Items, item => item.Name == "Local XML Artist");
            Assert.Equal(BaseItemKind.MusicArtist, artist.Type);
            var albums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(albums);
            var album = Assert.Single(albums.Items, item => item.Name == "Local XML Album");
            Assert.Equal(BaseItemKind.MusicAlbum, album.Type);
            Assert.Equal(2023, album.ProductionYear);
            var artistDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Artist from XML.", artistDetails?.Overview);
            var albumDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{album.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Album from XML.", albumDetails?.Overview);
            var tracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={album.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(tracks);
            Assert.Single(tracks.Items, item => item.Type == BaseItemKind.Audio);

            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var refreshedAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(refreshedAlbums);
            Assert.Single(refreshedAlbums.Items, item => item.Name == "Local XML Album");
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
    public async Task MusicNfo_ProvidesLocalArtistAndAlbumMetadataThroughPhysicalFolders()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-nfo-" + Guid.NewGuid().ToString("N"));
        var artistFolder = Path.Combine(testRoot, "Genres", "Physical Artist");
        var albumFolder = Path.Combine(artistFolder, "Physical Album");
        Directory.CreateDirectory(albumFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(albumFolder, "Track 01.mp3"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(artistFolder, "artist.nfo"),
            "<artist><name>Local NFO Artist</name><genre>Jazz</genre></artist>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(artistFolder, "artist.xml"),
            "<Artist><LocalTitle>Wrong XML Artist</LocalTitle></Artist>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(albumFolder, "album.nfo"),
            "<album><title>Local NFO Album</title><year>2022</year><plot>Local album description.</plot></album>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(albumFolder, "album.xml"),
            "<Item><LocalTitle>Wrong XML Album</LocalTitle><ProductionYear>2001</ProductionYear></Item>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music NFO test " + Guid.NewGuid().ToString("N");
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
            Assert.Equal(BaseItemKind.Folder, library.Type);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var genres = Assert.Single(rootItems.Items, item => item.Name == "Genres");
            Assert.Equal(BaseItemKind.Folder, genres.Type);
            var artists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(artists);
            var artist = Assert.Single(artists.Items, item => item.Name == "Local NFO Artist");
            Assert.Equal(BaseItemKind.MusicArtist, artist.Type);
            var folderArtists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}&sortBy=IsFolder,SortName",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(folderArtists);
            var folderArtist = Assert.Single(folderArtists.Items, item => item.Id.Equals(artist.Id));
            Assert.Equal(BaseItemKind.Folder, folderArtist.Type);
            Assert.True(folderArtist.IsFolder);
            var nameSortedArtists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}&sortBy=SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nameSortedArtists);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(nameSortedArtists.Items).Type);
            var artistDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(BaseItemKind.MusicArtist, artistDetails?.Type);
            var albums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(albums);
            var album = Assert.Single(albums.Items, item => item.Name == "Local NFO Album");
            Assert.Equal(BaseItemKind.MusicAlbum, album.Type);
            var folderAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}&sortBy=IsFolder,SortName",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(folderAlbums);
            var folderAlbum = Assert.Single(folderAlbums.Items, item => item.Id.Equals(album.Id));
            Assert.Equal(BaseItemKind.Folder, folderAlbum.Type);
            Assert.True(folderAlbum.IsFolder);
            var nameSortedAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}&sortBy=SortName&fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(nameSortedAlbums);
            Assert.Equal(BaseItemKind.Folder, Assert.Single(nameSortedAlbums.Items).Type);
            var artistDetailsAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}&sortBy=PremiereDate,ProductionYear,SortName&fields=ItemCounts,PrimaryImageAspectRatio,CanDelete,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(artistDetailsAlbums);
            Assert.Equal(BaseItemKind.MusicAlbum, Assert.Single(artistDetailsAlbums.Items).Type);
            var artistSortWithFolderFields = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}&sortBy=PremiereDate,ProductionYear,SortName&fields=Path,ChildCount,MediaSourceCount",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(artistSortWithFolderFields);
            Assert.Equal(BaseItemKind.MusicAlbum, Assert.Single(artistSortWithFolderFields.Items).Type);
            Assert.Equal(2022, album.ProductionYear);
            var details = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{album.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(BaseItemKind.MusicAlbum, details?.Type);
            Assert.Equal("Local album description.", details?.Overview);
            var tracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={album.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(tracks);
            Assert.Single(tracks.Items, item => item.Type == BaseItemKind.Audio);

            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var refreshedAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(refreshedAlbums);
            Assert.Single(refreshedAlbums.Items, item => item.Name == "Local NFO Album");

            File.Delete(Path.Combine(artistFolder, "artist.nfo"));
            File.Delete(Path.Combine(albumFolder, "album.nfo"));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var xmlArtists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(xmlArtists);
            Assert.Single(xmlArtists.Items, item => item.Id.Equals(artist.Id) && item.Name == "Wrong XML Artist");
            var xmlAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(xmlAlbums);
            Assert.Single(xmlAlbums.Items, item => item.Id.Equals(album.Id) && item.Name == "Wrong XML Album");

            var artistXmlPath = Path.Combine(artistFolder, "artist.xml");
            var albumXmlPath = Path.Combine(albumFolder, "album.xml");
            await File.WriteAllTextAsync(
                artistXmlPath,
                "<Artist><LocalTitle>Updated XML Artist</LocalTitle></Artist>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                albumXmlPath,
                "<Item><LocalTitle>Updated XML Album</LocalTitle><ProductionYear>2003</ProductionYear></Item>",
                TestContext.Current.CancellationToken);
            var updatedTimestamp = DateTime.UtcNow.AddMinutes(1);
            File.SetLastWriteTimeUtc(artistXmlPath, updatedTimestamp);
            File.SetLastWriteTimeUtc(albumXmlPath, updatedTimestamp);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var updatedArtists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(updatedArtists);
            Assert.Single(updatedArtists.Items, item => item.Id.Equals(artist.Id) && item.Name == "Updated XML Artist");
            var updatedAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(updatedAlbums);
            Assert.Single(updatedAlbums.Items, item => item.Id.Equals(album.Id) && item.Name == "Updated XML Album");

            File.Delete(artistXmlPath);
            File.Delete(albumXmlPath);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var fallbackArtists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(fallbackArtists);
            var fallbackArtist = Assert.Single(fallbackArtists.Items, item => item.Name == "Physical Artist");
            Assert.Equal(BaseItemKind.Folder, fallbackArtist.Type);
            var fallbackAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={fallbackArtist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(fallbackAlbums);
            Assert.Single(fallbackAlbums.Items, item => item.Name == "Physical Album" && item.Type == BaseItemKind.MusicAlbum);
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
    public async Task EmbySeriesXml_ProvidesClientMetadataWithoutHidingPhysicalGroups()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-series-xml-" + Guid.NewGuid().ToString("N"));
        var seriesFolder = Path.Combine(testRoot, "Drama", "Example Show");
        var seasonFolder = Path.Combine(seriesFolder, "Season 1");
        Directory.CreateDirectory(seasonFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(seasonFolder, "Example Show - S01E01.mp4"),
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(seasonFolder, "Example Show - S01E01.xml"),
            "<Item><LocalTitle>Local XML Episode</LocalTitle><Overview>From the episode XML.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(seasonFolder, "season.xml"),
            "<Item><LocalTitle>Local XML Season</LocalTitle><Overview>From the season XML.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(seriesFolder, "series.xml"),
            "<Series><LocalTitle>Local XML Series</LocalTitle><ProductionYear>2020</ProductionYear><Overview>From the series XML.</Overview></Series>",
            TestContext.Current.CancellationToken);
        var precedenceFolder = Path.Combine(testRoot, "Drama", "Both Sources");
        var precedenceSeasonFolder = Path.Combine(precedenceFolder, "Season 1");
        Directory.CreateDirectory(precedenceSeasonFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(precedenceSeasonFolder, "Both Sources - S01E01.mp4"),
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "series.xml"),
            "<Series><LocalTitle>Secondary Series XML</LocalTitle></Series>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "tvshow.nfo"),
            "<tvshow><title>Preferred Series NFO</title><namedseason number='1'>Parent Season Fallback</namedseason></tvshow>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceSeasonFolder, "Both Sources - S01E01.xml"),
            "<Item><LocalTitle>Secondary Episode XML</LocalTitle></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceSeasonFolder, "Both Sources - S01E01.nfo"),
            "<episodedetails><title>Preferred Episode NFO</title></episodedetails>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceSeasonFolder, "season.xml"),
            "<Item><LocalTitle>Secondary Season XML</LocalTitle><Overview>From secondary XML.</Overview></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceSeasonFolder, "season.nfo"),
            "<season><title>Preferred Season NFO</title><plot>From preferred NFO.</plot></season>",
            TestContext.Current.CancellationToken);
        var markerFolder = Path.Combine(testRoot, "Drama", "XML Marker Show");
        var bonusFolder = Path.Combine(markerFolder, "Bonus Collection");
        Directory.CreateDirectory(bonusFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(bonusFolder, "Bonus Clip.mp4"),
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(markerFolder, "series.xml"),
            "<Series><LocalTitle>Explicit XML Marker Show</LocalTitle><Overview>From the marker XML.</Overview></Series>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin series XML test " + Guid.NewGuid().ToString("N");
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
            Assert.Equal(BaseItemKind.Folder, library.Type);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var drama = Assert.Single(groups.Items, item => item.Name == "Drama");
            Assert.Equal(BaseItemKind.Folder, drama.Type);

            var seriesItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seriesItems);
            Assert.Equal(3, seriesItems.Items.Count);
            var series = Assert.Single(seriesItems.Items, item => item.Name == "Local XML Series");
            var preferredNfo = Assert.Single(seriesItems.Items, item => item.Name == "Preferred Series NFO");
            var marker = Assert.Single(seriesItems.Items, item => item.Name == "Explicit XML Marker Show");
            Assert.Equal(BaseItemKind.Series, series.Type);
            Assert.Equal(BaseItemKind.Series, preferredNfo.Type);
            Assert.Equal(BaseItemKind.Series, marker.Type);
            Assert.Equal(2020, series.ProductionYear);

            var seasons = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasons);
            var xmlSeason = Assert.Single(seasons.Items, item => item.Name == "Local XML Season");
            Assert.Equal(BaseItemKind.Season, xmlSeason.Type);
            var seasonDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{xmlSeason.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("From the season XML.", seasonDetails?.Overview);

            var precedenceSeasons = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={preferredNfo.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(precedenceSeasons);
            var nfoSeason = Assert.Single(precedenceSeasons.Items, item => item.Name == "Preferred Season NFO");
            Assert.Equal(BaseItemKind.Season, nfoSeason.Type);
            var nfoSeasonDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{nfoSeason.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("From preferred NFO.", nfoSeasonDetails?.Overview);
            var storedNfoSeason = libraryManager.GetItemById(nfoSeason.Id);
            Assert.NotNull(storedNfoSeason);
            Assert.Contains(ItemInfo.LocalNfoPathProviderId, storedNfoSeason.ProviderIds.Keys);
            Assert.Equal(Path.Combine(precedenceSeasonFolder, "season.nfo"), new ItemInfo(storedNfoSeason).PreviousLocalNfoPath);

            var details = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("From the series XML.", details?.Overview);
            var episodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Shows/{series.Id}/Episodes", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(episodes);
            var xmlEpisode = Assert.Single(episodes.Items, item => item.Name == "Local XML Episode");
            Assert.Equal(BaseItemKind.Episode, xmlEpisode.Type);
            var episodeDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{xmlEpisode.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("From the episode XML.", episodeDetails?.Overview);
            var preferredEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Shows/{preferredNfo.Id}/Episodes", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(preferredEpisodes);
            Assert.Single(preferredEpisodes.Items, item => item.Name == "Preferred Episode NFO");
            var markerDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{marker.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("From the marker XML.", markerDetails?.Overview);
            var markerChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={marker.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(markerChildren);
            var bonus = Assert.Single(markerChildren.Items, item => item.Name == "Bonus Collection");
            Assert.Equal(BaseItemKind.Folder, bonus.Type);
            var bonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={bonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(bonusChildren);
            Assert.Single(bonusChildren.Items, item => item.Type == BaseItemKind.Episode);

            var seasonXmlPath = Path.Combine(seasonFolder, "season.xml");
            await File.WriteAllTextAsync(
                seasonXmlPath,
                "<Item><LocalTitle>Updated XML Season</LocalTitle><Overview>Updated season XML.</Overview></Item>",
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(seasonXmlPath, DateTime.UtcNow.AddMinutes(1));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var updatedSeason = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{xmlSeason.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Updated XML Season", updatedSeason?.Name);
            Assert.Equal("Updated season XML.", updatedSeason?.Overview);
            var storedNfoSeasonAfterScan = libraryManager.GetItemById(nfoSeason.Id);
            Assert.NotNull(storedNfoSeasonAfterScan);
            Assert.Equal(Path.Combine(precedenceSeasonFolder, "season.nfo"), new ItemInfo(storedNfoSeasonAfterScan).PreviousLocalNfoPath);

            var seasonNfoPath = Path.Combine(precedenceSeasonFolder, "season.nfo");
            File.Delete(seasonNfoPath);
            Assert.False(File.Exists(seasonNfoPath));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var fallbackSeason = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{nfoSeason.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Secondary Season XML", fallbackSeason?.Name);
            Assert.Equal("From secondary XML.", fallbackSeason?.Overview);

            File.Delete(Path.Combine(precedenceSeasonFolder, "season.xml"));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var withoutSeasonSidecars = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{nfoSeason.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Parent Season Fallback", withoutSeasonSidecars?.Name);

            var seriesXmlPath = Path.Combine(seriesFolder, "series.xml");
            var episodeXmlPath = Path.Combine(seasonFolder, "Example Show - S01E01.xml");
            await File.WriteAllTextAsync(
                seriesXmlPath,
                "<Series><LocalTitle>Updated XML Series</LocalTitle><Overview>Updated series XML.</Overview></Series>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                episodeXmlPath,
                "<Item><LocalTitle>Updated XML Episode</LocalTitle><Overview>Updated episode XML.</Overview></Item>",
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(seriesXmlPath, DateTime.UtcNow.AddMinutes(1));
            File.SetLastWriteTimeUtc(episodeXmlPath, DateTime.UtcNow.AddMinutes(1));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var updatedSeries = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            var updatedEpisode = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{xmlEpisode.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Updated XML Series", updatedSeries?.Name);
            Assert.Equal("Updated series XML.", updatedSeries?.Overview);
            Assert.Equal("Updated XML Episode", updatedEpisode?.Name);
            Assert.Equal("Updated episode XML.", updatedEpisode?.Overview);

            File.Delete(Path.Combine(precedenceFolder, "tvshow.nfo"));
            File.Delete(Path.Combine(precedenceSeasonFolder, "Both Sources - S01E01.nfo"));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var fallbackSeries = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{preferredNfo.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            var preferredEpisode = Assert.Single(preferredEpisodes.Items, item => item.Name == "Preferred Episode NFO");
            var fallbackEpisode = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{preferredEpisode.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Secondary Series XML", fallbackSeries?.Name);
            Assert.Equal("Secondary Episode XML", fallbackEpisode?.Name);

            File.Delete(seriesXmlPath);
            File.Delete(episodeXmlPath);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var withoutSeriesXml = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            var withoutEpisodeXml = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{xmlEpisode.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Example Show", withoutSeriesXml?.Name);
            Assert.Equal("Example Show - S01E01", withoutEpisodeXml?.Name);
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
    public async Task EmbyMovieXml_ProvidesClientMetadataForDedicatedAndLooseMovies()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-xml-sidecar-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var dedicatedFolder = Path.Combine(categoryFolder, "Dedicated Movie (2021)");
        Directory.CreateDirectory(dedicatedFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(dedicatedFolder, "Dedicated Movie (2021).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(dedicatedFolder, "movie.xml"),
            "<Item><LocalTitle>Dedicated XML Title</LocalTitle><ProductionYear>2021</ProductionYear><Overview>Dedicated XML overview.</Overview></Item>",
            TestContext.Current.CancellationToken);
        var alternateNameFolder = Path.Combine(categoryFolder, "Emby Folder Name (2024)");
        Directory.CreateDirectory(alternateNameFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(alternateNameFolder, "alternate-name.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(alternateNameFolder, "movie.xml"),
            "<Item><LocalTitle>Different File XML Title</LocalTitle><ProductionYear>2024</ProductionYear></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(categoryFolder, "Loose Movie (2022).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(categoryFolder, "Loose Movie (2022).xml"),
            "<Item><LocalTitle>Loose XML Title</LocalTitle><ProductionYear>2022</ProductionYear><Overview>Loose XML overview.</Overview></Item>",
            TestContext.Current.CancellationToken);
        var precedenceFolder = Path.Combine(categoryFolder, "Precedence Movie (2023)");
        Directory.CreateDirectory(precedenceFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(precedenceFolder, "Precedence Movie (2023).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "movie.xml"),
            "<Item><LocalTitle>Secondary XML Title</LocalTitle></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "movie.nfo"),
            "<movie><title>Preferred NFO Title</title></movie>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin XML sidecar test " + Guid.NewGuid().ToString("N");
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
                "UserViews?presetViews=movies",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            Assert.Equal(4, movies.Items.Count);
            var dedicated = Assert.Single(movies.Items, item => item.Name == "Dedicated XML Title");
            var alternateName = Assert.Single(movies.Items, item => item.Name == "Different File XML Title");
            var loose = Assert.Single(movies.Items, item => item.Name == "Loose XML Title");
            var preferredNfo = Assert.Single(movies.Items, item => item.Name == "Preferred NFO Title");
            Assert.Equal(BaseItemKind.Movie, dedicated.Type);
            Assert.Equal(BaseItemKind.Movie, alternateName.Type);
            Assert.Equal(BaseItemKind.Movie, loose.Type);
            Assert.Equal(BaseItemKind.Movie, preferredNfo.Type);
            Assert.Equal(2021, dedicated.ProductionYear);
            Assert.Equal(2024, alternateName.ProductionYear);
            Assert.Equal(2022, loose.ProductionYear);

            var dedicatedDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{dedicated.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            var looseDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{loose.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Dedicated XML overview.", dedicatedDetails?.Overview);
            Assert.Equal("Loose XML overview.", looseDetails?.Overview);

            var dedicatedXmlPath = Path.Combine(dedicatedFolder, "movie.xml");
            await File.WriteAllTextAsync(
                dedicatedXmlPath,
                "<Item><LocalTitle>Updated XML Title</LocalTitle><Overview>Updated XML overview.</Overview></Item>",
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(dedicatedXmlPath, DateTime.UtcNow.AddMinutes(1));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var updatedDedicated = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{dedicated.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Updated XML Title", updatedDedicated?.Name);
            Assert.Equal("Updated XML overview.", updatedDedicated?.Overview);

            File.Delete(Path.Combine(precedenceFolder, "movie.nfo"));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var fallbackDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{preferredNfo.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Secondary XML Title", fallbackDetails?.Name);

            File.Delete(Path.Combine(precedenceFolder, "movie.xml"));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var withoutSidecars = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{preferredNfo.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(withoutSidecars);
            Assert.Equal("Precedence Movie (2023)", withoutSidecars.Name);
            Assert.Equal(2023, withoutSidecars.ProductionYear);
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
    public async Task SingleLooseMovie_DoesNotReplacePhysicalCategoryFolder()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-loose-movie-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        Directory.CreateDirectory(categoryFolder);
        var matchedMovieFolder = Path.Combine(testRoot, "Comedy", "Matched Movie (2020)");
        Directory.CreateDirectory(matchedMovieFolder);
        var yearInFolder = Path.Combine(testRoot, "Drama", "Year in Folder (2020)");
        Directory.CreateDirectory(yearInFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(categoryFolder, "Loose Movie (2020).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(matchedMovieFolder, "Matched Movie (2020).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(yearInFolder, "Year in Folder.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin loose movie test " + Guid.NewGuid().ToString("N");
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
                "UserViews?presetViews=movies",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            Assert.Null(library.CollectionType);
            Assert.True(library.IsFolder);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            Assert.Single(movies.Items, item => item.Type == BaseItemKind.Movie);

            var comedy = Assert.Single(groups.Items, item => item.Name == "Comedy");
            Assert.Equal(BaseItemKind.Folder, comedy.Type);
            var movieFolders = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={comedy.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movieFolders);
            var matchedMovie = Assert.Single(movieFolders.Items);
            Assert.Equal(BaseItemKind.Movie, matchedMovie.Type);
            Assert.Equal("Matched Movie (2020)", matchedMovie.Name);

            var drama = Assert.Single(groups.Items, item => item.Name == "Drama");
            Assert.Equal(BaseItemKind.Folder, drama.Type);
            var dramaItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(dramaItems);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(dramaItems.Items).Type);
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
    public async Task MovieNfo_ProvidesClientMetadataWithoutHidingPhysicalGroups()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-sidecar-" + Guid.NewGuid().ToString("N"));
        var movieFolder = Path.Combine(testRoot, "Action", "Example Movie (2020)");
        Directory.CreateDirectory(movieFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(movieFolder, "Example Movie (2020).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(movieFolder, "movie.nfo"),
            "<movie><title>A Local Sidecar Title</title><year>2020</year><plot>From the local NFO.</plot></movie>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin sidecar test " + Guid.NewGuid().ToString("N");
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
                "UserViews?presetViews=movies",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            Assert.Null(library.CollectionType);
            Assert.True(library.IsFolder);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            var movie = Assert.Single(movies.Items, item => item.Type == BaseItemKind.Movie);
            Assert.Equal("A Local Sidecar Title", movie.Name);
            Assert.Equal(2020, movie.ProductionYear);

            var movieDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movieDetails);
            Assert.Equal("From the local NFO.", movieDetails.Overview);
            var storedMovie = Assert.IsType<MediaBrowser.Controller.Entities.Movies.Movie>(libraryManager.GetItemById(movie.Id));
            Assert.True(storedMovie.ProviderIds.ContainsKey(ItemInfo.LocalNfoPathProviderId));
            var clientDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}?fields=ProviderIds",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(clientDetails);
            Assert.DoesNotContain(ItemInfo.LocalNfoPathProviderId, clientDetails.ProviderIds.Keys);

            using var streamResponse = await client.GetAsync(
                $"Videos/{movie.Id}/stream?static=true",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"Videos/{movie.Id}/stream?static=true");
            rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);
            using var rangeResponse = await client.SendAsync(rangeRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal(videoBytes[100..200], await rangeResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            var nfoPath = Path.Combine(movieFolder, "movie.nfo");
            await File.WriteAllTextAsync(
                nfoPath,
                "<movie><title>Updated Local Title</title><year>2021</year><plot>Updated from the local NFO.</plot></movie>",
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(nfoPath, storedMovie.DateLastSaved.AddSeconds(2));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var rescannedMovies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(rescannedMovies);
            var rescannedMovie = Assert.Single(rescannedMovies.Items);
            Assert.Equal(movie.Id, rescannedMovie.Id);
            Assert.Equal("Updated Local Title", rescannedMovie.Name);
            Assert.Equal(2021, rescannedMovie.ProductionYear);
            var rescannedDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal("Updated from the local NFO.", rescannedDetails?.Overview);

            File.Delete(nfoPath);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var withoutSidecar = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(withoutSidecar);
            Assert.Equal("Example Movie (2020)", withoutSidecar.Name);
            Assert.Equal(2020, withoutSidecar.ProductionYear);
            Assert.Null(withoutSidecar.Overview);
            var storedWithoutSidecar = Assert.IsType<MediaBrowser.Controller.Entities.Movies.Movie>(libraryManager.GetItemById(movie.Id));
            Assert.False(storedWithoutSidecar.ProviderIds.ContainsKey(ItemInfo.LocalNfoPathProviderId));
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
}
