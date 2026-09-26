using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Api.Tests.Helpers;

public sealed class LiveReadResultTests
{
    [Fact]
    public async Task StaticResult_OwnsReadLeaseAfterActionReturnsUntilResponseDisposal()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows filesystem sharing is required.");
        }

        var fixture = Directory.CreateTempSubdirectory("jigglefin-read-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Selected"));
            var file = Path.Combine(directory.FullName, "Track.mp3");
            await File.WriteAllBytesAsync(file, [1, 2, 3], TestContext.Current.CancellationToken);
            var response = Assert.IsType<FileStreamResult>(FileStreamResponseHelpers.GetProtectedFileResult(file, "audio/mpeg"));
            await using (response.FileStream)
            {
                Assert.True(response.EnableRangeProcessing);
                Assert.NotNull(response.LastModified);
                Assert.ThrowsAny<IOException>(() => Directory.Move(directory.FullName, directory.FullName + "-moved"));
                response.FileStream.Position = 1;
                var bytes = new byte[2];
                Assert.Equal(2, await response.FileStream.ReadAsync(bytes, TestContext.Current.CancellationToken));
                Assert.Equal(new byte[] { 2, 3 }, bytes);
            }

            Directory.Move(directory.FullName, directory.FullName + "-moved");
        }
        finally
        {
            fixture.Delete(true);
        }
    }
}
