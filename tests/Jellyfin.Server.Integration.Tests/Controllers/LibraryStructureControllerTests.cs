using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LibraryStructureControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private static string? _accessToken;

    public LibraryStructureControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateAndEnableGroup_UsesPrivateConfigurationNotLegacyDirectoriesOrScans()
    {
        using var client = await Client();
        var name = "configuration-only-" + Guid.NewGuid().ToString("N");
        var privateViews = _factory.Services.GetRequiredService<IServerApplicationPaths>().DefaultUserViewsPath;
        var before = PrivateEntries(privateViews);
        try
        {
            await Create(client, name);
            var group = await Group(client, name);
            Assert.False(group.LibraryOptions!.Enabled);
            Assert.Empty(group.Locations);
            Assert.DoesNotContain((await Views(client)).Items, item => item.Id.Equals(Guid.Parse(group.ItemId)));
            Assert.Equal(before, PrivateEntries(privateViews));

            using var enable = await client.PostAsync($"Jigglefin/Folders/{group.ItemId}/Enabled?enabled=true", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
            Assert.Equal(group.ItemId, (await Group(client, name)).ItemId);
            var visible = Assert.Single((await Views(client)).Items, item => item.Name == name);
            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>($"Items?parentId={visible.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            Assert.Empty(children.Items);
            Assert.Equal(before, PrivateEntries(privateViews));
        }
        finally
        {
            await Remove(client, name);
        }
    }

    [Fact]
    public async Task DuplicateName_DoesNotOverwriteConfiguration()
    {
        using var client = await Client();
        var name = Guid.NewGuid().ToString("N");
        try
        {
            await Create(client, name);
            var original = await Group(client, name);
            using var duplicate = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={name}&refreshLibrary=true",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            var unchanged = await Group(client, name);
            Assert.Equal(original.ItemId, unchanged.ItemId);
            Assert.False(unchanged.LibraryOptions!.Enabled);
        }
        finally
        {
            await Remove(client, name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogOptionsWrite_IsUnavailableForUnknownAndExistingGroups(bool existing)
    {
        using var client = await Client();
        var name = Guid.NewGuid().ToString("N");
        try
        {
            if (existing)
            {
                await Create(client, name);
            }

            var id = existing ? Guid.Parse((await Group(client, name)).ItemId) : Guid.NewGuid();
            using var response = await client.PostAsJsonAsync(
                "Library/VirtualFolders/LibraryOptions",
                new UpdateLibraryOptionsDto { Id = id, LibraryOptions = new LibraryOptions { SaveLocalMetadata = true, EnableRealtimeMonitor = true } },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            if (existing)
            {
                var options = (await Group(client, name)).LibraryOptions!;
                Assert.False(options.Enabled);
                Assert.False(options.SaveLocalMetadata);
                Assert.False(options.EnableRealtimeMonitor);
            }
        }
        finally
        {
            if (existing)
            {
                await Remove(client, name);
            }
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData(".")]
    [InlineData("test/../..")]
    [InlineData("/var/lib/jellyfin/data")]
    public async Task Rename_DisplayLabelsAreNotUsedAsFilesystemPaths(string label)
    {
        using var client = await Client();
        var name = Guid.NewGuid().ToString("N");
        var currentName = name;
        var privateViews = _factory.Services.GetRequiredService<IServerApplicationPaths>().DefaultUserViewsPath;
        var before = PrivateEntries(privateViews);
        try
        {
            await Create(client, name);
            var original = await Group(client, name);
            using var response = await client.PostAsync(
                $"Library/VirtualFolders/Name?name={name}&newName={Uri.EscapeDataString(label)}", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            currentName = label;
            var renamed = await Group(client, label);
            Assert.Equal(original.ItemId, renamed.ItemId);
            Assert.Equal(before, PrivateEntries(privateViews));
        }
        finally
        {
            await Remove(client, currentName);
        }
    }

    [Theory]
    [InlineData("doesntExist")]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData(".")]
    [InlineData("test/../..")]
    [InlineData("/var/lib/jellyfin/data")]
    public async Task UnknownLabel_CannotDeleteOrRenameAnyFilesystemLocation(string name)
    {
        using var client = await Client();
        using var deleted = await client.DeleteAsync($"Library/VirtualFolders?name={Uri.EscapeDataString(name)}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        using var renamed = await client.PostAsync($"Library/VirtualFolders/Name?name={Uri.EscapeDataString(name)}&newName=renamed", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, renamed.StatusCode);
    }

    private async Task<HttpClient> Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        return client;
    }

    private static async Task Create(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            $"Library/VirtualFolders?name={Uri.EscapeDataString(name)}&refreshLibrary=true",
            new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { Enabled = false } },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task Remove(HttpClient client, string name)
    {
        using var response = await client.DeleteAsync($"Library/VirtualFolders?name={Uri.EscapeDataString(name)}&refreshLibrary=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain((await Views(client)).Items, item => item.Name == name);
    }

    private static async Task<VirtualFolderInfo> Group(HttpClient client, string name)
        => Assert.Single((await client.GetFromJsonAsync<VirtualFolderInfo[]>("Library/VirtualFolders", JsonDefaults.Options, TestContext.Current.CancellationToken))!, group => group.Name == name);

    private static async Task<QueryResult<BaseItemDto>> Views(HttpClient client)
        => (await client.GetFromJsonAsync<QueryResult<BaseItemDto>>("UserViews", JsonDefaults.Options, TestContext.Current.CancellationToken))!;

    private static string[] PrivateEntries(string path)
        => Directory.Exists(path) ? Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal).ToArray() : [];
}
