using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

namespace MediaBrowser.Controller.Library;

/// <summary>Shared authorization for live directory, detail and playback routes.</summary>
public static class LiveLibraryAccess
{
    /// <summary>Checks folder permissions without reading media or metadata.</summary>
    /// <param name="user">The user, or null for authenticated API keys/trusted internal callers.</param>
    /// <param name="library">The owning group.</param>
    /// <returns>Whether access is allowed.</returns>
    public static bool CanAccess(User? user, LiveLibraryDefinition library)
    {
        if (user is null)
        {
            return true;
        }

        // Metadata is intentionally unknown while browsing. Never silently bypass an
        // older profile's tag/rating restrictions; use explicit folder permissions instead.
        if (user.MaxParentalRatingScore.HasValue || user.MaxParentalRatingSubScore.HasValue
            || user.GetPreference(PreferenceKind.AllowedTags).Length > 0
            || user.GetPreference(PreferenceKind.BlockedTags).Length > 0
            || user.GetPreference(PreferenceKind.BlockUnratedItems).Length > 0)
        {
            return false;
        }

        return !System.Array.Exists(user.GetPreferenceValues<System.Guid>(PreferenceKind.BlockedMediaFolders), id => id.Equals(library.Id))
            && (user.HasPermission(PermissionKind.EnableAllFolders)
                || System.Array.Exists(user.GetPreferenceValues<System.Guid>(PreferenceKind.EnabledFolders), id => id.Equals(library.Id)));
    }
}
