using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Api.Filters;

/// <summary>
/// Fail-closed public capability boundary. Upstream endpoints are not automatically
/// enabled by an update. Media trees are read-only and catalog/online features have
/// no executable route, even for an administrator or a client with a cached menu.
/// </summary>
public sealed class LiveCapabilityFilter : IActionFilter, IOrderedFilter
{
    private readonly ILiveLibrary _library;
    private readonly IUserManager _users;

    /// <summary>Initializes a new instance of the <see cref="LiveCapabilityFilter"/> class.</summary>
    /// <param name="library">Ownership lookup; no filesystem access.</param>
    /// <param name="users">The authenticated users.</param>
    public LiveCapabilityFilter(ILiveLibrary library, IUserManager users)
    {
        _library = library;
        _users = users;
    }

    /// <inheritdoc />
    public int Order => -1000;

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action)
        {
            return;
        }

        var controller = action.ControllerTypeInfo.Name;
        var method = action.MethodInfo.Name;
        if (controller == nameof(BrandingController) && method == "GetBrandingCss")
        {
            context.Result = new ContentResult { Content = string.Empty, ContentType = "text/css" };
            return;
        }

        if (IsEmptyFeature(controller, method))
        {
            context.Result = EmptyResponse(action.MethodInfo.ReturnType);
            return;
        }

        if (controller == nameof(ItemRefreshController)
            || (controller == nameof(LibraryController) && method is "RefreshLibrary" or "PostUpdatedSeries" or "PostUpdatedMovies" or "PostUpdatedMedia"))
        {
            // A legacy refresh notification is acknowledged without touching any media.
            context.Result = new NoContentResult();
            return;
        }

        if (!IsAllowed(controller, method))
        {
            context.Result = new BadRequestObjectResult("This feature is unavailable in Jigglefin. Use Folder View; media folders are read-only.");
            return;
        }

        if (controller is nameof(AudioController) or nameof(VideosController) or nameof(UniversalAudioController)
            or nameof(DynamicHlsController) or nameof(MediaInfoController) or nameof(SubtitleController)
            or nameof(PlaystateController) or nameof(UserLibraryController) or nameof(ItemsController)
            || (controller == nameof(ImageController) && method.StartsWith("GetItem", StringComparison.Ordinal)))
        {
            // Reject stale catalog IDs before any upstream resolution can reach the old
            // database. Authorization precedes stat/probe. Playback report DTOs supplied
            // by clients are not accepted as media source descriptions.
            var id = GetItemId(context);
            if (id.HasValue && !id.Value.Equals(Guid.Empty) && !id.Value.Equals(LiveFolderApiFilter.HomeId))
            {
                var userId = RequestHelpers.GetUserId(context.HttpContext.User, null);
                var user = userId.Equals(Guid.Empty) ? null : _users.GetUserById(userId);
                var library = _library.FindLibrary(id.Value);
                if (library is null || (user is null && !context.HttpContext.User.GetIsApiKey()) || !LiveLibraryAccess.CanAccess(user, library))
                {
                    context.Result = new NotFoundResult();
                }
            }
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    private static Guid? GetItemId(ActionExecutingContext context)
    {
        foreach (var value in context.ActionArguments.Values)
        {
            if (value is PlaybackProgressInfo progress)
            {
                progress.Item = null;
                return progress.ItemId;
            }

            if (value is PlaybackStopInfo stop)
            {
                stop.Item = null;
                return stop.ItemId;
            }
        }

        // Older subtitle routes accept a query override; validate the effective ID.
        foreach (var key in new[] { "itemId", "routeItemId" })
        {
            if (context.ActionArguments.TryGetValue(key, out var value) && value is Guid id)
            {
                return id;
            }
        }

        return null;
    }

    private static bool IsEmptyFeature(string controller, string method)
        => controller switch
        {
            nameof(InstantMixController) or nameof(SuggestionsController) or nameof(MoviesController)
                or nameof(YearsController) or nameof(FilterController) or nameof(MediaSegmentsController) => true,
            nameof(ChannelsController) or nameof(LiveTvController) or nameof(PlaylistsController)
                => method.StartsWith("Get", StringComparison.Ordinal),
            nameof(ItemLookupController) => method != "ApplySearchCriteria",
            nameof(RemoteImageController) => method is "GetRemoteImages" or "GetRemoteImageProviders",
            nameof(SubtitleController) => method is "SearchRemoteSubtitles" or "GetFallbackFontList",
            nameof(LyricsController) => method == "SearchRemoteLyrics",
            nameof(PluginsController) => method == "GetPlugins",
            nameof(PackageController) => method is "GetPackages" or "GetRepositories",
            nameof(DashboardController) => method == "GetConfigurationPages",
            nameof(BrandingController) => method == "GetBrandingOptions",
            nameof(VideosController) => method == "GetAdditionalPart",
            nameof(LibraryController) => method is "GetThemeSongs" or "GetThemeVideos" or "GetThemeMedia" or "GetItemCounts" or "GetLibraryOptionsInfo",
            _ => false
        };

    private static bool IsAllowed(string controller, string method)
        => controller switch
        {
            // Private profile data, authentication and incoming client connections.
            nameof(UserController) => method is "GetUsers" or "GetPublicUsers" or "GetUserById" or "DeleteUser"
                or "AuthenticateUser" or "AuthenticateUserByName" or "AuthenticateWithQuickConnect"
                or "UpdateUserPassword" or "UpdateUserPasswordLegacy" or "UpdateUser" or "UpdateUserLegacy" or "UpdateUserPolicy"
                or "UpdateUserConfiguration" or "UpdateUserConfigurationLegacy" or "CreateUserByName" or "ForgotPassword" or "ForgotPasswordPin" or "GetCurrentUser",
            nameof(QuickConnectController) => method is "GetQuickConnectEnabled" or "InitiateQuickConnect" or "GetQuickConnectState" or "AuthorizeQuickConnect",
            nameof(ApiKeyController) => method is "GetKeys" or "CreateKey" or "RevokeKey",
            nameof(DisplayPreferencesController) => method is "GetDisplayPreferences" or "UpdateDisplayPreferences",
            nameof(DevicesController) => method is "GetDevices" or "GetDeviceInfo" or "GetDeviceOptions" or "UpdateDeviceOptions" or "DeleteDevice",
            nameof(StartupController) => method is "CompleteWizard" or "GetStartupConfiguration" or "UpdateInitialConfiguration" or "SetRemoteAccess" or "GetFirstUser" or "UpdateStartupUser",
            nameof(LocalizationController) => method is "GetCultures" or "GetCountries" or "GetParentalRatings" or "GetLocalizationOptions",
            nameof(TimeSyncController) => method == "GetUtcTime",
            nameof(SystemController) => method is "GetSystemInfo" or "GetSystemStorage" or "GetPublicSystemInfo" or "PingSystem" or "RestartApplication"
                or "ShutdownApplication" or "GetServerLogs" or "GetEndpointInfo" or "GetLogFile",
            nameof(ActivityLogController) => method == "GetLogEntries",
            nameof(ClientLogController) => method == "LogFile",
            nameof(JigglefinController) => method == "ClearSelectionCache",
            // These routes are wholly replaced by LiveFolderApiFilter.
            nameof(LibraryStructureController) or nameof(UserViewsController) or nameof(SearchController)
                or nameof(ArtistsController) or nameof(GenresController) or nameof(MusicGenresController)
                or nameof(PersonsController) or nameof(StudiosController) or nameof(TvShowsController)
                or nameof(TrailersController) => true,
            nameof(ItemsController) => method is "GetItems" or "GetItemsByUserIdLegacy" or "GetResumeItems" or "GetResumeItemsLegacy"
                or "GetItemUserData" or "GetItemUserDataLegacy" or "UpdateItemUserData" or "UpdateItemUserDataLegacy",
            nameof(UserLibraryController) => method is "GetItem" or "GetItemLegacy" or "GetRootFolder" or "GetRootFolderLegacy"
                or "GetIntros" or "GetIntrosLegacy" or "MarkFavoriteItem" or "MarkFavoriteItemLegacy" or "UnmarkFavoriteItem" or "UnmarkFavoriteItemLegacy"
                or "DeleteUserItemRating" or "DeleteUserItemRatingLegacy" or "UpdateUserItemRating" or "UpdateUserItemRatingLegacy"
                or "GetLocalTrailers" or "GetLocalTrailersLegacy" or "GetSpecialFeatures" or "GetSpecialFeaturesLegacy" or "GetLatestMedia" or "GetLatestMediaLegacy",
            nameof(LibraryController) => method is "GetAncestors" or "GetMediaFolders" or "GetItemCollections" or "GetSimilarItems",
            nameof(PlaystateController) => method is "MarkPlayedItem" or "MarkPlayedItemLegacy" or "MarkUnplayedItem" or "MarkUnplayedItemLegacy"
                or "ReportPlaybackStart" or "ReportPlaybackProgress" or "PingPlaybackSession" or "ReportPlaybackStopped" or "OnPlaybackStart" or "OnPlaybackStartLegacy"
                or "OnPlaybackProgress" or "OnPlaybackProgressLegacy" or "OnPlaybackStopped" or "OnPlaybackStoppedLegacy",
            nameof(AudioController) => method is "GetAudioStream" or "GetAudioStreamByContainer",
            nameof(UniversalAudioController) => method == "GetUniversalAudioStream",
            nameof(DynamicHlsController) => method is "GetLiveHlsStream" or "GetMasterHlsVideoPlaylist" or "GetMasterHlsAudioPlaylist" or "GetVariantHlsVideoPlaylist"
                or "GetVariantHlsAudioPlaylist" or "GetHlsVideoSegment" or "GetHlsAudioSegment",
            nameof(HlsSegmentController) => method is "GetHlsAudioSegmentLegacy" or "GetHlsPlaylistLegacy" or "StopEncodingProcess" or "GetHlsVideoSegmentLegacy",
            nameof(VideosController) => method is "GetVideoStream" or "GetVideoStreamByContainer",
            nameof(MediaInfoController) => method is "GetPlaybackInfo" or "GetPostedPlaybackInfo" or "GetBitrateTestBytes" or "CloseLiveStream",
            nameof(SubtitleController) => method is "GetSubtitle" or "GetSubtitleWithTicks" or "GetSubtitlePlaylist",
            nameof(ImageController) => method is "GetItemImage" or "GetItemImageByIndex" or "GetItemImage2" or "GetItemImageInfos"
                or "GetUserImage" or "GetUserImageLegacy" or "GetUserImageByIndexLegacy" or "GetSplashscreen",
            nameof(SessionController) => method is "GetSessions" or "PostCapabilities" or "PostFullCapabilities" or "ReportViewing" or "ReportSessionEnded"
                or "GetAuthProviders" or "GetPasswordResetProviders",
            nameof(ScheduledTasksController) => method is "GetTasks" or "GetTask" or "StartTask" or "StopTask",
            nameof(EnvironmentController) => method is "GetDirectoryContents" or "GetDrives" or "GetParentPath" or "GetDefaultDirectoryBrowser",
            nameof(ConfigurationController) => method is "GetConfiguration" or "GetNamedConfiguration",
            _ => false
        };

    private static IActionResult EmptyResponse(Type type)
    {
        // Construct the declared response shape, not an untyped [] for every endpoint.
        while (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ActionResult<>)))
        {
            type = type.GenericTypeArguments[0];
        }

        if (type == typeof(BaseItemDto))
        {
            // Do not manufacture a blank playable channel/recording/playlist item.
            return new NotFoundResult();
        }

        if (type.IsArray)
        {
            return new OkObjectResult(Array.CreateInstance(type.GetElementType()!, 0));
        }

        if (type.IsGenericType && new[] { typeof(IEnumerable<>), typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>), typeof(List<>) }.Contains(type.GetGenericTypeDefinition()))
        {
            return new OkObjectResult(Array.CreateInstance(type.GenericTypeArguments[0], 0));
        }

        if ((type.IsGenericType && type.GetGenericTypeDefinition() == typeof(QueryResult<>))
            || (type.Namespace?.StartsWith("MediaBrowser.Model", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("Jellyfin.Api.Models", StringComparison.Ordinal) == true))
        {
            if (type.GetConstructor(Type.EmptyTypes) is not null)
            {
                return new OkObjectResult(Activator.CreateInstance(type));
            }
        }

        return new NotFoundResult();
    }
}
