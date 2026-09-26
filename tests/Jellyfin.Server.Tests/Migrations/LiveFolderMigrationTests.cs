using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Migrations.Routines;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class LiveFolderMigrationTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("jigglefin-migration-options-");
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Mock<ILiveDirectoryReader> _media = new(MockBehavior.Strict);
    private readonly LiveLibraryStore _folders;
    private readonly MigrateLiveFolders _migration;
    private readonly string _views;

    public LiveFolderMigrationTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        _views = Path.Combine(_fixture.FullName, "Private", "root", "default");
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(value => value.DefaultUserViewsPath).Returns(_views);
        JellyfinDbContext Database() => new(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(paths.Object, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        using (var database = Database())
        {
            database.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Database);
        var host = new Mock<IServerApplicationHost>();
        host.Setup(value => value.ExpandVirtualPath(It.IsAny<string>())).Returns((string path) => path);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var legacy = new Mock<ILibraryManager>();
        legacy.Setup(value => value.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string path, Type _) =>
        {
            if (!ids.TryGetValue(path, out var id))
            {
                ids[path] = id = Guid.NewGuid();
            }

            return id;
        });
        var statePath = Path.Combine(_fixture.FullName, "Private", "State");
        _folders = new LiveLibraryStore(new LiveDirectoryBrowser(_media.Object), statePath, [Path.Combine(_fixture.FullName, "Private")]);
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.SetupGet(value => value.Configuration).Returns(new ServerConfiguration());
        _migration = new MigrateLiveFolders(factory.Object, paths.Object, host.Object, legacy.Object, _folders, new LiveUserDataStore(statePath), configuration.Object, NullLogger<MigrateLiveFolders>.Instance);
    }

    [Fact]
    public async Task OfflineDisabledConfiguration_IsImportedOnceWithoutMediaAccess()
    {
        var missing = Path.Combine(_fixture.FullName, "Missing drive & path");
        var options = new XDocument(new XElement("LibraryOptions", new XElement("Enabled", false), new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", missing)))));
        await WriteOptions("Books", options.ToString());
        await _migration.PerformAsync(TestContext.Current.CancellationToken);
        var first = Assert.Single(_folders.GetLibraries());
        Assert.False(first.Enabled);
        Assert.Equal(missing, Assert.Single(first.Roots).FullPath);
        await _migration.PerformAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.Id, Assert.Single(_folders.GetLibraries()).Id);
        _media.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LegacyShortcut_IsConfigurationOnlyAndDoesNotNeedAnOnlineDrive()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_views, "Old shortcuts"));
        var missing = Path.Combine(_fixture.FullName, "Offline");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "One.mblink"), missing, TestContext.Current.CancellationToken);
        await _migration.PerformAsync(TestContext.Current.CancellationToken);
        Assert.Equal(missing, Assert.Single(Assert.Single(_folders.GetLibraries()).Roots).FullPath);
        _media.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExternalXmlEntity_IsRejectedBeforeAnyConfigurationIsImported()
    {
        await WriteOptions("A valid", "<LibraryOptions />");
        await WriteOptions("Z unsafe", "<!DOCTYPE LibraryOptions [<!ENTITY external SYSTEM 'https://example.invalid/never'>]><LibraryOptions>&external;</LibraryOptions>");
        await Assert.ThrowsAsync<XmlException>(() => _migration.PerformAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_folders.GetLibraries());
        _media.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PrivatePathOverlap_IsRejectedWithoutImportingEarlierGroups()
    {
        await WriteOptions("A valid", "<LibraryOptions />");
        var options = new XDocument(new XElement("LibraryOptions", new XElement("PathInfos", new XElement("MediaPathInfo", new XElement("Path", Path.Combine(_fixture.FullName, "Private"))))));
        await WriteOptions("Z unsafe", options.ToString());
        await Assert.ThrowsAsync<ArgumentException>(() => _migration.PerformAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_folders.GetLibraries());
        _media.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _fixture.Delete(true);
    }

    private async Task WriteOptions(string name, string xml)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_views, name));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "options.xml"), xml, TestContext.Current.CancellationToken);
    }
}
