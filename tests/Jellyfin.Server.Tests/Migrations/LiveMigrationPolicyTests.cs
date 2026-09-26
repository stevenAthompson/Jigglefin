using System;
using Jellyfin.Server.Migrations;
using Jellyfin.Server.Migrations.Routines;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class LiveMigrationPolicyTests
{
    [Theory]
    [InlineData(typeof(MoveTrickplayFiles))]
    [InlineData(typeof(MoveExtractedFiles))]
    [InlineData(typeof(RestorePlaylistChildrenFromMetadata))]
    [InlineData(typeof(AddDefaultPluginRepository))]
    [InlineData(typeof(ReaddDefaultPluginRepository))]
    [InlineData(typeof(UpdateDefaultPluginRepository))]
    [InlineData(typeof(AddDefaultCastReceivers))]
    [InlineData(typeof(RefreshInternalDateModified))]
    [InlineData(typeof(EnableLocalSimilarityProviders))]
    [InlineData(typeof(LiveMigrationPolicyTests))]
    public void MediaOnlineAndUnknownCodeRoutines_AreNotRun(Type routine)
    {
        Assert.False(LiveMigrationPolicy.ShouldRun(routine));
    }

    [Theory]
    [InlineData(typeof(MigrateLiveFolders))]
    [InlineData(typeof(MigrateUserDb))]
    [InlineData(typeof(MigrateAuthenticationDb))]
    [InlineData(typeof(MigrateDisplayPreferencesDb))]
    [InlineData(typeof(MigrateLibraryDb))]
    [InlineData(typeof(MigrateLibraryUserData))]
    public void PrivateProfileAndDatabaseUpgrades_ArePreserved(Type routine)
    {
        Assert.True(LiveMigrationPolicy.ShouldRun(routine));
    }
}
