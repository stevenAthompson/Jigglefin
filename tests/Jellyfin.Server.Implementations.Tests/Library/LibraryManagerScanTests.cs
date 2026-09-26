using System;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Naming.Common;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Moq;
using Xunit;
using ServerLibraryManager = Emby.Server.Implementations.Library.LibraryManager;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class LibraryManagerScanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartScanInBackground_DoesNotQueueOrAccessFilesystem(bool scanRunning)
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        var tasks = fixture.Freeze<Mock<ITaskManager>>();
        var filesystem = fixture.Freeze<Mock<IFileSystem>>();
        var manager = fixture.Create<ServerLibraryManager>();
        filesystem.Invocations.Clear();
        typeof(ServerLibraryManager).GetProperty(nameof(ServerLibraryManager.IsScanRunning))!.SetValue(manager, scanRunning);

        await manager.StartScanInBackground().ConfigureAwait(true);

        tasks.Verify(t => t.QueueScheduledTask<RefreshMediaLibraryTask>(), Times.Never());
        tasks.Verify(t => t.CancelIfRunningAndQueue<RefreshMediaLibraryTask>(), Times.Never());
        filesystem.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LegacyValidationEntryPoints_CompleteWithoutScansOrFilesystemAccess()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        var tasks = fixture.Freeze<Mock<ITaskManager>>();
        var filesystem = fixture.Freeze<Mock<IFileSystem>>();
        var manager = fixture.Create<ServerLibraryManager>();
        filesystem.Invocations.Clear();
        var progress = new InlineProgress();

        await manager.ValidateMediaLibrary(progress, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await manager.ValidateMediaLibraryInternal(progress, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await manager.ValidateTopLibraryFolders(TestContext.Current.CancellationToken).ConfigureAwait(true);
        manager.QueueLibraryScan();

        Assert.Equal(100, progress.Value);
        tasks.Verify(t => t.CancelIfRunningAndQueue<RefreshMediaLibraryTask>(), Times.Never());
        tasks.Verify(t => t.QueueScheduledTask<RefreshMediaLibraryTask>(), Times.Never());
        filesystem.VerifyNoOtherCalls();
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public double Value { get; private set; }

        public void Report(double value) => Value = value;
    }
}
