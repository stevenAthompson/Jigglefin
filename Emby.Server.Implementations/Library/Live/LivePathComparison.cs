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

    public static bool OverlapsPrivateStorage(string media, string privatePath)
    {
        if (Overlaps(media, privatePath))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var mediaIdentities = Identities(media);
        var privateIdentities = Identities(privatePath);
        if (!mediaIdentities.Exists(path => path.Contains('~', StringComparison.Ordinal))
            && !privateIdentities.Exists(path => path.Contains('~', StringComparison.Ordinal)))
        {
            return false;
        }

        // Short aliases must not disguise writable storage, but resolving the
        // media path would itself touch media during startup. Inspect only the
        // configured private location (or its nearest existing ancestor).
        var longPrivate = Path.GetFullPath(privatePath);
        var shortPrivate = ShortPrivatePath(longPrivate);
        var longIdentities = Identities(longPrivate, expandShortNames: true);
        var shortIdentities = Identities(shortPrivate);
        foreach (var candidate in mediaIdentities)
        {
            foreach (var longIdentity in longIdentities)
            {
                foreach (var shortIdentity in shortIdentities)
                {
                    if (AliasComponentsOverlap(candidate, longIdentity, shortIdentity))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static bool Overlaps(string first, string second)
    {
        first = Identity(first, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        second = Identity(second, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (Contains(first, second) || Contains(second, first))
        {
            return true;
        }

        // A UNC spelling can name this machine even when its host is an alias.
        // Inspect only the local share table (never resolve/contact that host).
        // Conservatively treat a same-named local share as another possible
        // identity; a remote alias must not hide local private storage.
        var firstLocal = LocalShareIdentity(first);
        var secondLocal = LocalShareIdentity(second);
        if ((firstLocal is not null && (Contains(firstLocal, second) || Contains(second, firstLocal)))
            || (secondLocal is not null && (Contains(secondLocal, first) || Contains(first, secondLocal)))
            || (firstLocal is not null && secondLocal is not null && (Contains(firstLocal, secondLocal) || Contains(secondLocal, firstLocal))))
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

    private static List<string> Identities(string path, bool expandShortNames = false)
    {
        var primary = Identity(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase), expandShortNames);
        var result = new List<string> { primary };
        if (LocalShareIdentity(primary, expandShortNames) is { } local)
        {
            result.Add(local);
        }

        return result;
    }

    private static bool AliasComponentsOverlap(string media, string longPrivate, string shortPrivate)
    {
        var candidate = media.Split('\\');
        var longParts = longPrivate.Split('\\');
        var shortParts = shortPrivate.Split('\\');
        if (longParts.Length != shortParts.Length)
        {
            return false;
        }

        for (var index = 0; index < Math.Min(candidate.Length, longParts.Length); index++)
        {
            if (!string.Equals(candidate[index], longParts[index], StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate[index], shortParts[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string ShortPrivatePath(string path)
    {
        var remaining = new Stack<string>();
        var current = path;
        while (true)
        {
            var buffer = new StringBuilder(32768);
            var length = GetShortPathName(current, buffer, buffer.Capacity);
            if (length > 0 && length < buffer.Capacity)
            {
                var result = buffer.ToString();
                while (remaining.Count > 0)
                {
                    result = Path.Combine(result, remaining.Pop());
                }

                return result;
            }

            var error = Marshal.GetLastWin32Error();
            var parent = Path.GetDirectoryName(current);
            if (length > 0 || error is not (2 or 3) || parent is null)
            {
                throw new IOException("Could not inspect short-name aliases of private storage.", new Win32Exception(error));
            }

            remaining.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    private static string? LocalShareIdentity(string path, bool expandShortNames = false)
    {
        if (!OperatingSystem.IsWindows() || !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = path[2..].Split('\\', 3);
        if (parts.Length < 2)
        {
            throw new InvalidDataException("A configured share requires a server and share name.");
        }

        var error = NetShareGetInfo(null, parts[1], 2, out var buffer);
        try
        {
            if (error is 2310 or 2114) // NERR_NetNameNotFound / NERR_ServerNotStarted.
            {
                return null;
            }

            if (error != 0)
            {
                throw new IOException("Could not inspect local share aliases before accessing configured storage.", new Win32Exception(error));
            }

            var share = Marshal.PtrToStructure<ShareInfo>(buffer);
            if ((share.Type & 0xFF) != 0 || string.IsNullOrEmpty(share.Path))
            {
                return null; // Not a disk-tree share.
            }

            return Identity(Path.Combine(share.Path, parts.Length == 3 ? parts[2] : string.Empty), new HashSet<string>(StringComparer.OrdinalIgnoreCase), expandShortNames);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }
    }

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

    private static string Identity(string path, HashSet<string> seen, bool expandShortNames = false)
    {
        path = expandShortNames ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : LiveDirectoryBrowser.NormalizeRootPath(path);
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
            return Identity(@"\\" + target[8..] + path[2..], seen, expandShortNames);
        }

        if (target.StartsWith(@"\??\", StringComparison.Ordinal) && target.Length > 6 && target[5] == ':')
        {
            return Identity(target[4..].TrimEnd('\\') + path[2..], seen, expandShortNames);
        }

        if (target.StartsWith(@"\Device\LanmanRedirector\", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase))
        {
            // The DOS-device target already contains the provider's UNC name.
            // Read that local mapping without asking the provider to reconnect.
            var remainder = target[(target.IndexOf('\\', 8) + 1)..];
            while (remainder.StartsWith(';'))
            {
                var separator = remainder.IndexOf('\\', StringComparison.Ordinal);
                if (separator < 0)
                {
                    break;
                }

                remainder = remainder[(separator + 1)..];
            }

            if (!remainder.StartsWith(';') && remainder.Contains('\\', StringComparison.Ordinal))
            {
                return Identity(@"\\" + remainder.TrimEnd('\\') + path[2..], seen, expandShortNames);
            }

            return NetworkDrivePrefix + path;
        }

        // Volume identity also catches two ordinary letters for the same volume.
        // This is comparison data only, never a path passed to file APIs.
        return target.TrimEnd('\\') + path[2..];
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string path, StringBuilder shortPath, int size);

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WNetGetConnection(string device, StringBuilder target, ref int size);

    [DllImport("netapi32.dll", EntryPoint = "NetShareGetInfo", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetShareGetInfo(string? server, string name, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo
    {
        public string Name;
        public uint Type;
        public string Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        public string Path;
        public string Password;
    }
}
