using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Pins a Windows path from its volume/share root to the selected entry. Every
/// component is opened without following reparse points and without write/delete
/// sharing, so subsequent managed/native readers cannot race a name replacement.
/// This is not authorization; callers must authorize the selected live ID first.
/// </summary>
public sealed partial class LivePathLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];

    private LivePathLease()
    {
    }

    /// <summary>Protects an existing path until the returned lease is disposed.</summary>
    /// <param name="path">An authorized absolute file or directory path.</param>
    /// <returns>A lease which must outlive every consumer of the path.</returns>
    public static LivePathLease Acquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A protected read requires an absolute filesystem path.", nameof(path));
        }

        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsSyntax(path);
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var components = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var lease = new LivePathLease();
        try
        {
            var current = root;
            lease.Pin(current, components.Length > 0);
            for (var index = 0; index < components.Length; index++)
            {
                current = Path.Combine(current, components[index]);
                lease.Pin(current, index < components.Length - 1);
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Opens an asynchronous read stream which owns its path lease.</summary>
    /// <param name="path">The authorized absolute file path.</param>
    /// <returns>A read-only seekable stream; disposal also releases all path handles.</returns>
    public static FileStream OpenRead(string path)
    {
        var lease = Acquire(path);
        try
        {
            return new LeasedReadStream(path, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Leaf-to-root release; SafeHandle disposal is idempotent.
        for (var index = _handles.Count - 1; index >= 0; index--)
        {
            _handles[index].Dispose();
        }
    }

    private void Pin(string path, bool requireDirectory)
    {
        FileAttributes attributes;
        if (OperatingSystem.IsWindows())
        {
            // FILE_READ_DATA/FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
            // FILE_SHARE_READ, OPEN_EXISTING,
            // FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS.
            // Attribute-only handles do not enforce file sharing: requesting
            // read access is essential even though the lease never reads bytes.
            // Unlike managed File APIs, CreateFileW does not automatically enable
            // long paths. Only add this prefix to our canonical, validated path;
            // caller-supplied device/extended aliases remain forbidden.
            var nativePath = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;
            var handle = CreateFile(nativePath, 0x81, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw error switch
                {
                    2 => new FileNotFoundException("The selected path no longer exists.", path),
                    3 => new DirectoryNotFoundException("The selected directory no longer exists: " + path),
                    5 => new UnauthorizedAccessException("The selected path is not accessible: " + path),
                    _ => new IOException("Could not protect the selected path: " + path, new Win32Exception(error))
                };
            }

            _handles.Add(handle);
            if (!GetFileInformationByHandleEx(handle, 9, out var information, 8))
            {
                throw new IOException("Could not inspect the protected path: " + path, new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            attributes = (FileAttributes)information.Attributes;
        }
        else
        {
            // Jigglefin's supported server target is Windows. Preserve static
            // link rejection for upstream non-Windows development/tests, without
            // claiming Windows sharing guarantees exist on those platforms.
            attributes = File.GetAttributes(path);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Link/reparse paths cannot be read: " + path);
        }

        if (requireDirectory && (attributes & FileAttributes.Directory) == 0)
        {
            throw new DirectoryNotFoundException("A path ancestor is not a directory: " + path);
        }
    }

    private static void ValidateWindowsSyntax(string path)
    {
        var syntax = path.Replace('/', '\\');
        if (syntax.StartsWith(@"\\?\", StringComparison.Ordinal) || syntax.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Device/extended path aliases are not readable media paths.", nameof(path));
        }

        foreach (var component in syntax[(Path.GetPathRoot(syntax)?.Length ?? 0)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = component.Split('.')[0].ToUpperInvariant();
            var device = stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
            if (device || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Ambiguous/device/alternate-stream path components are not readable media paths.", nameof(path));
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTagInfo information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    private sealed class LeasedReadStream : FileStream
    {
        private LivePathLease? _lease;

        public LeasedReadStream(string path, LivePathLease lease)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan)
        {
            _lease = lease;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    Interlocked.Exchange(ref _lease, null)?.Dispose();
                }
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _lease, null)?.Dispose();
            }
        }
    }
}
