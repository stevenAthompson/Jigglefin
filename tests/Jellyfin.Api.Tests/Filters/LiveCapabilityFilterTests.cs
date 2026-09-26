using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Filters;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Filters;

public sealed class LiveCapabilityFilterTests
{
    private readonly Mock<ILiveLibrary> _library = new(MockBehavior.Strict);
    private readonly Mock<IUserManager> _users = new(MockBehavior.Strict);
    private readonly User _user = new("local", "local", "local");

    [Theory]
    [InlineData(typeof(LibraryController), "DeleteItem")]
    [InlineData(typeof(LibraryController), "DeleteItems")]
    [InlineData(typeof(ItemUpdateController), "UpdateItem")]
    [InlineData(typeof(ImageController), "SetItemImage")]
    [InlineData(typeof(ImageController), "DeleteItemImage")]
    [InlineData(typeof(SubtitleController), "UploadSubtitle")]
    [InlineData(typeof(SubtitleController), "DeleteSubtitle")]
    [InlineData(typeof(SubtitleController), "DownloadRemoteSubtitles")]
    [InlineData(typeof(SubtitleController), "GetRemoteSubtitles")]
    [InlineData(typeof(LyricsController), "UploadLyrics")]
    [InlineData(typeof(LyricsController), "DownloadRemoteLyrics")]
    [InlineData(typeof(RemoteImageController), "DownloadRemoteImage")]
    [InlineData(typeof(PackageController), "InstallPackage")]
    [InlineData(typeof(PackageController), "SetRepositories")]
    [InlineData(typeof(PluginsController), "EnablePlugin")]
    [InlineData(typeof(ItemLookupController), "ApplySearchCriteria")]
    [InlineData(typeof(ConfigurationController), "UpdateConfiguration")]
    [InlineData(typeof(ConfigurationController), "UpdateNamedConfiguration")]
    [InlineData(typeof(EnvironmentController), "ValidatePath")]
    [InlineData(typeof(VideosController), "MergeVersions")]
    [InlineData(typeof(SessionController), "Play")]
    [InlineData(typeof(SessionController), "SendGeneralCommand")]
    [InlineData(typeof(MediaInfoController), "OpenLiveStream")]
    [InlineData(typeof(BackupController), "CreateBackup")]
    public void MutatingOrOnlineFeatures_AreBlockedBeforeAnyMediaAccess(Type controller, string method)
    {
        Assert.IsType<BadRequestObjectResult>(Apply(controller, method));
        _library.VerifyNoOtherCalls();
        _users.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(typeof(InstantMixController), "GetInstantMixFromSong", typeof(QueryResult<BaseItemDto>))]
    [InlineData(typeof(ChannelsController), "GetChannels", typeof(QueryResult<BaseItemDto>))]
    [InlineData(typeof(FilterController), "GetQueryFilters", typeof(QueryFilters))]
    [InlineData(typeof(FilterController), "GetQueryFiltersLegacy", typeof(QueryFiltersLegacy))]
    [InlineData(typeof(LibraryController), "GetThemeMedia", typeof(AllThemeMediaResult))]
    public void UnsupportedCatalogs_ReturnTheirDeclaredEmptyShape(Type controller, string method, Type expected)
    {
        var result = Assert.IsType<OkObjectResult>(Apply(controller, method));
        Assert.IsType(expected, result.Value);
        _library.VerifyNoOtherCalls();
        _users.VerifyNoOtherCalls();
    }

    [Fact]
    public void UnknownController_IsBlockedWithoutMediaAccess()
    {
        Assert.IsType<BadRequestObjectResult>(Apply(typeof(UnavailableController), nameof(UnavailableController.Read)));
        _library.VerifyNoOtherCalls();
        _users.VerifyNoOtherCalls();
    }

    [Fact]
    public void UnknownCatalogMediaId_IsRejectedWithoutResolvingTheOldCatalog()
    {
        var id = Guid.NewGuid();
        _users.Setup(users => users.GetUserById(_user.Id)).Returns(_user);
        _library.Setup(library => library.FindLibrary(id)).Returns((LiveLibraryDefinition?)null);
        Assert.IsType<NotFoundResult>(Apply(typeof(AudioController), "GetAudioStream", new() { ["itemId"] = id }));
        _library.Verify(library => library.FindLibrary(id), Times.Once);
        _library.VerifyNoOtherCalls();
    }

    [Fact]
    public void DeniedMediaId_IsRejectedBeforeStatsAndClientMediaDescriptionsAreDiscarded()
    {
        var id = Guid.NewGuid();
        _users.Setup(users => users.GetUserById(_user.Id)).Returns(_user);
        _library.Setup(library => library.FindLibrary(id)).Returns(new LiveLibraryDefinition(Guid.NewGuid(), "Private", []));
        var body = new PlaybackStartInfo { ItemId = id, Item = new BaseItemDto { Path = "https://example.invalid/remote.mp3", MediaType = MediaType.Audio } };
        Assert.IsType<NotFoundResult>(Apply(typeof(PlaystateController), "ReportPlaybackStart", new() { ["playbackStartInfo"] = body }));
        Assert.Null(body.Item);
        _library.Verify(library => library.FindLibrary(id), Times.Once);
        _library.VerifyNoOtherCalls();
    }

    private IActionResult? Apply(Type controller, string method, Dictionary<string, object?>? arguments = null)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(InternalClaimTypes.UserId, _user.Id.ToString())], "test"))
        };
        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = controller.GetTypeInfo(), MethodInfo = controller.GetMethod(method)! };
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], arguments ?? [], new object());
        new LiveCapabilityFilter(_library.Object, _users.Object, Mock.Of<ITranscodeManager>()).OnActionExecuting(context);
        return context.Result;
    }

    private sealed class UnavailableController : ControllerBase
    {
        public ActionResult Read() => throw new InvalidOperationException("Must not execute.");
    }
}
