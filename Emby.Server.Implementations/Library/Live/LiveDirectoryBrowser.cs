using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>
/// Lists the filesystem as it exists now, without consulting or populating a catalog.
/// The caller must authorize the configured root before invoking this service.
/// </summary>
public sealed class LiveDirectoryBrowser
{
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly ILiveDirectoryReader _reader;

    /// <summary>Initializes a new instance of the <see cref="LiveDirectoryBrowser"/> class.</summary>
    /// <param name="reader">The read-only filesystem adapter.</param>
    public LiveDirectoryBrowser(ILiveDirectoryReader reader)
    {
        _reader = reader;
    }

    /// <summary>Validates a root's own attributes without reading its directory contents.</summary>
    /// <param name="name">The configured display name.</param>
    /// <param name="path">The fully qualified root path.</param>
    /// <returns>A stable root descriptor.</returns>
    public LiveMediaRoot Mount(string name, string path)
    {
        var root = DescribeRoot(name, path);
        RequireDirectory(_reader.Stat(root.FullPath));
        return root;
    }

    /// <summary>Creates a root identity from configuration alone, without accessing even an offline drive.</summary>
    /// <param name="name">The configured display name.</param>
    /// <param name="path">The fully qualified configured path.</param>
    /// <returns>The root address. Normal navigation still revalidates its attributes.</returns>
    public static LiveMediaRoot DescribeRoot(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var fullPath = NormalizeRootPath(path);
        return new LiveMediaRoot(CreateId("root", CanonicalIdentityPath(fullPath)), name, fullPath);
    }

