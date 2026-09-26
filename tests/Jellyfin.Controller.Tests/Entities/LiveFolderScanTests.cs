using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

public sealed class LiveFolderScanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyValidation_IsNoOpWithoutFilesystemOrProviderDependencies(bool recursive)
    {
        var folder = new Folder();
        var progress = new InlineProgress();
        // There are deliberately no configured static library/provider/filesystem services.
        await folder.ValidateChildren(progress, null!, recursive, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(100, progress.Value);
    }

    [Fact]
    public async Task LegacyValidation_StillHonorsCancellation()
    {
        var folder = new Folder();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => folder.ValidateChildren(new InlineProgress(), null!, cancellationToken: cancellation.Token));
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public double Value { get; private set; }

        public void Report(double value) => Value = value;
    }
}
