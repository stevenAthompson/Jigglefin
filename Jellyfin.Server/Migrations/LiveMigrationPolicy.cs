using System;
using System.Collections.Generic;
using Jellyfin.Server.Migrations.Routines;

namespace Jellyfin.Server.Migrations;

/// <summary>Only private account/database upgrades run; old media/catalog processing cannot execute during startup.</summary>
internal static class LiveMigrationPolicy
{
    private static readonly HashSet<Type> _allowed =
    [
        typeof(DisableTranscodingThrottling),
        typeof(CreateUserLoggingConfigFile),
        typeof(MigrateActivityLogDb),
        typeof(MigrateUserDb),
        typeof(MigrateDisplayPreferencesDb),
        typeof(MigrateAuthenticationDb),
        typeof(MigrateLibraryDbCompatibilityCheck),
        typeof(MigrateLibraryDb),
        typeof(MigrateLibraryUserData),
        typeof(UpdateNormalizedUsername),
        typeof(DisableLegacyAuthorization),
        typeof(MigrateLiveFolders)
    ];

    /// <summary>Unknown future code routines are not enabled automatically. EF schema migrations remain independent.</summary>
    public static bool ShouldRun(Type type) => _allowed.Contains(type);
}
