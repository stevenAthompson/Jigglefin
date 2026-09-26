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
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Jellyfin.Server.Integration.Tests;

/// <summary>Owns synthetic media/profile paths and observes every live media stat/listing.</summary>
internal sealed class LiveFolderFixture : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("jigglefin-live-contract-");
    private readonly JellyfinApplicationFactory _factory;
    private WebApplicationFactory<Startup>? _configured;
    private int _initialCatalogCount;

    public LiveFolderFixture(bool enableEncoder = false)
    {
        Media = Directory.CreateDirectory(Path.Combine(_directory.FullName, "Media")).FullName;
        _factory = new JellyfinApplicationFactory
        {
            TestProfilePath = Path.Combine(_directory.FullName, "Profile"),
            FfmpegPath = enableEncoder ? Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG") : null
        };
    }

    public string Media { get; }

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _configured!.Services;

    public RecordingReader Reader { get; } = new();

    public static void AssertUnprobedSource(BaseItemDto item)
    {
        if (item.IsFolder != true && item.MediaType is MediaType.Audio or MediaType.Video)
        {
            var source = Assert.Single(item.MediaSources);
            Assert.Equal(item.Id.ToString("N"), source.Id);
            Assert.Equal(item.Path, source.Path);
            Assert.Equal(MediaProtocol.File, source.Protocol);
            Assert.Empty(source.MediaStreams);
            Assert.Null(source.RunTimeTicks);
            Assert.Null(source.Container);
            Assert.Null(source.TranscodingUrl);
        }
        else
        {
            Assert.True(item.MediaSources is null or { Length: 0 });
        }
    }

    public async Task Start(Action<IServiceCollection>? configureServices = null)
    {
        _configured = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILiveDirectoryReader>();
            services.AddSingleton<ILiveDirectoryReader>(Reader);
            configureServices?.Invoke(services);
        }));
        Client = _configured.CreateClient();
        Client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(Client));
        Assert.Empty(Reader.Stats);
        Assert.Empty(Reader.Enumerations);
        _initialCatalogCount = await CatalogCount();
    }

    public HttpClient NewClient() => _configured!.CreateClient();

    public string PathFor(string relative) => Path.Combine(Media, relative.Replace('/', Path.DirectorySeparatorChar));

    public string ReadPathFor(string relative)
    {
        using var lease = LivePathLease.Acquire(Media);
        return Path.Combine(lease.ReadPath, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public string MakeDirectory(string relative) => Directory.CreateDirectory(PathFor(relative)).FullName;

    public string Write(string relative, byte[] bytes)
    {
        var path = PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public async Task<BaseItemDto> AddGroup(string name = "Files", string collectionType = "movies", params string[] paths)
    {
        paths = paths.Length == 0 ? [Media] : paths;
        var before = Reader.Enumerations.Count;
        var beforeStats = Reader.Stats.Count;
        using var response = await Client.PostAsJsonAsync(
            $"Library/VirtualFolders?name={Uri.EscapeDataString(name)}&collectionType={collectionType}&refreshLibrary=true",
            new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { PathInfos = paths.Select(path => new MediaPathInfo(path)).ToArray() } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, Reader.Enumerations.Count);
        var views = await Query("UserViews");
        Assert.Equal(beforeStats, Reader.Stats.Count);
        return Assert.Single(views.Items, item => item.Name == name);
    }

    public async Task<QueryResult<BaseItemDto>> Browse(Guid parent, string directory, string query = "", HttpClient? client = null)
    {
        string readDirectory;
        using (var lease = LivePathLease.Acquire(directory))
        {
            readDirectory = lease.ReadPath;
        }

        var beforeLists = Reader.Enumerations.Count;
        var beforeStats = Reader.Stats.Count;
        var items = await Query($"Items?parentId={parent}{query}", client);
        Assert.Equal(new[] { readDirectory }, Reader.Enumerations.Skip(beforeLists));
        Assert.All(Reader.Stats.Skip(beforeStats), path => Assert.True(
            string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, readDirectory, StringComparison.OrdinalIgnoreCase) || readDirectory.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Listing unexpectedly inspected a child/sidecar: " + path));
        return items;
    }

    public async Task<BaseItemDto> Navigate(Guid group, params string[] segments)
    {
        var parent = group;
        var directory = Media;
        BaseItemDto? item = null;
        foreach (var segment in segments)
        {
            item = Assert.Single((await Browse(parent, directory)).Items, item => item.Name == segment);
            parent = item.Id;
            directory = Path.Combine(directory, segment);
        }

        return item ?? throw new ArgumentException("Specify a relative entry.", nameof(segments));
    }

    public async Task<QueryResult<BaseItemDto>> Query(string path, HttpClient? client = null)
        => await (client ?? Client).GetFromJsonAsync<QueryResult<BaseItemDto>>(path, JsonDefaults.Options, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Missing folder response.");

    public async Task<BaseItemDto> Details(Guid id)
        => await Client.GetFromJsonAsync<BaseItemDto>($"Items/{id}", JsonDefaults.Options, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Missing detail response.");

    public async Task AssertNoCatalogImport() => Assert.Equal(_initialCatalogCount, await CatalogCount());

    public IDisposable LockFiles()
        => new LockedFiles(Directory.EnumerateFiles(Media, "*", SearchOption.AllDirectories));

    public void Dispose()
    {
        Client?.Dispose();
        _configured?.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _directory.Delete(true);
    }

    private async Task<int> CatalogCount()
    {
        await using var database = await Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await database.BaseItems.CountAsync(TestContext.Current.CancellationToken);
    }

    internal sealed class RecordingReader : ILiveDirectoryReader
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

    private sealed class LockedFiles : IDisposable
    {
        private readonly List<FileStream> _streams = [];

        public LockedFiles(IEnumerable<string> paths)
        {
            try
            {
                foreach (var path in paths)
                {
                    _streams.Add(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }
    }
}
