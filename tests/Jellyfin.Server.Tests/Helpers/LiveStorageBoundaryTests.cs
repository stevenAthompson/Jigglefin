using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Server.Helpers;
using Microsoft.Extensions.Configuration;
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
            Assert.Throws<InvalidDataException>(() => StartupHelpers.CreateApplicationPaths(new StartupOptions { DataDir = profile, CacheDir = unsafeCache }));
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

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows storage aliases require Windows.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

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
        private static extern uint QueryDosDevice(string device, StringBuilder target, int size);

        [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DefineDosDevice(uint flags, string device, string target);
    }
}
