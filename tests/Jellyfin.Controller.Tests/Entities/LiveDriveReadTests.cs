using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Controller.Tests.Entities;

public sealed class LiveDriveReadTests
{
    [Fact]
    public async Task SubstitutedDrive_CannotHideAReparseAncestor()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows SUBST/junction regression.");
        }

        var fixture = Directory.CreateTempSubdirectory("jigglefin-hidden-ancestor-");
        var junction = Path.Combine(fixture.FullName, "Linked");
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Private"));
            var child = Directory.CreateDirectory(Path.Combine(target.FullName, "Child"));
            await File.WriteAllTextAsync(Path.Combine(child.FullName, "Secret.txt"), "must not be read", TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + junction.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" + target.FullName.Replace("'", "''", StringComparison.Ordinal) + "' -ErrorAction Stop | Out-Null");
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            using var drive = new OwnedDrive(Path.Combine(junction, "Child"));
            Assert.Throws<UnauthorizedAccessException>(() => LivePathLease.OpenRead(Path.Combine(drive.Root, "Secret.txt")));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction); // Remove only this owned junction, never its target.
            }

            fixture.Delete(true);
        }
    }

    [Fact]
    public void RawDeviceAliasWithHiddenAncestors_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows raw device alias regression.");
        }

        var fixture = Directory.CreateTempSubdirectory("jigglefin-raw-alias-read-");
        try
        {
            using var drive = new OwnedDrive(fixture.FullName, rawDevicePath: true);
            Assert.Throws<IOException>(() => LivePathLease.Acquire(drive.Root));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Fact]
    public void ResolvedVolumeAddresses_AreInternalOnlyAndRemainPinnedOnNestedReads()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows stable volume paths.");
        }

        var fixture = Directory.CreateTempSubdirectory("jigglefin-volume-read-");
        try
        {
            var file = Path.Combine(fixture.FullName, "Selected.bin");
            File.WriteAllText(file, "selected bytes");
            using var lease = LivePathLease.Acquire(file);
            Assert.StartsWith(@"\\?\Volume{", lease.ReadPath, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<ArgumentException>(() => LivePathLease.Acquire(lease.ReadPath));
            using var nested = LivePathLease.AcquireReadPath(lease.ReadPath);
            Assert.Equal(lease.ReadPath, nested.ReadPath);
            Assert.Equal("selected bytes", File.ReadAllText(nested.ReadPath));
            Assert.ThrowsAny<IOException>(() => File.Move(file, file + ".moved"));
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedReadAddress_DoesNotFollowDriveRemapping(bool nativeReader)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows drive alias regression.");
        }

        var ffmpeg = Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG");
        if (nativeReader && string.IsNullOrEmpty(ffmpeg))
        {
            throw SkipException.ForSkip("Supply JIGGLEFIN_TEST_FFMPEG to exercise the actual native reader.");
        }

        var fixture = Directory.CreateTempSubdirectory("jigglefin-drive-read-");
        try
        {
            var original = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Original"));
            var replacement = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Replacement"));
            // A tiny self-contained PCM WAV avoids fixture/library dependencies.
            var originalFile = Path.Combine(original.FullName, "Selected.wav");
            var bytes = new byte[844];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
            BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
            BitConverter.GetBytes(16).CopyTo(bytes, 16);
            BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
            BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
            BitConverter.GetBytes(8000).CopyTo(bytes, 24);
            BitConverter.GetBytes(16000).CopyTo(bytes, 28);
            BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
            BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
            BitConverter.GetBytes(800).CopyTo(bytes, 40);
            await File.WriteAllBytesAsync(originalFile, bytes, TestContext.Current.CancellationToken);
            var replacementBytes = (byte[])bytes.Clone();
            Array.Fill(replacementBytes, (byte)127, 44, 800);
            await File.WriteAllBytesAsync(Path.Combine(replacement.FullName, "Selected.wav"), replacementBytes, TestContext.Current.CancellationToken);
            var modified = File.GetLastWriteTimeUtc(originalFile);
            using var drive = new OwnedDrive(original.FullName);
            var logical = Path.Combine(drive.Root, "Selected.wav");
            using (var lease = LivePathLease.Acquire(logical))
            {
                drive.Repoint(replacement.FullName);
                Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(logical, TestContext.Current.CancellationToken));
                if (nativeReader)
                {
                    var start = new ProcessStartInfo(ffmpeg!)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        Arguments = OfflineMediaInput.Arguments + $"-v error -i file:\"{lease.ReadPath}\" -f hash -hash sha256 -"
                    };
                    using var process = Process.Start(start)!;
                    var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                    var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                    Assert.True(process.ExitCode == 0, await error);
                    Assert.Equal("SHA256=" + Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(44))), (await output).Trim());
                }
                else
                {
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(lease.ReadPath, TestContext.Current.CancellationToken));
                    await using var stream = LivePathLease.OpenRead(lease.ReadPath);
                    using var contents = new MemoryStream();
                    await stream.CopyToAsync(contents, TestContext.Current.CancellationToken);
                    Assert.Equal(bytes, contents.ToArray());
                }

                Assert.ThrowsAny<IOException>(() => File.Move(originalFile, originalFile + ".moved"));
            }

            Assert.Equal(modified, File.GetLastWriteTimeUtc(originalFile));
            File.Move(originalFile, originalFile + ".moved");
        }
        finally
        {
            fixture.Delete(true);
        }
    }

    private sealed class OwnedDrive : IDisposable
    {
        private readonly string _device;
        private readonly List<string> _targets = [];

        public OwnedDrive(string target, bool rawDevicePath = false)
        {
            // Never replace an existing definition or use the user's Z: drive.
            for (var letter = 'Y'; letter >= 'E'; letter--)
            {
                var candidate = letter + ":";
                if (QueryDosDevice(candidate, new StringBuilder(32768), 32768) == 0 && Marshal.GetLastWin32Error() == 2)
                {
                    _device = candidate;
                    if (rawDevicePath)
                    {
                        var volume = new StringBuilder(32768);
                        Assert.NotEqual(0u, QueryDosDevice(target[..2], volume, volume.Capacity));
                        AddDefinition(volume + target[2..]);
                    }
                    else
                    {
                        Repoint(target);
                    }

                    return;
                }
            }

            throw new InvalidOperationException("No unused drive letter for this owned fixture.");
        }

        public string Root => _device + @"\";

        public void Repoint(string target)
            => AddDefinition(@"\??\" + target);

        private void AddDefinition(string raw)
        {
            if (!DefineDosDevice(1 | 8, _device, raw))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _targets.Add(raw);
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
