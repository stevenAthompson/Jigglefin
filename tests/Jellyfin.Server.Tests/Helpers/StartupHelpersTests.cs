using System;
using System.IO;
using Jellyfin.Server.Helpers;
using Xunit;

namespace Jellyfin.Server.Tests.Helpers;

public sealed class StartupHelpersTests
{
    [Fact]
    public void GetDataDirectory_IgnoresJellyfinProfileOverride()
    {
        var options = new StartupOptions();

        Assert.Equal(
            StartupHelpers.GetDefaultDataDirectory(),
            StartupHelpers.GetDataDirectory(
                options,
                name => name == "JELLYFIN_DATA_DIR" ? "existing-jellyfin-profile" : null));

        Assert.Equal(
            "jigglefin-profile",
            StartupHelpers.GetDataDirectory(
                options,
                name => name == "JIGGLEFIN_DATA_DIR" ? "jigglefin-profile" : null));

        options.DataDir = "explicit-profile";
        Assert.Equal(
            "explicit-profile",
            StartupHelpers.GetDataDirectory(options, _ => "jigglefin-profile"));
    }

    [Fact]
    public void GetDefaultDataDirectory_UsesSeparateJigglefinProfile()
    {
        var localDataDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        Assert.Equal(
            Path.Join(localDataDirectory, "jigglefin"),
            StartupHelpers.GetDefaultDataDirectory());
        Assert.NotEqual(
            Path.Join(localDataDirectory, "jellyfin"),
            StartupHelpers.GetDefaultDataDirectory());
    }
}
