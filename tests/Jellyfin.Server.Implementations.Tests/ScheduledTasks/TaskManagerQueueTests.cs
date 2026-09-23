using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

public class TaskManagerQueueTests
{
    [Fact]
    public async Task CompletingAnotherTask_DoesNotDiscardQueuedFollowUpForRunningTask()
    {
        var tempDir = Directory.CreateTempSubdirectory("jigglefin-task-queue-");
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.ConfigurationDirectoryPath).Returns(tempDir.FullName);
        paths.Setup(p => p.DataPath).Returns(tempDir.FullName);

        using var manager = new TaskManager(paths.Object, NullLogger<TaskManager>.Instance);
        var blockingTask = new BlockingScheduledTask();
        manager.AddTasks([blockingTask, new UnrelatedScheduledTask()]);
        var blockingWorker = manager.ScheduledTasks.Single(t => t.ScheduledTask is BlockingScheduledTask);
        var unrelatedWorker = manager.ScheduledTasks.Single(t => t.ScheduledTask is UnrelatedScheduledTask);
        var followUpCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskCompleted += (_, e) =>
        {
            if (e.Task == blockingWorker && blockingTask.RunCount == 2)
            {
                followUpCompleted.TrySetResult();
            }
        };

        var firstRun = manager.Execute(blockingWorker, new TaskOptions());
        try
        {
            await blockingTask.FirstRunStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            manager.QueueScheduledTask<BlockingScheduledTask>();
            manager.QueueScheduledTask<BlockingScheduledTask>();

            // This unrelated completion used to drain and silently drop the queued scan.
            await manager.Execute(unrelatedWorker, new TaskOptions()).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, blockingTask.RunCount);

            blockingTask.ReleaseFirstRun();
            await followUpCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, blockingTask.RunCount);
        }
        finally
        {
            blockingTask.ReleaseFirstRun();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            tempDir.Delete(true);
        }
    }

    private sealed class BlockingScheduledTask : IScheduledTask
    {
        private readonly TaskCompletionSource _firstRunStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public string Name => nameof(BlockingScheduledTask);

        public string Key => nameof(BlockingScheduledTask);

        public string Description => string.Empty;

        public string Category => string.Empty;

        public int RunCount => Volatile.Read(ref _runCount);

        public Task FirstRunStarted => _firstRunStarted.Task;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runCount) == 1)
            {
                _firstRunStarted.TrySetResult();
                await _releaseFirstRun.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseFirstRun() => _releaseFirstRun.TrySetResult();
    }

    private sealed class UnrelatedScheduledTask : IScheduledTask
    {
        public string Name => nameof(UnrelatedScheduledTask);

        public string Key => nameof(UnrelatedScheduledTask);

        public string Description => string.Empty;

        public string Category => string.Empty;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
