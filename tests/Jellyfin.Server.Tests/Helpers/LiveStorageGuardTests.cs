using System;
using System.IO;
using System.Xml.Linq;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Server.Helpers;
using MediaBrowser.Controller.Library;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Tests.Helpers;

public sealed class LiveStorageGuardTests
{
    [Theory]
    [InlineData("cache", true)]
    [InlineData("log", true)]
    [InlineData("system-cache", true)]
    [InlineData("metadata", true)]
    [InlineData("transcode", true)]
    [InlineData("cache", false)]
    [InlineData("log", false)]
    [InlineData("system-cache", false)]
    [InlineData("metadata", false)]
    [InlineData("transcode", false)]
    public void Startup_RejectsMediaOverlapBeforeCreatingAnyPrivateFiles(string location, bool legacy)
    {
        var fixture = Directory.CreateTempSubdirectory("jigglefin-startup-path-guard-");
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var config = Directory.CreateDirectory(Path.Combine(profile, "config"));
            var unsafePath = Path.Combine(media.FullName, "Must not be created");
            var cache = location == "cache" ? unsafePath : Path.Combine(profile, "cache");
            var logs = location == "log" ? unsafePath : Path.Combine(profile, "log");
            if (legacy)
            {
                var group = Directory.CreateDirectory(Path.Combine(profile, "root", "default", "Books"));
                new XDocument(new XElement("LibraryOptions", new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", media.FullName)))))
                    .Save(Path.Combine(group.FullName, "options.xml"));
            }
            else
            {
                var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data", "live-folders"), [profile]);
                store.ImportConfiguration([new LiveLibraryDefinition(Guid.NewGuid(), "Books", [LiveDirectoryBrowser.DescribeRoot("Media", media.FullName)])]);
            }

            if (location is "system-cache" or "metadata")
            {
                new XDocument(new XElement("ServerConfiguration", new XElement(location == "metadata" ? "MetadataPath" : "CachePath", unsafePath)))
                    .Save(Path.Combine(config.FullName, "system.xml"));
            }
            else if (location == "transcode")
            {
                new XDocument(new XElement("EncodingOptions", new XElement("TranscodingTempPath", unsafePath)))
                    .Save(Path.Combine(config.FullName, "encoding.xml"));
            }

            var options = new StartupOptions { DataDir = profile, ConfigDir = config.FullName, CacheDir = cache, LogDir = logs, WebDir = Path.Combine(fixture.FullName, "Web") };
            Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(options));
            Assert.Empty(Directory.EnumerateFileSystemEntries(media.FullName));
            Assert.False(Directory.Exists(Path.Combine(profile, "log")));
            Assert.False(Directory.Exists(Path.Combine(profile, "cache")));
            Assert.False(File.Exists(Path.Combine(config.FullName, ".jellyfin-config")));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void WindowsAliases_CannotBypassPrivatePathChecks()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var alias in new[] { @"C:\Media.\Cache", @"C:\Media \Cache", @"C:\Media:stream\Cache", @"\\?\C:\Media\Cache", @"\\.\C:\Media\Cache" })
        {
            Assert.Throws<ArgumentException>(() => LiveDirectoryBrowser.DescribeRoot("Unsafe", alias));
        }
    }

    [Fact]
    public void LinkedPrivateCache_IsRejectedBeforeStartupWritesIntoItsTarget()
    {
        var fixture = Directory.CreateTempSubdirectory("jigglefin-startup-linked-cache-");
        var alias = Path.Combine(fixture.FullName, "Cache alias");
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            var target = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            try
            {
                Directory.CreateSymbolicLink(alias, target.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                throw SkipException.ForSkip("Creating the test link requires local symlink privileges.");
            }

            var options = new StartupOptions { DataDir = profile, CacheDir = Path.Combine(alias, "Cache"), WebDir = Path.Combine(fixture.FullName, "Web") };
            Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(options));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target.FullName));
            Assert.False(Directory.Exists(profile));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }

            fixture.Delete(true);
        }
    }

    [Fact]
    public void MissingMediaLocation_IsNeverCreatedOrRequiredByStartupValidation()
    {
        var fixture = Directory.CreateTempSubdirectory("jigglefin-startup-offline-");
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            var missing = Path.Combine(fixture.FullName, "Disconnected");
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data", "live-folders"), [profile]);
            store.ImportConfiguration([new LiveLibraryDefinition(Guid.NewGuid(), "Offline", [LiveDirectoryBrowser.DescribeRoot("Missing", missing)])]);
            LiveStorageGuard.Validate(profile, Path.Combine(profile, "config"), Path.Combine(profile, "cache"), Path.Combine(profile, "logs"));
            Assert.False(Directory.Exists(missing));
            Assert.False(Directory.Exists(Path.Combine(profile, "cache")));
            Assert.False(Directory.Exists(Path.Combine(profile, "logs")));
        }
        finally
        {
            fixture.Delete(true);
        }
    }
}
