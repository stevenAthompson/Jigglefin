using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Server.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Tests.Helpers;

[Collection("Windows storage boundaries")]
public sealed class LiveStorageBoundaryTests
{
    [Theory]
    [InlineData("data/jellyfin.db")]
    [InlineData("data/live-folders/live-folders.db")]
    [InlineData("cache/images/cached.png")]
    [InlineData("log/log_.log")]
    [InlineData("config/system.xml")]
    [InlineData("config/logging.json")]
    [InlineData("cache/transcodes/session.ts")]
    public void PrivateHardlink_IsRejectedBeforeAnyStartupWrites(string relative)
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-hardlink-");
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var original = Path.Combine(media.FullName, "Keep untouched.txt");
            File.WriteAllText(original, "<ServerConfiguration />");
            var timestamp = File.GetLastWriteTimeUtc(original);
            var alias = Path.Combine(profile, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
            Assert.True(CreateHardLink(alias, original, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message);

            using (File.Open(original, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile }));
            }

            Assert.Equal("<ServerConfiguration />", File.ReadAllText(original));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(original));
            Assert.False(File.Exists(Path.Combine(profile, "config", ".jellyfin-config")));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData("cache/images")]
    [InlineData("data/live-folders")]
    public void NestedPrivateLink_IsRejectedWithoutFollowingTarget(string relative)
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-link-");
        var alias = Path.Combine(fixture.FullName, "Profile", relative);
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var original = Path.Combine(target.FullName, "Untouched.txt");
            File.WriteAllText(original, "private link target must not be visited");
            Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
            Directory.CreateSymbolicLink(alias, target.FullName);
            using var locked = File.Open(original, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = Path.Combine(fixture.FullName, "Profile") }));
            Assert.Single(Directory.EnumerateFileSystemEntries(target.FullName));
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
    public void ConfigurationJournalHardlink_IsRejectedBeforeSqliteCanReadIt()
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-journal-");
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            var data = Path.Combine(profile, "data", "live-folders");
            _ = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), data);
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var original = Path.Combine(media.FullName, "Untouched.bin");
            File.WriteAllText(original, "not a journal");
            Assert.True(CreateHardLink(Path.Combine(data, "live-folders.db-journal"), original, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message);
            using (File.Open(original, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile }));
            }

            Assert.Equal("not a journal", File.ReadAllText(original));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void SubstitutedDrive_CannotHidePrivateMediaOverlapAtStartupOrMount()
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-drive-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            using var drive = new TestDrive(media.FullName);
            var profile = Path.Combine(fixture.FullName, "Profile");
            var group = Directory.CreateDirectory(Path.Combine(profile, "root", "default", "Books"));
            new XDocument(new XElement("LibraryOptions", new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", media.FullName)))))
                .Save(Path.Combine(group.FullName, "options.xml"));
            var unsafeCache = Path.Combine(drive.Root, "Do not create");
            var startupError = Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile, CacheDir = unsafeCache }));
            Assert.Contains("overlap", startupError.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(media.FullName));

            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data", "live-folders"), [unsafeCache]);
            Assert.Throws<ArgumentException>(() => store.AddLibrary("Forbidden", [media.FullName]));
            Assert.Empty(store.GetLibraries());
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void OfflineNetworkDrive_DoesNotRequireMediaAccessWithLocalPrivateStorage()
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-offline-drive-");
        try
        {
            // A deliberately dangling DOS mapping: there is no share to open and
            // no provider connection to resolve. Creating/querying the mapping
            // touches only the local object namespace, not a media filesystem.
            using var drive = new TestDrive(@"\Device\LanmanRedirector\;JigglefinOffline\" + Guid.NewGuid().ToString("N"), rawTarget: true);
            var profile = Path.Combine(fixture.FullName, "Profile");
            var group = Directory.CreateDirectory(Path.Combine(profile, "root", "default", "Offline"));
            new XDocument(new XElement("LibraryOptions", new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", drive.Root)))))
                .Save(Path.Combine(group.FullName, "options.xml"));
            var paths = StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile });
            Assert.Equal(profile, paths.ProgramDataPath);
            Assert.True(Directory.Exists(paths.DataPath));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShortNameAlias_CannotHidePrivateMediaOverlap(bool shortMediaRoot, bool mixedSpelling)
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-short-name-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Long media directory"));
            var buffer = new StringBuilder(32768);
            Assert.NotEqual(0u, GetShortPathName(media.FullName, buffer, buffer.Capacity));
            var shortPath = buffer.ToString();
            if (string.Equals(shortPath, media.FullName, StringComparison.OrdinalIgnoreCase))
            {
                throw SkipException.ForSkip("This volume does not create 8.3 aliases; do not change its global setting for a test.");
            }

            if (mixedSpelling)
            {
                shortPath = Path.Combine(media.Parent!.FullName, Path.GetFileName(shortPath));
            }

            var mediaPath = shortMediaRoot ? shortPath : media.FullName;
            var unsafeCache = Path.Combine(shortMediaRoot ? media.FullName : shortPath, "Do not create");
            var profile = Path.Combine(fixture.FullName, "Profile");
            var group = Directory.CreateDirectory(Path.Combine(profile, "root", "default", "Books"));
            new XDocument(new XElement("LibraryOptions", new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", mediaPath)))))
                .Save(Path.Combine(group.FullName, "options.xml"));
            Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile, CacheDir = unsafeCache }));
            Assert.Empty(Directory.EnumerateFileSystemEntries(media.FullName));
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data", "live-folders"), [unsafeCache]);
            Assert.Throws<ArgumentException>(() => store.AddLibrary("Forbidden", [mediaPath]));
            Assert.Empty(store.GetLibraries());
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void ShortMediaSpelling_IsNotExpandedByConfigurationOrSavedAddressImport()
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-short-config-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Long media directory"));
            var buffer = new StringBuilder(32768);
            Assert.NotEqual(0u, GetShortPathName(media.FullName, buffer, buffer.Capacity));
            var shortPath = buffer.ToString();
            if (string.Equals(shortPath, media.FullName, StringComparison.OrdinalIgnoreCase))
            {
                throw SkipException.ForSkip("Requires existing 8.3 support; never change the volume setting.");
            }

            Assert.Equal(shortPath, LiveDirectoryBrowser.NormalizeRootPath(shortPath));
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(fixture.FullName, "Profile"));
            var group = store.AddLibrary("Short spelling", [shortPath]);
            Assert.Equal(shortPath, Assert.Single(group.Roots).FullPath);
            var file = Path.Combine(media.FullName, "Long chapter name.m4b");
            File.WriteAllText(file, "synthetic media");
            var logicalFile = Path.Combine(shortPath, Path.GetFileName(file));
            var saved = store.ImportSavedAddress(logicalFile);
            var entry = Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken));
            Assert.Equal(saved, entry.Id);
            Assert.Equal(logicalFile, entry.File.FullPath);
            Assert.Equal(entry.Id, store.GetEntry(entry.Id)!.Id);
            File.Delete(file);
            Assert.Empty(store.Browse(group.Id, TestContext.Current.CancellationToken));
            File.WriteAllText(file, "synthetic media");
            Assert.Equal(entry.Id, Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken)).Id);
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void PrivateDriveWithShortTarget_CannotHideOverlapWithLongMediaRoot()
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-short-drive-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Long media directory"));
            var buffer = new StringBuilder(32768);
            Assert.NotEqual(0u, GetShortPathName(media.FullName, buffer, buffer.Capacity));
            var shortPath = buffer.ToString();
            Assert.NotEqual(media.FullName, shortPath);
            using var drive = new TestDrive(shortPath);
            var privatePath = Path.Combine(drive.Root, "Do not create");
            var profile = Path.Combine(fixture.FullName, "Profile");
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data"), [privatePath]);
            Assert.Throws<ArgumentException>(() => store.AddLibrary("Forbidden", [media.FullName]));
            Assert.Empty(Directory.EnumerateFileSystemEntries(media.FullName));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("jigglefin-alias-must-not-resolve.invalid", false)]
    public void LocalShareAlias_CannotHidePrivateMediaOverlap(string host, bool sharedPrivatePath)
    {
        RequireLocalSmb();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-local-share-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var sharePath = @"\\" + host + @"\" + media.FullName[0] + "$" + media.FullName[2..];
            if (!host.EndsWith(".invalid", StringComparison.Ordinal))
            {
                Assert.True(Directory.Exists(sharePath), "The existing local administrative share must be readable for this regression; do not create or modify a share.");
            }

            var mediaPath = sharedPrivatePath ? media.FullName : sharePath;
            var unsafeCache = Path.Combine(sharedPrivatePath ? sharePath : media.FullName, "Do not create");
            var profile = Path.Combine(fixture.FullName, "Profile");
            var group = Directory.CreateDirectory(Path.Combine(profile, "root", "default", "Books"));
            new XDocument(new XElement("LibraryOptions", new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", mediaPath)))))
                .Save(Path.Combine(group.FullName, "options.xml"));
            Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile, CacheDir = unsafeCache }));
            Assert.Empty(Directory.EnumerateFileSystemEntries(media.FullName));
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data", "live-folders"), [unsafeCache]);
            Assert.Throws<ArgumentException>(() => store.AddLibrary("Forbidden", [mediaPath]));
            Assert.Empty(store.GetLibraries());
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData(@"\Device\LanmanRedirector\;Y:0000000000000000\localhost\")]
    [InlineData(@"\Device\Mup\localhost\")]
    [InlineData(@"\Device\Mup\;LanmanRedirector\;Y:0000000000000000\localhost\")]
    public void NetworkDriveDeviceName_CannotHideLocalPrivateStorage(string provider)
    {
        RequireLocalSmb();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-network-device-");
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            using var drive = new TestDrive(provider + fixture.FullName[0] + "$" + fixture.FullName[2..], rawTarget: true);
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data"), [profile]);
            Assert.Throws<ArgumentException>(() => store.AddLibrary("Forbidden", [Path.Combine(drive.Root, "Profile")]));
            Assert.Empty(store.GetLibraries());
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void RealSmbRoot_BrowsesFreshEntriesAndRecoversWithoutAScan()
    {
        RequireLocalSmb();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-live-smb-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var profile = Path.Combine(fixture.FullName, "Profile");
            var sharePath = @"\\localhost\" + media.FullName[0] + "$" + media.FullName[2..];
            Assert.True(Directory.Exists(sharePath), "Read the existing local share only; do not modify server/share settings.");
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data"), [profile]);
            var group = store.AddLibrary("SMB", [sharePath]);
            Assert.Empty(store.Browse(group.Id, TestContext.Current.CancellationToken));
            var file = Path.Combine(media.FullName, "Chapter.m4b");
            File.WriteAllText(file, "owned SMB bytes");
            var timestamp = File.GetLastWriteTimeUtc(file);
            var entry = Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken));
            Assert.Equal("Chapter.m4b", entry.Name);
            using (var stream = MediaBrowser.Controller.Library.LivePathLease.OpenRead(entry.File.FullPath))
            using (var reader = new StreamReader(stream))
            {
                Assert.Equal("owned SMB bytes", reader.ReadToEnd());
            }

            var offline = media.FullName + "-unavailable";
            Directory.Move(media.FullName, offline);
            Assert.ThrowsAny<IOException>(() => store.Browse(group.Id, TestContext.Current.CancellationToken));
            Directory.Move(offline, media.FullName);
            Assert.Equal(entry.Id, Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken)).Id);
            Assert.Equal("owned SMB bytes", File.ReadAllText(file));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
            File.Delete(file);
            Assert.Empty(store.Browse(group.Id, TestContext.Current.CancellationToken));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void OwnedSmbDrive_DisconnectAndReconnectKeepSavedIdsWithoutAScan()
    {
        RequireLocalSmb();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-live-smb-drive-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var file = Path.Combine(media.FullName, "Chapter.m4b");
            File.WriteAllText(file, "owned mapped SMB bytes");
            var timestamp = File.GetLastWriteTimeUtc(file);
            var remote = @"\\localhost\" + media.FullName[0] + "$" + media.FullName[2..];
            using var drive = new TestNetworkDrive(remote);
            var profile = Path.Combine(fixture.FullName, "Profile");
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile, "data"), [profile]);
            var group = store.AddLibrary("Mapped SMB", [drive.Root]);
            var entry = Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken));
            using (var stream = MediaBrowser.Controller.Library.LivePathLease.OpenRead(entry.File.FullPath))
            using (var reader = new StreamReader(stream))
            {
                Assert.Equal("owned mapped SMB bytes", reader.ReadToEnd());
            }

            drive.Disconnect();
            Assert.ThrowsAny<IOException>(() => store.Browse(group.Id, TestContext.Current.CancellationToken));
            drive.Connect();
            Assert.Equal(entry.Id, Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken)).Id);
            Assert.NotNull(store.GetEntry(entry.Id));
            Assert.Equal("owned mapped SMB bytes", File.ReadAllText(file));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void ChangedDriveMapping_IsRecheckedForSavedIdsWithoutHidingOtherRoots()
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-remap-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var other = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Other"));
            var profile = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Profile"));
            File.WriteAllText(Path.Combine(media.FullName, "Chapter.m4b"), "public media");
            File.WriteAllText(Path.Combine(profile.FullName, "Chapter.m4b"), "private data must not be exposed");
            using var drive = new TestDrive(media.FullName);
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader()), Path.Combine(profile.FullName, "data"), [profile.FullName]);
            var group = store.AddLibrary("Mapped", [drive.Root, other.FullName]);
            var mappedRoot = group.Roots[0];
            var entry = Assert.Single(store.Browse(mappedRoot.Id, TestContext.Current.CancellationToken));
            Assert.NotNull(store.GetEntry(entry.Id));
            drive.Repoint(profile.FullName);
            Assert.Throws<UnauthorizedAccessException>(() => store.GetEntry(entry.Id));
            Assert.Throws<UnauthorizedAccessException>(() => store.Browse(mappedRoot.Id, TestContext.Current.CancellationToken));
            var roots = store.Browse(group.Id, TestContext.Current.CancellationToken);
            Assert.True(Assert.Single(roots, root => root.Id.Equals(mappedRoot.Id)).IsUnavailable);
            Assert.False(Assert.Single(roots, root => root.Id.Equals(group.Roots[1].Id)).IsUnavailable);
            Assert.Empty(store.Browse(group.Roots[1].Id, TestContext.Current.CancellationToken));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoggingConfiguration_CannotRedirectWritesOrLoadConfiguredSinks(bool requestUnknownAssembly)
    {
        var fixture = Directory.CreateTempSubdirectory("jigglefin-private-logging-");
        var previous = Log.Logger;
        try
        {
            var profile = Path.Combine(fixture.FullName, "Profile");
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var paths = StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile });
            var forbidden = Path.Combine(media.FullName, "Must not be a log.txt");
            var settings = new Dictionary<string, string?>
            {
                ["Serilog:WriteTo:0:Name"] = "File",
                ["Serilog:WriteTo:0:Args:path"] = forbidden,
                ["Serilog:MinimumLevel:Default"] = "Debug"
            };
            if (requestUnknownAssembly)
            {
                settings["Serilog:Using:0"] = "Jigglefin.Test.MustNotLoadThisSinkAssembly";
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            StartupHelpers.InitializeLoggingFramework(configuration, paths);
            Log.Debug("Local-only log boundary sentinel");
            Log.CloseAndFlush();
            Assert.Empty(Directory.EnumerateFileSystemEntries(media.FullName));
            Assert.Contains(Directory.EnumerateFiles(paths.LogDirectoryPath), file => File.ReadAllText(file).Contains("Local-only log boundary sentinel", StringComparison.Ordinal));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previous;
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MappingChangesBetweenValidationAndReads_CannotExposePrivateStorage(bool beforeRootStat, bool lookup)
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-root-read-handoff-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var profile = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Profile"));
            const string MediaBytes = "original selected media";
            File.WriteAllText(Path.Combine(media.FullName, "Chapter.m4b"), MediaBytes);
            File.WriteAllText(Path.Combine(profile.FullName, "Chapter.m4b"), "private data must not be exposed");
            File.WriteAllText(Path.Combine(profile.FullName, "PrivateOnly.txt"), "not a media entry");
            using var drive = new TestDrive(media.FullName);
            var reader = new RemappingReader();
            var store = new LiveLibraryStore(new LiveDirectoryBrowser(reader), Path.Combine(profile.FullName, "data"), [profile.FullName]);
            var group = store.AddLibrary("Mapped", [drive.Root]);
            var original = Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken));
            if (beforeRootStat)
            {
                reader.BeforeStat = () => drive.Repoint(profile.FullName);
                if (lookup)
                {
                    Assert.Throws<UnauthorizedAccessException>(() => store.GetEntry(original.Id));
                }
                else
                {
                    Assert.Throws<UnauthorizedAccessException>(() => store.Browse(group.Id, TestContext.Current.CancellationToken));
                }
            }
            else
            {
                reader.AfterStat = () => drive.Repoint(profile.FullName);
                var entry = lookup ? store.GetEntry(original.Id)! : Assert.Single(store.Browse(group.Id, TestContext.Current.CancellationToken));
                Assert.Equal(original.Id, entry.Id);
                Assert.Equal(MediaBytes.Length, entry.File.Length);
                using var stream = LivePathLease.OpenRead(entry.File.ReadPath ?? entry.File.FullPath);
                using var content = new StreamReader(stream);
                Assert.Equal(MediaBytes, content.ReadToEnd());
            }

            // A new request rechecks the now-private mapping rather than trusting
            // the previous request's successful root validation.
            Assert.Throws<UnauthorizedAccessException>(() => store.Browse(group.Id, TestContext.Current.CancellationToken));
            Assert.Equal(MediaBytes, File.ReadAllText(Path.Combine(media.FullName, "Chapter.m4b")));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void SelectedMetadata_RemainsBoundAfterMappingChangesButNewRootSelectionRevalidates()
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-selected-root-handoff-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var profile = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Profile"));
            foreach (var directory in new[] { media, profile })
            {
                File.WriteAllText(Path.Combine(directory.FullName, "Chapter.m4b"), directory.Name);
                File.WriteAllText(Path.Combine(directory.FullName, "Chapter.nfo"), "<audiobook><title>" + directory.Name + "</title></audiobook>");
                File.WriteAllText(Path.Combine(directory.FullName, "folder.jpg"), directory.Name);
            }

            using var drive = new TestDrive(media.FullName);
            var browser = new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader());
            var store = new LiveLibraryStore(browser, Path.Combine(profile.FullName, "data"), [profile.FullName]);
            var group = store.AddLibrary("Mapped", [drive.Root]);
            var id = store.Browse(group.Id, TestContext.Current.CancellationToken).Single(entry => entry.Name == "Chapter.m4b").Id;
            var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
            using var items = new LiveItemService(store, browser, encoder.Object, NullLogger<LiveItemService>.Instance);
            var selected = items.Resolve(id)!;
            var selectedGroup = items.Resolve(group.Id)!;
            drive.Repoint(profile.FullName);
            items.LoadLocalMetadata(selected);
            Assert.Equal("Media", selected.Name);
            using var image = LivePathLease.OpenRead(Assert.Single(selected.ImageInfos).Path);
            using var content = new StreamReader(image);
            Assert.Equal("Media", content.ReadToEnd());
            Assert.Throws<UnauthorizedAccessException>(() => items.LoadLocalMetadata(selectedGroup));
            Assert.Null(items.Resolve(id));
            encoder.VerifyNoOtherCalls();
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackAndSidecars_KeepValidatedRootAndReprobeAChangedLocation(bool privateReplacement)
    {
        RequireWindows();
        var fixture = Directory.CreateTempSubdirectory("jigglefin-playback-root-handoff-");
        try
        {
            var media = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var profile = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Profile"));
            var replacement = privateReplacement ? profile : Directory.CreateDirectory(Path.Combine(fixture.FullName, "Other"));
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var directory in new[] { media, replacement })
            {
                // Same length/time forces cache isolation to depend on the resolved
                // location while the client-visible ID and bookmark stay stable.
                var video = Path.Combine(directory.FullName, "Movie.mp4");
                await File.WriteAllTextAsync(video, "same probe fixture", TestContext.Current.CancellationToken);
                File.SetLastWriteTimeUtc(video, timestamp);
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Movie.nfo"), "<movie><title>" + directory.Name + "</title></movie>", TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Movie.en.srt"), directory.Name, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, "folder.jpg"), directory.Name, TestContext.Current.CancellationToken);
            }

            using var drive = new TestDrive(media.FullName);
            var browser = new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader());
            var store = new LiveLibraryStore(browser, Path.Combine(profile.FullName, "data"), [profile.FullName]);
            var group = store.AddLibrary("Mapped", [drive.Root]);
            var id = store.Browse(group.Id, TestContext.Current.CancellationToken).Single(entry => entry.Name == "Movie.mp4").Id;
            var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
            var calls = 0;
            encoder.Setup(value => value.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((MediaInfoRequest request, CancellationToken _) =>
                {
                    var expected = calls++ == 0 ? media : replacement;
                    using var lease = LivePathLease.Acquire(Path.Combine(expected.FullName, "Movie.mp4"));
                    Assert.Equal(lease.ReadPath, request.MediaSource.Path);
                    if (calls == 1)
                    {
                        drive.Repoint(replacement.FullName);
                    }

                    return new MediaInfo { Container = "mp4", MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video }] };
                });
            using var items = new LiveItemService(store, browser, encoder.Object, NullLogger<LiveItemService>.Instance);
            var selected = items.Resolve(id)!;
            var original = await items.PreparePlayback(selected, TestContext.Current.CancellationToken);
            Assert.Equal("Media", selected.Name);
            using (var lease = LivePathLease.Acquire(Path.Combine(media.FullName, "Movie.mp4")))
            {
                Assert.Equal(lease.ReadPath, original.Path);
            }

            foreach (var path in new[] { Assert.Single(selected.ImageInfos).Path, Assert.Single(original.MediaStreams, stream => stream.IsExternal).Path })
            {
                using var stream = LivePathLease.OpenRead(path);
                using var content = new StreamReader(stream);
                Assert.Equal("Media", await content.ReadToEndAsync(TestContext.Current.CancellationToken));
            }

            if (privateReplacement)
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => items.PreparePlayback(selected, TestContext.Current.CancellationToken));
                Assert.Equal(1, calls);
            }
            else
            {
                var updated = await items.PreparePlayback(selected, TestContext.Current.CancellationToken);
                Assert.Equal("Other", selected.Name);
                Assert.Equal(id, selected.Id);
                Assert.NotEqual(original.Path, updated.Path);
                Assert.NotEqual(original.ETag, updated.ETag);
                Assert.Equal(2, calls);
            }
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows storage aliases require Windows.");
        }
    }

    private static void RequireLocalSmb()
    {
        RequireWindows();
        if (Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_LOCAL_SMB") != "1")
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_LOCAL_SMB=1 with the existing local administrative share readable; tests never create or change shares.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [SuppressMessage("Performance", "CA1838", Justification = "Bounded Windows test-fixture setup, not a production hot path.")]
    private static extern uint GetShortPathName(string path, StringBuilder shortPath, int size);

    private sealed class RemappingReader : ILiveDirectoryReader
    {
        private readonly PhysicalLiveDirectoryReader _physical = new();

        public Action? BeforeStat { get; set; }

        public Action? AfterStat { get; set; }

        public LiveFileInfo Stat(string path)
        {
            var before = BeforeStat;
            BeforeStat = null;
            before?.Invoke();
            var result = _physical.Stat(path);
            var after = AfterStat;
            AfterStat = null;
            after?.Invoke();
            return result;
        }

        public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
            => _physical.EnumerateDirectory(path, cancellationToken);
    }

    private sealed class TestNetworkDrive : IDisposable
    {
        private readonly string _device;
        private readonly string _remote;
        private bool _connected;

        public TestNetworkDrive(string remote)
        {
            _remote = remote;
            for (var letter = 'Y'; letter >= 'E'; letter--)
            {
                var candidate = letter + ":";
                if (QueryDosDevice(candidate, new StringBuilder(32768), 32768) == 0 && Marshal.GetLastWin32Error() == 2)
                {
                    _device = candidate;
                    Connect();
                    return;
                }
            }

            throw new InvalidOperationException("No unused drive letter is available for the owned SMB mapping.");
        }

        public string Root => _device + @"\";

        public void Connect()
        {
            Assert.False(_connected);
            Assert.Equal(0u, QueryDosDevice(_device, new StringBuilder(32768), 32768));
            Assert.Equal(2, Marshal.GetLastWin32Error());
            var resource = new NetworkResource { Type = 1, LocalName = _device, RemoteName = _remote };
            var error = WNetAddConnection2(ref resource, null, null, 4); // CONNECT_TEMPORARY; no prompts/credential changes.
            Assert.Equal(0, error);
            _connected = true;
        }

        public void Disconnect()
        {
            Assert.True(_connected);
            var remote = new StringBuilder(32768);
            var length = remote.Capacity;
            Assert.Equal(0, WNetGetConnection(_device, remote, ref length));
            Assert.Equal(_remote, remote.ToString(), ignoreCase: true);
            // The redirector can briefly retain a closed directory handle. A
            // bounded retry is safe; forcing an in-use mapping closed is not.
            var error = WNetCancelConnection2(_device, 0, false);
            for (var attempt = 0; error == 2401 && attempt < 20; attempt++)
            {
                System.Threading.Thread.Sleep(50);
                remote.Clear();
                length = remote.Capacity;
                Assert.Equal(0, WNetGetConnection(_device, remote, ref length));
                Assert.Equal(_remote, remote.ToString(), ignoreCase: true);
                error = WNetCancelConnection2(_device, 0, false);
            }

            Assert.Equal(0, error);
            _connected = false;
        }

        public void Dispose()
        {
            if (_connected)
            {
                Disconnect();
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [SuppressMessage("Performance", "CA1838", Justification = "Bounded Windows test-fixture setup, not a production hot path.")]
        private static extern uint QueryDosDevice(string device, StringBuilder target, int size);

        [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WNetAddConnection2(ref NetworkResource resource, string? password, string? username, uint flags);

        [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WNetCancelConnection2(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);

        [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [SuppressMessage("Performance", "CA1838", Justification = "Bounded Windows test-fixture verification, not a production hot path.")]
        private static extern int WNetGetConnection(string device, StringBuilder remote, ref int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NetworkResource
        {
            public uint Scope;
            public uint Type;
            public uint DisplayType;
            public uint Usage;
            public string? LocalName;
            public string RemoteName;
            public string? Comment;
            public string? Provider;
        }
    }

    private sealed class TestDrive : IDisposable
    {
        private readonly string _device;
        private readonly List<string> _targets = [];

        public TestDrive(string target, bool rawTarget = false)
        {
            var nativeTarget = rawTarget ? target : @"\??\" + target;
            // Never replace a user's mapping or use Z:. Cleanup removes only the
            // exact definition owned by this fixture and broadcasts no UI event.
            for (var letter = 'Y'; letter >= 'E'; letter--)
            {
                var candidate = letter + ":";
                if (QueryDosDevice(candidate, new StringBuilder(32768), 32768) == 0 && Marshal.GetLastWin32Error() == 2)
                {
                    _device = candidate;
                    if (!DefineDosDevice(1 | 8, _device, nativeTarget))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    _targets.Add(nativeTarget);
                    return;
                }
            }

            throw new InvalidOperationException("No unused test drive letter is available.");
        }

        public string Root => _device + @"\";

        public void Repoint(string target)
        {
            var rawTarget = @"\??\" + target;
            if (!DefineDosDevice(1 | 8, _device, rawTarget))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _targets.Add(rawTarget);
        }

        public void Dispose()
        {
            for (var index = _targets.Count - 1; index >= 0; index--)
            {
                if (!DefineDosDevice(1 | 2 | 4 | 8, _device, _targets[index]))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [SuppressMessage("Performance", "CA1838", Justification = "Bounded Windows test-fixture setup, not a production hot path.")]
        private static extern uint QueryDosDevice(string device, StringBuilder target, int size);

        [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DefineDosDevice(uint flags, string device, string target);
    }
}
