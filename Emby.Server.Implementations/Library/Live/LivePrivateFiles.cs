using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Checks only private storage; never follows links or opens media content.</summary>
internal static class LivePrivateFiles
{
    public static void ValidateTree(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Private server storage must not contain symbolic links or reparse points: " + current);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(current))
                {
                    pending.Push(child);
                }
            }
            else
            {
                ValidateFile(current);
            }
        }
    }

    public static void ValidateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Read attributes only, without following the final reparse point or
        // reading file content. Never open a private file for writing to inspect it.
        var full = Path.GetFullPath(path);
        var native = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        using var handle = CreateFile(native, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("Could not validate a private server file: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (((FileAttributes)information.Attributes & FileAttributes.ReparsePoint) != 0 || information.NumberOfLinks != 1)
        {
            throw new InvalidDataException("Private server files must be independent regular files, not hardlinks or reparse points: " + path);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        public System.Runtime.InteropServices.ComTypes.FILETIME Access;
        public System.Runtime.InteropServices.ComTypes.FILETIME Write;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint IndexHigh;
        public uint IndexLow;
    }
}
