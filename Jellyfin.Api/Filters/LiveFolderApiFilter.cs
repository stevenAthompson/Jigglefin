using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Api.Models.UserViewDtos;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Api.Filters;

/// <summary>
/// A small compatibility boundary for upstream folder routes. Authorization and model
/// binding still run normally, but these actions never enter the catalog implementation.
/// </summary>
public sealed class LiveFolderApiFilter : IActionFilter
{
    /// <summary>The virtual navigation root; not a catalog row or filesystem address.</summary>
    public static readonly Guid HomeId = new("530b1635-c4cd-4a01-a68b-cdd43c1c0f92");
    private readonly ILiveLibrary _library;
    private readonly IUserManager _users;
    private readonly IServerApplicationHost _host;

    /// <summary>Initializes a new instance of the <see cref="LiveFolderApiFilter"/> class.</summary>
    /// <param name="library">The live filesystem library.</param>
    /// <param name="users">Existing Jellyfin users and permissions.</param>
    /// <param name="host">The server identity.</param>
    public LiveFolderApiFilter(ILiveLibrary library, IUserManager users, IServerApplicationHost host)
    {
        _library = library;
        _users = users;
        _host = host;
    }

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action)
        {
            return;
        }

        try
        {
            context.Result = Route(context, action) ?? context.Result;
        }
        catch (FileNotFoundException)
        {
            context.Result = new NotFoundResult();
        }
        catch (DirectoryNotFoundException)
        {
            context.Result = new NotFoundResult();
        }
        catch (UnauthorizedAccessException)
        {
            context.Result = new NotFoundResult();
        }
        catch (ArgumentException exception)
        {
            context.Result = new BadRequestObjectResult(exception.Message);
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    private IActionResult? Route(ActionExecutingContext context, ControllerActionDescriptor action)
    {
        var method = action.MethodInfo.Name;
        var args = context.ActionArguments;
        var controller = action.ControllerTypeInfo.AsType();
        if (controller == typeof(LibraryStructureController))
        {
            // Existing controller-level FirstTimeSetupOrElevated policy still applies.
            return Configure(method, args);
        }

        var catalog = controller == typeof(ArtistsController) || controller == typeof(GenresController)
            || controller == typeof(MusicGenresController) || controller == typeof(PersonsController)
            || controller == typeof(StudiosController) || controller == typeof(TvShowsController)
            || controller == typeof(TrailersController);
        var handled = catalog || controller == typeof(UserViewsController) || controller == typeof(SearchController)
            || (controller == typeof(ItemsController) && method is "GetItems" or "GetItemsByUserIdLegacy")
            || (controller == typeof(LibraryController) && method is "GetAncestors" or "GetSimilarItems" or "GetItemCollections" or "GetMediaFolders")
            || (controller == typeof(UserLibraryController) && method is "GetItem" or "GetItemLegacy" or "GetRootFolder" or "GetRootFolderLegacy"
                or "GetLatestMedia" or "GetLatestMediaLegacy" or "GetIntros" or "GetIntrosLegacy" or "GetLocalTrailers" or "GetLocalTrailersLegacy" or "GetSpecialFeatures" or "GetSpecialFeaturesLegacy");
        if (!handled)
        {
            return null;
        }

        var userId = RequestHelpers.GetUserId(context.HttpContext.User, Arg<Guid?>(args, "userId"));
        var user = userId.Equals(Guid.Empty) ? null : _users.GetUserById(userId);
        if (user is null && !context.HttpContext.User.GetIsApiKey())
        {
            return new NotFoundResult();
        }

        if (catalog)
        {
            return action.MethodInfo.ReturnType == typeof(ActionResult<BaseItemDto>)
                ? new OkObjectResult(Folder(HomeId, "Use Folder View"))
                : new OkObjectResult(new QueryResult<BaseItemDto>());
        }

        switch (method)
        {
            case "GetSearchHints":
                return new OkObjectResult(new SearchHintResult([], 0));
            case "GetSimilarItems":
            case "GetItemCollections":
            case "GetIntros":
            case "GetIntrosLegacy":
                return new OkObjectResult(new QueryResult<BaseItemDto>());
            case "GetLatestMedia":
            case "GetLatestMediaLegacy":
            case "GetLocalTrailers":
            case "GetLocalTrailersLegacy":
            case "GetSpecialFeatures":
            case "GetSpecialFeaturesLegacy":
                return new OkObjectResult(Array.Empty<BaseItemDto>());
            case "GetAncestors":
                var ancestors = new List<BaseItemDto>();
                var visited = new HashSet<Guid>();
                var current = Item(Arg<Guid>(args, "itemId"), user);
                while (current.ParentId is { } parentId && visited.Add(parentId))
                {
                    current = Item(parentId, user);
                    ancestors.Add(current);
                }

                return new OkObjectResult(ancestors);
            case "GetMediaFolders":
            case "GetUserViews":
            case "GetUserViewsLegacy":
                return new OkObjectResult(new QueryResult<BaseItemDto>(Views(user)));
            case "GetGroupingOptions":
            case "GetGroupingOptionsLegacy":
                return new OkObjectResult(Array.Empty<SpecialViewOptionDto>());
            case "GetRootFolder":
            case "GetRootFolderLegacy":
                return new OkObjectResult(Folder(HomeId, "Folders"));
            case "GetItem":
            case "GetItemLegacy":
                return new OkObjectResult(Item(Arg<Guid>(args, "itemId"), user));
            default:
                return new OkObjectResult(Items(context, user));
        }
    }

    private IActionResult Configure(string method, IDictionary<string, object?> args)
    {
        switch (method)
        {
            case "GetVirtualFolders":
                return new OkObjectResult(_library.GetLibraries().Select(library => new VirtualFolderInfo
                {
                    ItemId = library.Id.ToString("N"),
                    Name = library.Name,
                    Locations = library.Roots.Select(root => root.FullPath).ToArray(),
                    CollectionType = null,
                    LibraryOptions = new LibraryOptions
                    {
                        PathInfos = library.Roots.Select(root => new MediaPathInfo(root.FullPath)).ToArray(),
                        SaveLocalMetadata = false,
                        SaveSubtitlesWithMedia = false,
                        EnableRealtimeMonitor = false,
                        EnableAutomaticSeriesGrouping = false,
                        MetadataSavers = []
                    }
                }).ToArray());
            case "AddVirtualFolder":
                var paths = Arg<string[]>(args, "paths") ?? [];
                if (paths.Length == 0)
                {
                    paths = (Arg<AddVirtualFolderDto>(args, "libraryOptionsDto")?.LibraryOptions?.PathInfos ?? []).Select(path => path.Path).ToArray();
                }

                _library.AddLibrary(Arg<string>(args, "name"), paths);
                return new NoContentResult();
            case "RemoveVirtualFolder":
                _library.RemoveLibrary(Arg<string>(args, "name"));
                return new NoContentResult();
            case "RenameVirtualFolder":
                _library.RenameLibrary(Arg<string>(args, "name"), Arg<string>(args, "newName"));
                return new NoContentResult();
            case "AddMediaPath":
                var request = Arg<MediaPathDto>(args, "mediaPathDto");
                _library.AddPath(request.Name, request.PathInfo?.Path ?? request.Path ?? throw new ArgumentException("A filesystem path is required."));
                return new NoContentResult();
            case "RemoveMediaPath":
                _library.RemovePath(Arg<string>(args, "name"), Arg<string>(args, "path"));
                return new NoContentResult();
            default:
                // Never fall through to old option writes, metadata refreshes or watchers.
                return new BadRequestObjectResult("Folder configuration supports names and filesystem paths only.");
        }
    }

    private BaseItemDto[] Views(User? user)
        => _library.GetLibraries().Where(library => CanAccess(user, library))
            .Select(library => Folder(library.Id, library.Name, HomeId)).ToArray();

    private QueryResult<BaseItemDto> Items(ActionExecutingContext context, User? user)
    {
        var args = context.ActionArguments;
        var ids = Arg<Guid[]>(args, "ids") ?? [];
        IEnumerable<BaseItemDto> items;
        if (ids.Length > 0)
        {
            items = ids.Select(id => Item(id, user));
        }
        else
        {
            var parent = Arg<Guid?>(args, "parentId") ?? HomeId;
            if (parent.Equals(Guid.Empty) || parent.Equals(HomeId))
            {
                items = Views(user);
            }
            else
            {
                var library = Authorize(parent, user);
                items = _library.Browse(parent, context.HttpContext.RequestAborted).Select(entry => ToDto(entry, library));
            }
        }

        // Recursive means nothing here: every request is limited to its selected directory.
        var search = Arg<string>(args, "searchTerm");
        if (!string.IsNullOrEmpty(search))
        {
            items = items.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var excluded = Arg<Guid[]>(args, "excludeItemIds") ?? [];
        var includeKinds = Arg<BaseItemKind[]>(args, "includeItemTypes") ?? [];
        var excludeKinds = Arg<BaseItemKind[]>(args, "excludeItemTypes") ?? [];
        var mediaTypes = Arg<MediaType[]>(args, "mediaTypes") ?? [];
        items = items.Where(item => !excluded.Contains(item.Id)
            && (includeKinds.Length == 0 || includeKinds.Contains(item.Type))
            && !excludeKinds.Contains(item.Type)
            && (mediaTypes.Length == 0 || mediaTypes.Contains(item.MediaType)));
        var descending = (Arg<SortOrder[]>(args, "sortOrder") ?? []).FirstOrDefault() == SortOrder.Descending;
        var ordered = items.OrderByDescending(item => item.IsFolder == true);
        var all = (descending ? ordered.ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase) : ordered.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var start = Math.Max(0, Arg<int?>(args, "startIndex") ?? 0);
        var limit = Math.Max(0, Arg<int?>(args, "limit") ?? all.Length);
        return new QueryResult<BaseItemDto>(start, all.Length, all.Skip(start).Take(limit).ToArray());
    }

    private BaseItemDto Item(Guid id, User? user)
    {
        if (id.Equals(Guid.Empty) || id.Equals(HomeId))
        {
            return Folder(HomeId, "Folders");
        }

        var library = Authorize(id, user);
        if (library.Id.Equals(id))
        {
            return Folder(id, library.Name, HomeId);
        }

        return ToDto(_library.GetEntry(id) ?? throw new FileNotFoundException(), library);
    }

    private LiveLibraryDefinition Authorize(Guid id, User? user)
    {
        var library = _library.FindLibrary(id);
        return library is not null && CanAccess(user, library) ? library : throw new FileNotFoundException();
    }

    private static bool CanAccess(User? user, LiveLibraryDefinition library)
    {
        if (user is null)
        {
            return true; // Only authenticated API keys reach this branch.
        }

        // Unknown local ratings/tags cannot safely satisfy legacy metadata restrictions.
        // Fail closed; administrators can replace those with explicit folder permissions.
        if (user.MaxParentalRatingScore.HasValue || user.MaxParentalRatingSubScore.HasValue
            || user.GetPreference(PreferenceKind.AllowedTags).Length > 0
            || user.GetPreference(PreferenceKind.BlockedTags).Length > 0
            || user.GetPreference(PreferenceKind.BlockUnratedItems).Length > 0)
        {
            return false;
        }

        var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders);
        return !blocked.Contains(library.Id)
            && (user.HasPermission(PermissionKind.EnableAllFolders) || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Contains(library.Id));
    }

    private BaseItemDto ToDto(LiveDirectoryEntry entry, LiveLibraryDefinition library)
    {
        var parent = entry.ParentId;
        if (parent is null || (library.Roots.Count == 1 && parent.Value.Equals(entry.RootId)))
        {
            parent = library.Id;
        }

        var dto = Folder(entry.Id, entry.Name, parent);
        dto.IsFolder = entry.File.IsDirectory;
        dto.LocationType = LocationType.FileSystem;
        if (!entry.File.IsDirectory)
        {
            (dto.Type, dto.MediaType) = LiveMediaClassifier.Classify(entry.Name);
        }

        if (entry.File.IsLink)
        {
            dto.Overview = "Link/reparse entries are visible but cannot be opened.";
            dto.MediaType = MediaType.Unknown;
        }

        return dto;
    }

    private BaseItemDto Folder(Guid id, string name, Guid? parentId = null)
        => new()
        {
            Id = id,
            ParentId = parentId,
            Name = name,
            SortName = name,
            Type = BaseItemKind.Folder,
            IsFolder = true,
            ServerId = _host.SystemId,
            DisplayPreferencesId = id.ToString("N"),
            CanDelete = false,
            CanDownload = false,
            ImageTags = new Dictionary<ImageType, string>(),
            BackdropImageTags = [],
            UserData = new UserItemDataDto { Key = id.ToString("N"), ItemId = id }
        };

    private static T Arg<T>(IDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var value) && value is T typed ? typed : default!;
}
