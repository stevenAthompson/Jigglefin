using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Filters;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Filters;

public sealed class LiveFolderApiFilterTests
{
    private readonly Mock<ILiveLibrary> _library = new(MockBehavior.Strict);
    private readonly Mock<IUserManager> _users = new(MockBehavior.Strict);
    private readonly Mock<IServerApplicationHost> _host = new(MockBehavior.Strict);
    private readonly User _user = new("test", "test", "test");
    private readonly LiveLibraryDefinition _definition = new(Guid.NewGuid(), "Books", []);

    public LiveFolderApiFilterTests()
    {
        _user.SetPermission(PermissionKind.EnableAllFolders, true);
        _users.Setup(manager => manager.GetUserById(_user.Id)).Returns(_user);
        _host.SetupGet(host => host.SystemId).Returns("test-server");
    }

    [Theory]
    [InlineData("GetUserViews")]
    [InlineData("GetUserViewsLegacy")]
    public void Views_AreFoldersAndDoNotBrowseMedia(string method)
    {
        _library.Setup(library => library.GetLibraries()).Returns([_definition]);
        var result = Apply<UserViewsController>(method);
        var query = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(result).Value);
        var folder = Assert.Single(query.Items);
        Assert.Equal(_definition.Id, folder.Id);
        Assert.Equal(BaseItemKind.Folder, folder.Type);
        Assert.Null(folder.CollectionType);
        Assert.Equal("test-server", folder.ServerId);
        _library.Verify(library => library.GetLibraries(), Times.Once);
        _library.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("GetItems")]
    [InlineData("GetItemsByUserIdLegacy")]
    public void RecursiveRequest_OnlyListsSelectedDirectory(string method)
    {
        var folderId = Guid.NewGuid();
        _library.Setup(library => library.FindLibrary(folderId)).Returns(_definition);
        _library.Setup(library => library.Browse(folderId, It.IsAny<CancellationToken>())).Returns(
        [
            Entry("Zebra.mp3", false), Entry("Alpha.m4b", false), Entry("Subfolder", true)
        ]);
        var result = Apply<ItemsController>(method, new()
        {
            ["parentId"] = folderId,
            ["recursive"] = true,
            ["startIndex"] = 1,
            ["limit"] = 1
        });
        var query = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(3, query.TotalRecordCount);
        Assert.Equal(1, query.StartIndex);
        var book = Assert.Single(query.Items);
        Assert.Equal("Alpha.m4b", book.Name);
        Assert.Equal(BaseItemKind.AudioBook, book.Type);
        _library.Verify(library => library.Browse(folderId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void DeniedRoot_DoesNotTouchFilesystem()
    {
        _user.SetPermission(PermissionKind.EnableAllFolders, false);
        _library.Setup(library => library.FindLibrary(_definition.Id)).Returns(_definition);
        Assert.IsType<NotFoundResult>(Apply<ItemsController>("GetItems", new() { ["parentId"] = _definition.Id }));
        _library.Verify(library => library.Browse(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _library.Verify(library => library.GetEntry(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void BlockedRoot_TakesPrecedenceOverAllFoldersPermission()
    {
        _user.SetPreference(PreferenceKind.BlockedMediaFolders, new[] { _definition.Id });
        _library.Setup(library => library.GetLibraries()).Returns([_definition]);
        var result = Apply<UserViewsController>("GetUserViews");
        Assert.Empty(Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(result).Value).Items);
    }

    [Fact]
    public void MetadataRestrictedUser_FailsClosedWithoutReadingMetadata()
    {
        _user.SetPreference(PreferenceKind.AllowedTags, ["Safe"]);
        _library.Setup(library => library.GetLibraries()).Returns([_definition]);
        var result = Apply<UserViewsController>("GetUserViews");
        Assert.Empty(Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(result).Value).Items);
    }

    [Fact]
    public void ExplicitlyEnabledRoot_IsVisible()
    {
        _user.SetPermission(PermissionKind.EnableAllFolders, false);
        _user.SetPreference(PreferenceKind.EnabledFolders, new[] { _definition.Id });
        _library.Setup(library => library.GetLibraries()).Returns([_definition]);
        var result = Apply<UserViewsController>("GetUserViews");
        Assert.Single(Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(result).Value).Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void AddRoot_RefreshFlagDoesNotInvokeCatalogAndAvailabilityIsAtomic(bool? enabled)
    {
        _library.Setup(library => library.AddLibrary("Books", new[] { "configured-path" }, null, enabled != false)).Returns(_definition);
        var result = Apply<LibraryStructureController>("AddVirtualFolder", new()
        {
            ["name"] = "Books",
            ["paths"] = new[] { "configured-path" },
            ["refreshLibrary"] = true,
            ["libraryOptionsDto"] = enabled.HasValue ? new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { Enabled = enabled.Value } } : null
        });
        Assert.IsType<NoContentResult>(result);
        _library.Verify(library => library.AddLibrary("Books", new[] { "configured-path" }, null, enabled != false), Times.Once);
        _library.VerifyNoOtherCalls();
        _users.VerifyNoOtherCalls();
    }

    [Fact]
    public void UnknownId_DoesNotFallBackToOldCatalog()
    {
        var id = Guid.NewGuid();
        _library.Setup(library => library.FindLibrary(id)).Returns((LiveLibraryDefinition?)null);
        Assert.IsType<NotFoundResult>(Apply<UserLibraryController>("GetItem", new() { ["itemId"] = id }));
    }

    [Fact]
    public void UnsupportedConfiguration_CannotReenableMetadataFeatures()
    {
        Assert.IsType<BadRequestObjectResult>(Apply<LibraryStructureController>("UpdateLibraryOptions"));
        _library.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(typeof(ArtistsController), "GetArtists")]
    [InlineData(typeof(ArtistsController), "GetAlbumArtists")]
    [InlineData(typeof(GenresController), "GetGenres")]
    [InlineData(typeof(MusicGenresController), "GetMusicGenres")]
    [InlineData(typeof(PersonsController), "GetPersons")]
    [InlineData(typeof(StudiosController), "GetStudios")]
    [InlineData(typeof(TvShowsController), "GetNextUp")]
    [InlineData(typeof(TvShowsController), "GetSeasons")]
    [InlineData(typeof(TvShowsController), "GetEpisodes")]
    [InlineData(typeof(TrailersController), "GetTrailers")]
    public void UnsupportedCatalog_ReturnsValidEmptyQueryWithoutFilesystemWork(Type controller, string method)
    {
        var result = Apply(controller, method);
        var query = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(query.Items);
        Assert.Equal(0, query.TotalRecordCount);
        _library.VerifyNoOtherCalls();
    }

    [Fact]
    public void MissingCatalogObject_LeadsBackToARealFolderView()
    {
        var result = Apply<ArtistsController>("GetArtistByName", new() { ["name"] = "not indexed" });
        var item = Assert.IsType<BaseItemDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Use Folder View", item.Name);
        Assert.Equal(LiveFolderApiFilter.HomeId, item.Id);
        Assert.True(item.IsFolder);
        Assert.Equal(MediaType.Unknown, item.MediaType);
        _library.VerifyNoOtherCalls();

        _library.Setup(library => library.GetLibraries()).Returns([_definition]);
        var browse = Apply<ItemsController>("GetItems", new() { ["parentId"] = item.Id });
        var query = Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(browse).Value);
        Assert.Equal(_definition.Id, Assert.Single(query.Items).Id);
    }

    private IActionResult? Apply<T>(string method, Dictionary<string, object?>? arguments = null)
        => Apply(typeof(T), method, arguments);

    private IActionResult? Apply(Type controller, string method, Dictionary<string, object?>? arguments = null)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(InternalClaimTypes.UserId, _user.Id.ToString())], "test"))
        };
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = controller.GetTypeInfo(),
            MethodInfo = controller.GetMethod(method)!
        };
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], arguments ?? [], new object());
        var state = new Mock<ILiveUserDataStore>();
        state.Setup(store => store.Get(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns((Guid userId, Guid itemId) => new MediaBrowser.Controller.Entities.UserItemData { Key = itemId.ToString("N") });
        new LiveFolderApiFilter(_library.Object, _users.Object, _host.Object, Mock.Of<ILiveItemService>(), state.Object).OnActionExecuting(context);
        return context.Result;
    }

    private LiveDirectoryEntry Entry(string name, bool isDirectory)
        => new(Guid.NewGuid(), Guid.NewGuid(), _definition.Id, name, name, new LiveFileInfo(name, isDirectory, false, isDirectory ? null : 42, DateTime.UnixEpoch));
}
