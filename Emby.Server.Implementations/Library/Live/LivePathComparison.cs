using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Compares configured locations without opening or enumerating media.</summary>
internal static class LivePathComparison
{
    private const string NetworkDrivePrefix = "network-drive:";

    public static bool Overlaps(string first, string second)
    {
        first = Identity(first, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        second = Identity(second, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (Contains(first, second) || Contains(second, first))
        {
            return true;
        }

        var firstNetwork = IsNetwork(first);
        var secondNetwork = IsNetwork(second);
        if (firstNetwork != secondNetwork)
        {
            return false;
        }

        if (firstNetwork)
        {
            // An offline network mount must not prevent startup with ordinary
            // local private storage. Query the provider's resource name only
            // when comparing two network locations; otherwise the device
            // namespaces already distinguish them without consulting a provider.
            first = NetworkIdentity(first);
            second = NetworkIdentity(second);
        }

        return Contains(first, second) || Contains(second, first);
    }

    private static bool IsNetwork(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(NetworkDrivePrefix, StringComparison.Ordinal);

    private static string NetworkIdentity(string path)
    {
        if (!path.StartsWith(NetworkDrivePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        path = path[NetworkDrivePrefix.Length..];
        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        var error = WNetGetConnection(path[..2], buffer, ref length);
        if (error != 0)
        {
            throw new IOException("Could not establish whether configured network locations overlap.", new Win32Exception(error));
        }

        return LiveDirectoryBrowser.NormalizeRootPath(buffer.ToString().TrimEnd('\\') + path[2..]);
    }

    private static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison)
            || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }

    private static string Identity(string path, HashSet<string> seen)
    {
        path = LiveDirectoryBrowser.NormalizeRootPath(path);
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path;
        }

        var drive = path[..2];
        if (!seen.Add(drive) || seen.Count > 26)
        {
            throw new InvalidDataException("A configured drive alias contains a cycle.");
        }

        var buffer = new StringBuilder(32768);
        if (QueryDosDevice(drive, buffer, buffer.Capacity) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2)
            {
                // A disconnected root remains valid configuration. Re-evaluate
                // its mapping before every access, not just when it is mounted.
                return "unmapped:" + path;
            }

            throw new IOException("Could not inspect a configured drive mapping.", new Win32Exception(error));
        }

        var target = buffer.ToString(); // The first string is the current mapping.
        if (target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return Identity(@"\\" + target[8..] + path[2..], seen);
        }

        if (target.StartsWith(@"\??\", StringComparison.Ordinal) && target.Length > 6 && target[5] == ':')
        {
            return Identity(target[4..].TrimEnd('\\') + path[2..], seen);
        }

        if (target.StartsWith(@"\Device\LanmanRedirector\", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkDrivePrefix + path;
        }

        // Volume identity also catches two ordinary letters for the same volume.
        // This is comparison data only, never a path passed to file APIs.
        return target.TrimEnd('\\') + path[2..];
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int size);

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetGetConnection(string device, StringBuilder target, ref int size);
}