    /// <summary>Normalizes a configured path without accepting Windows device/ADS/trailing-dot aliases.</summary>
    /// <param name="path">An explicit absolute directory location.</param>
    /// <returns>The canonical lexical path, without expanding short names or accessing the filesystem.</returns>
    public static string NormalizeRootPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A media root must have a fully qualified path.", nameof(path));
        }

        if (OperatingSystem.IsWindows())
        {
            var syntax = path.Replace('/', '\\');
            if (syntax.StartsWith(@"\\?\", StringComparison.Ordinal) || syntax.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                throw new ArgumentException("Device/extended path aliases are not supported for configured locations.", nameof(path));
            }

            var components = syntax[(Path.GetPathRoot(syntax)?.Length ?? 0)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in components)
            {
                if (component is not ("." or "..") && (component.EndsWith('.') || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    throw new ArgumentException("Configured locations cannot contain ambiguous Windows path components.", nameof(path));
                }
            }

            // Unlike .NET Path.GetFullPath, GetFullPathNameW does not expand
            // existing 8.3 names. It is lexical and cannot inspect an offline
            // media root merely to create its configuration/address identity.
            if (syntax.Length >= 32768)
            {
                throw new PathTooLongException("The configured path exceeds Windows' path limit.");
            }

            var buffer = new StringBuilder(Math.Max(256, syntax.Length + 1));
            var length = GetFullPathName(syntax, buffer.Capacity, buffer, IntPtr.Zero);
            if (length >= buffer.Capacity && length < 32768)
            {
                buffer.EnsureCapacity((int)length + 1);
                length = GetFullPathName(syntax, buffer.Capacity, buffer, IntPtr.Zero);
            }

            if (length == 0)
            {
                throw new ArgumentException("Invalid configured path.", nameof(path), new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (length >= buffer.Capacity)
            {
                throw new PathTooLongException("The configured path exceeds Windows' path limit.");
            }

            return Path.TrimEndingDirectorySeparator(buffer.ToString());
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>Reads one entry's current attributes, without loading metadata.</summary>
    /// <param name="root">The authorized configured root.</param>
    /// <param name="relativePath">The relative path, or empty for the root itself.</param>
    /// <returns>The current entry.</returns>
    public LiveDirectoryEntry GetEntry(LiveMediaRoot root, string relativePath)
    {
        var fullPath = ResolveWithinRoot(root, relativePath);
        var info = StatWithoutFollowingLinks(root, fullPath);
        return Describe(root, info);
    }

    /// <summary>Reads only the selected directory, never its descendants.</summary>
    /// <param name="root">The authorized configured root.</param>
    /// <param name="relativePath">The relative directory path, or empty for the root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The immediate filesystem entries, with no cached membership.</returns>
    public IReadOnlyList<LiveDirectoryEntry> Browse(LiveMediaRoot root, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolveWithinRoot(root, relativePath);
        RequireDirectory(StatWithoutFollowingLinks(root, fullPath));
        var entries = new List<LiveDirectoryEntry>();
        foreach (var info in _reader.EnumerateDirectory(fullPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetDirectoryName(info.FullPath), fullPath, _pathComparison))
            {
                throw new IOException("The directory reader returned an entry outside the requested directory.");
            }

            entries.Add(Describe(root, info));
        }

        return entries;
    }

    private static string ResolveWithinRoot(LiveMediaRoot root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A media entry must use a relative path.", nameof(relativePath));
        }

        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || (OperatingSystem.IsWindows() && (segment.EndsWith(' ') || segment.EndsWith('.'))))
            {
                throw new ArgumentException("The media entry path contains an unsafe segment.", nameof(relativePath));
            }
        }

        var fullPath = NormalizeRootPath(Path.Combine(root.FullPath, relativePath));
        var rootPrefix = Path.EndsInDirectorySeparator(root.FullPath) ? root.FullPath : root.FullPath + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, root.FullPath, _pathComparison) && !fullPath.StartsWith(rootPrefix, _pathComparison))
        {
            throw new ArgumentException("The media entry path escapes its root.", nameof(relativePath));
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private LiveFileInfo StatWithoutFollowingLinks(LiveMediaRoot root, string fullPath)
    {
        var info = _reader.Stat(root.FullPath);
        RequireDirectory(info);
        var relative = RelativeWithinRoot(root.FullPath, fullPath);
        if (relative == ".")
        {
            return info;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar);
        var current = root.FullPath;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            info = _reader.Stat(current);
            if (info.IsLink)
            {
                throw new UnauthorizedAccessException("Link/reparse entries cannot be traversed or opened.");
            }

            if (index < segments.Length - 1)
            {
                RequireDirectory(info);
            }
        }

        return info;
    }

    private static void RequireDirectory(LiveFileInfo info)
    {
        if (info.IsLink)
        {
            throw new UnauthorizedAccessException("A live directory cannot be a link/reparse point.");
        }

        if (!info.IsDirectory)
        {
            throw new IOException("The requested path is not a directory.");
        }
    }

    private static LiveDirectoryEntry Describe(LiveMediaRoot root, LiveFileInfo info)
    {
        var relativePath = RelativeWithinRoot(root.FullPath, info.FullPath);
        if (relativePath == ".")
        {
            return new LiveDirectoryEntry(root.Id, root.Id, null, root.Name, string.Empty, info);
        }

        var parent = Path.GetDirectoryName(relativePath);
        var parentId = string.IsNullOrEmpty(parent) ? root.Id : EntryId(root, parent);
        return new LiveDirectoryEntry(EntryId(root, relativePath), root.Id, parentId, Path.GetFileName(info.FullPath), relativePath, info);
    }

    /// <summary>Computes a saved path's identity without loading or trusting its filesystem contents.</summary>
    /// <param name="root">The configured root.</param>
    /// <param name="relativePath">A validated relative path.</param>
    /// <returns>The stable entry ID.</returns>
    public static Guid EntryId(LiveMediaRoot root, string relativePath)
    {
        var path = ResolveWithinRoot(root, relativePath);
        var relative = RelativeWithinRoot(root.FullPath, path);
        return relative == "." ? root.Id : CreateId(root.Id.ToString("N"), CanonicalIdentityPath(relative));
    }

    internal static string RelativeWithinRoot(string root, string path)
    {
        if (string.Equals(root, path, _pathComparison))
        {
            return ".";
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, _pathComparison))
        {
            throw new ArgumentException("The media path is outside its configured root.", nameof(path));
        }

        return path[prefix.Length..];
    }

    private static string CanonicalIdentityPath(string path)
        => OperatingSystem.IsWindows() ? path.Replace('\\', '/').ToUpperInvariant() : path;

    [DllImport("kernel32.dll", EntryPoint = "GetFullPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFullPathName(string path, int size, StringBuilder fullPath, IntPtr filePart);

    private static Guid CreateId(string scope, string path)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("jigglefin/live/v1\0" + scope + "\0" + path));
        return new Guid(digest.AsSpan(0, 16));
    }
}
