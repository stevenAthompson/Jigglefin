using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Controller.Tests.Entities;

public sealed class LivePathLeaseTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("jigglefin-read-lease-");

    [Fact]
    public void ActiveLease_PreventsAncestorAndFileReplacementButDoesNotWriteAnything()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows filesystem sharing is required.");
        }

        var directory = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Media", "Selected"));
        var path = Path.Combine(directory.FullName, "Media.mp4");
        File.WriteAllText(path, "original bytes");
        var modified = File.GetLastWriteTimeUtc(path);
        using (LivePathLease.Acquire(path))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(directory.FullName, directory.FullName + "-moved"));
            Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "replacement"));
            Assert.Equal("original bytes", File.ReadAllText(path));
            // Holding the selected path is not a tree-wide content lock.
            File.WriteAllText(Path.Combine(directory.FullName, "New sibling.txt"), "new sibling");
        }

        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Directory.Move(directory.FullName, directory.FullName + "-moved");
    }

    [Fact]
    public async Task ReadStream_HoldsLeaseUntilAsyncDisposalAndSupportsSeeking()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Media"));
        var path = Path.Combine(directory.FullName, "Selected.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        await using (var stream = LivePathLease.OpenRead(path))
        {
            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);
            Assert.False(stream.CanWrite);
            stream.Position = 1;
            var bytes = new byte[2];
            Assert.Equal(2, await stream.ReadAsync(bytes, TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 2, 3 }, bytes);
            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsAny<IOException>(() => Directory.Move(directory.FullName, directory.FullName + "-moved"));
            }
        }

        Directory.Move(directory.FullName, directory.FullName + "-moved");
    }

    [Fact]
    public void FailedAcquisition_ReleasesAlreadyOpenedAncestors()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Media"));
        Assert.Throws<FileNotFoundException>(() => LivePathLease.Acquire(Path.Combine(directory.FullName, "Missing.bin")));
        Directory.Move(directory.FullName, directory.FullName + "-moved");
    }

    [Fact]
    public async Task JunctionAtLeafOrAncestor_IsRejectedWithoutOpeningItsTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("This regression exercises a Windows directory junction.");
        }

        var target = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Private"));
        var secret = Path.Combine(target.FullName, "Secret.txt");
        await File.WriteAllTextAsync(secret, "must not be exposed", TestContext.Current.CancellationToken);
        var junction = Path.Combine(_fixture.FullName, "Media link");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + junction.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" + target.FullName.Replace("'", "''", StringComparison.Ordinal) + "' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        using var locked = File.Open(secret, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Throws<UnauthorizedAccessException>(() => LivePathLease.Acquire(junction));
        Assert.Throws<UnauthorizedAccessException>(() => LivePathLease.OpenRead(Path.Combine(junction, "Secret.txt")));
        // Remove the junction itself before recursive fixture cleanup visits its
        // target. This also proves rejection released the reparse-point handle.
        Directory.Delete(junction);
    }

    [Fact]
    public void RelativePath_IsRejectedBeforeOpeningAnything()
        => Assert.Throws<ArgumentException>(() => LivePathLease.Acquire("relative.mp4"));

    [Theory]
    [InlineData(@"\\.\pipe\jigglefin-test")]
    [InlineData(@"\\?\C:\Missing\secret.txt")]
    [InlineData(@"C:\Missing\secret.txt:stream")]
    [InlineData(@"C:\Missing\NUL.mp4")]
    [InlineData(@"C:\Missing\COM1.nfo")]
    [InlineData(@"C:\Missing\COM¹.mp3")]
    [InlineData(@"C:\Missing\folder.\secret.txt")]
    [InlineData(@"C:\Missing\folder \secret.txt")]
    [InlineData(@"C:\Missing\..\secret.txt")]
    public void WindowsAliases_AreRejectedBeforeFilesystemAccess(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows path syntax is required.");
        }

        Assert.Throws<ArgumentException>(() => LivePathLease.Acquire(path));
    }

    public void Dispose() => _fixture.Delete(true);
}
