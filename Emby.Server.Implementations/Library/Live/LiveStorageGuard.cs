using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Rejects unsafe private paths before startup creates any folders, logs, caches or markers.</summary>
public static class LiveStorageGuard
{
    /// <summary>Validates writable locations against existing live and legacy root configuration, without accessing media.</summary>
    /// <param name="programData">The profile directory.</param>
    /// <param name="config">The configuration directory.</param>
    /// <param name="cache">The startup cache directory.</param>
    /// <param name="logs">The log directory.</param>
    public static void Validate(string programData, string config, string cache, string logs)
    {
        var data = Path.Combine(programData, "data");
        var metadata = Path.Combine(programData, "metadata");
        var writable = new List<string> { programData, config, cache, logs, Path.Combine(Path.GetTempPath(), "jigglefin") };
        // Check only the configuration's own location before learning the media
        // roots. A bad cache/log location is rejected lexically before statting it.
        CheckPrivateAncestors(programData);
        CheckPrivateAncestors(config);

        var system = LegacyFolderConfiguration.ReadXml(Path.Combine(config, "system.xml"));
        var encoding = LegacyFolderConfiguration.ReadXml(Path.Combine(config, "encoding.xml"));
        var customMetadata = Field(system, "MetadataPath");
        if (!string.IsNullOrWhiteSpace(customMetadata))
        {
            metadata = customMetadata;
        }

        writable.Add(metadata);
        writable.AddRange(new[] { Field(system, "CachePath"), Field(encoding, "TranscodingTempPath") }.OfType<string>().Where(path => !string.IsNullOrWhiteSpace(path)));
        string Expand(string path) => path.Replace("%AppDataPath%", data, StringComparison.OrdinalIgnoreCase).Replace("%MetadataPath%", metadata, StringComparison.OrdinalIgnoreCase);
        var roots = LegacyFolderConfiguration.Read(Path.Combine(programData, "root", "default"))
            .SelectMany(group => group.Paths).Select(Expand).ToList();
        CheckOverlap(roots, writable);

        var database = Path.Combine(data, "live-folders", "live-folders.db");
        if (File.Exists(database))
        {
            CheckPrivateAncestors(database);
            // Our configuration store uses SQLite's rollback journal, not WAL.
            // Do not open a foreign WAL database which could create a shared-memory
            // sidecar before its root locations have been checked.
            if (File.Exists(database + "-wal"))
            {
                throw new InvalidDataException("Live folder configuration has an unexpected WAL file. Restore a closed, consistent profile before starting.");
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Definition FROM Libraries";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var library = JsonSerializer.Deserialize<LiveLibraryDefinition>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid live folder configuration.");
                roots.AddRange(library.Roots.Select(root => root.FullPath));
            }
        }

        CheckOverlap(roots, writable);
        foreach (var path in writable)
        {
            CheckPrivateAncestors(path);
        }
    }

    private static string? Field(XDocument? document, string name)
        => document?.Root?.Elements().FirstOrDefault(node => node.Name.LocalName == name)?.Value;

    private static void CheckOverlap(IEnumerable<string> roots, IReadOnlyList<string> writable)
    {
        foreach (var root in roots)
        {
            if (!Path.IsPathFullyQualified(root))
            {
                throw new InvalidDataException("A configured media root is not fully qualified.");
            }

            if (writable.Any(path => Contains(path, root) || Contains(root, path)))
            {
                throw new InvalidDataException("Private profile/cache/log/metadata/transcode locations overlap a media root. Move the private location outside media before starting; no startup files were created.");
            }
        }
    }

    private static bool Contains(string parent, string child)
    {
        parent = LiveDirectoryBrowser.NormalizeRootPath(parent);
        child = LiveDirectoryBrowser.NormalizeRootPath(child);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }

    private static void CheckPrivateAncestors(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Private server locations must use fully qualified paths.");
        }

        string? current = LiveDirectoryBrowser.NormalizeRootPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Private server locations must not traverse symbolic links or reparse points.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
