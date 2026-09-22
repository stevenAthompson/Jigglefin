using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Model.Tests.Configuration;

public static class ServerConfigurationTests
{
    [Fact]
    public static void Constructor_UsesFolderFirstDefaults()
    {
        var configuration = new ServerConfiguration();

        Assert.True(configuration.EnableFolderView);
    }
}
