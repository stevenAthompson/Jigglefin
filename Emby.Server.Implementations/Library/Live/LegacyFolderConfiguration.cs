using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Reads bounded private configuration only, never the media locations it describes.</summary>
public static class LegacyFolderConfiguration
{
    /// <summary>A legacy group's settings and private configuration location.</summary>
    /// <param name="Name">The display name.</param>
    /// <param name="ConfigurationPath">The private group directory.</param>
    /// <param name="Enabled">Whether the group is enabled.</param>
    /// <param name="Paths">Configured media locations, not discovered directory entries.</param>
    public sealed record Group(string Name, string ConfigurationPath, bool Enabled, IReadOnlyList<string> Paths);

    /// <summary>Reads explicit folder settings without resolving shortcuts or accessing their targets.</summary>
    /// <param name="viewsPath">The private root/default directory.</param>
    /// <returns>Configured groups.</returns>
    public static IReadOnlyList<Group> Read(string viewsPath)
    {
        if (!Exists(viewsPath))
        {
            return [];
        }

        RequireRegularEntry(viewsPath, true);
        var groups = new List<Group>();
        foreach (var directory in Directory.EnumerateDirectories(viewsPath).Order(StringComparer.Ordinal))
        {
            RequireRegularEntry(directory, true);
            var document = ReadXml(Path.Combine(directory, "options.xml"));
            var enabled = document?.Root?.Elements().FirstOrDefault(node => node.Name.LocalName == "Enabled")?.Value;
            var pathInfos = document?.Root?.Elements().FirstOrDefault(node => node.Name.LocalName == "PathInfos");
            var paths = pathInfos?.Elements().SelectMany(node => node.Elements()).Where(node => node.Name.LocalName == "Path")
                .Select(node => node.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
            if (paths.Length == 0)
            {
                paths = Directory.EnumerateFiles(directory, "*.mblink", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)
                    .Select(path =>
                    {
                        RequireRegularEntry(path, false);
                        return File.ReadAllText(path).Trim();
                    }).ToArray();
            }

            groups.Add(new Group(Path.GetFileName(directory), directory, enabled is null || bool.Parse(enabled), paths));
        }

        return groups;
    }

    /// <summary>Reads optional private XML with no external entities or unbounded input.</summary>
    /// <param name="path">The exact private file.</param>
    /// <returns>The document, or null only if the path does not exist.</returns>
    public static XDocument? ReadXml(string path)
    {
        if (!Exists(path))
        {
            return null;
        }

        RequireRegularEntry(path, false);
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        return XDocument.Load(reader);
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void RequireRegularEntry(string path, bool directory)
    {
        var entry = new PhysicalLiveDirectoryReader().Stat(path);
        if (entry.IsLink || entry.IsDirectory != directory || (!directory && entry.Length > 1024 * 1024))
        {
            throw new InvalidDataException("Private configuration must use bounded regular files/directories, not links.");
        }

        if (!directory)
        {
            LivePrivateFiles.ValidateFile(path);
        }
    }
}
