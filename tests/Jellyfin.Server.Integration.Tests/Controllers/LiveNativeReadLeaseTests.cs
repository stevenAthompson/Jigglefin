using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LiveNativeReadLeaseTests
{
    [Fact]
    public async Task RealNativeJob_PinsInputAfterStartReturnsAndReleasesItOnStopAndStartFailure()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG")))
        {
            throw SkipException.ForSkip("Requires Windows and JIGGLEFIN_TEST_FFMPEG for a real native read lease.");
        }

        using var fixture = new LiveFolderFixture(enableEncoder: true);
        fixture.Write("Selected/Chapter.m4b", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"), TestContext.Current.CancellationToken));
        await fixture.Start();
        var group = await fixture.AddGroup();
        var file = await fixture.Navigate(group.Id, "Selected", "Chapter.m4b");
        var items = fixture.Services.GetRequiredService<ILiveItemService>();
        var source = await items.PreparePlayback(items.Resolve(file.Id)!, TestContext.Current.CancellationToken);
        var manager = fixture.Services.GetRequiredService<ITranscodeManager>();
        var sources = fixture.Services.GetRequiredService<IMediaSourceManager>();
        var output = Path.Combine(Path.GetDirectoryName(fixture.Media)!, "Native output", "read-test.wav");
        using var state = new StreamState(sources, TranscodingJobType.Progressive, manager)
        {
            MediaSource = source, MediaPath = file.Path, InputProtocol = MediaProtocol.File,
            OutputFilePath = output, OutputAudioCodec = "pcm_s16le",
            Request = new StreamingRequestDto { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSourceId = source.Id, DeviceId = "isolated-native-read-lease" }
        };
        var directory = Path.GetDirectoryName(file.Path)!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Slow, finite native conversion, so an actual running process—not a
            // stale job record—proves the lease survives StartFfMpeg's return.
            var input = fixture.Services.GetRequiredService<IMediaEncoder>().GetInputArgument(file.Path, source);
            var command = OfflineMediaInput.Arguments + $"-re -stream_loop -1 -i {input} -t 20 -vn -c:a pcm_s16le -f wav -y \"{output}\"";
            var job = await manager.StartFfMpeg(state, output, command, Guid.Empty, TranscodingJobType.Progressive, cancellation);
            Assert.NotNull(job.Process);
            Assert.False(job.Process.HasExited);
            Assert.NotNull(job.InputPathLease);
            Assert.Contains(job.InputPathLease.ReadPath, job.Process.StartInfo.Arguments, StringComparison.Ordinal);
            Assert.DoesNotContain(file.Path, job.Process.StartInfo.Arguments, StringComparison.Ordinal);
            Assert.ThrowsAny<IOException>(() => Directory.Move(directory, directory + "-moved"));
            Assert.ThrowsAny<IOException>(() => File.Move(file.Path, file.Path + ".moved"));
            await manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => true);
            for (var count = 0; count < 100 && job.InputPathLease is not null; count++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.True(job.HasExited);
            Assert.Null(job.InputPathLease);
            Directory.Move(directory, directory + "-moved");
            Directory.Move(directory + "-moved", directory);
        }
        finally
        {
            await manager.KillTranscodingJobs(state.Request.DeviceId, state.Request.PlaySessionId, _ => true);
        }

        using var failureState = new StreamState(sources, TranscodingJobType.Progressive, manager)
        {
            MediaSource = source, MediaPath = Path.Combine(directory, "Missing.m4b"), InputProtocol = MediaProtocol.File,
            OutputFilePath = output + ".failed.wav", OutputAudioCodec = "pcm_s16le",
            Request = new StreamingRequestDto { PlaySessionId = Guid.NewGuid().ToString("N"), MediaSourceId = source.Id, DeviceId = "isolated-native-failure" }
        };
        using var failureCancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.StartFfMpeg(failureState, failureState.OutputFilePath, "-version", Guid.Empty, TranscodingJobType.Progressive, failureCancellation));
        Assert.Null(manager.GetTranscodingJob(failureState.Request.PlaySessionId));
        Directory.Move(directory, directory + "-moved");
    }
}
