using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>
/// Persists root configuration and GUID-to-path addresses, not directory membership or metadata.
/// Only explicit navigation discovers addresses. No method queries addresses to list children.
/// </summary>
public sealed class LiveLibraryStore : ILiveLibrary
{
    private readonly object _gate = new();
    private readonly LiveDirectoryBrowser _browser;
    private readonly string _dataDirectory;
    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="LiveLibraryStore"/> class.</summary>
    /// <param name="browser">The bounded read-only filesystem browser.</param>
    /// <param name="dataDirectory">Private server state, outside all media roots.</param>
    public LiveLibraryStore(LiveDirectoryBrowser browser, string dataDirectory)
    {
        _browser = browser;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dataDirectory, "live-folders.db"),
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Libraries (Id TEXT PRIMARY KEY, Definition TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Addresses (Id TEXT PRIMARY KEY, RootId TEXT NOT NULL, RelativePath TEXT NOT NULL);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<LiveLibraryDefinition> GetLibraries()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Definition FROM Libraries ORDER BY rowid";
            using var reader = command.ExecuteReader();
            var libraries = new List<LiveLibraryDefinition>();
            while (reader.Read())
            {
                libraries.Add(JsonSerializer.Deserialize<LiveLibraryDefinition>(reader.GetString(0))
                    ?? throw new InvalidDataException("Invalid live folder configuration."));
            }

            return libraries;
        }
    }

    /// <inheritdoc />
    public LiveLibraryDefinition AddLibrary(string name, IReadOnlyList<string> paths, Guid? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            var existing = GetLibraries();
            EnsureUniqueName(existing, name);
            var roots = new List<LiveMediaRoot>();
            foreach (var path in paths)
            {
                var root = Mount(existing, path);
                if (roots.Any(other => ContainsPath(other.FullPath, root.FullPath) || ContainsPath(root.FullPath, other.FullPath)))
                {
                    throw new ArgumentException("Media roots cannot overlap.", nameof(paths));
                }

                roots.Add(root);
            }

            var library = new LiveLibraryDefinition(id ?? Guid.NewGuid(), name, roots);
            if (existing.Any(item => item.Id.Equals(library.Id)))
            {
                throw new ArgumentException("The folder group ID already exists.", nameof(id));
            }

            Save(library);
            return library;
        }
    }

    /// <inheritdoc />
    public void RenameLibrary(string name, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        lock (_gate)
        {
            var library = FindByName(name);
            EnsureUniqueName(GetLibraries().Where(item => !item.Id.Equals(library.Id)), newName);
            Save(library with { Name = newName });
        }
    }

    /// <inheritdoc />
    public void RemoveLibrary(string name)
    {
        lock (_gate)
        {
            var library = FindByName(name);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Libraries WHERE Id = $id";
            command.Parameters.AddWithValue("$id", library.Id.ToString("N"));
            command.ExecuteNonQuery();
            // Addresses are not authority. Without a configured root they cannot resolve.
            // Retaining them permits bookmarks to reconnect when the same root is re-added.
        }
    }

    /// <inheritdoc />
    public void AddPath(string name, string path)
    {
        lock (_gate)
        {
            var library = FindByName(name);
            var root = Mount(GetLibraries(), path);
            Save(library with { Roots = [.. library.Roots, root] });
        }
    }

    /// <inheritdoc />
    public void RemovePath(string name, string path)
    {
        lock (_gate)
        {
            var library = FindByName(name);
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            Save(library with { Roots = library.Roots.Where(root => !PathsEqual(root.FullPath, fullPath)).ToArray() });
        }
    }

    /// <inheritdoc />
    public LiveLibraryDefinition? FindLibrary(Guid itemId)
    {
        lock (_gate)
        {
            var libraries = GetLibraries();
            var library = libraries.FirstOrDefault(item => item.Id.Equals(itemId) || item.Roots.Any(root => root.Id.Equals(itemId)));
            if (library is not null)
            {
                return library;
            }

            var address = ReadAddress(itemId);
            return address is null ? null : libraries.FirstOrDefault(item => item.Roots.Any(root => root.Id.Equals(address.Value.RootId)));
        }
    }

    /// <inheritdoc />
    public LiveDirectoryEntry? GetEntry(Guid itemId)
    {
        lock (_gate)
        {
            var library = FindLibrary(itemId);
            if (library is null || library.Id.Equals(itemId))
            {
                return null;
            }

            var address = ResolveAddress(library, itemId);
            var entry = _browser.GetEntry(address.Root, address.RelativePath);
            if (!entry.Id.Equals(itemId))
            {
                throw new InvalidDataException("The item address does not match its stable identifier.");
            }

            return entry;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LiveDirectoryEntry> Browse(Guid parentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var library = FindLibrary(parentId) ?? throw new DirectoryNotFoundException("The folder is not configured.");
            IReadOnlyList<LiveDirectoryEntry> entries;
            if (library.Id.Equals(parentId))
            {
                entries = library.Roots.Count == 1
                    ? _browser.Browse(library.Roots[0], string.Empty, cancellationToken)
                    : library.Roots.Select(root => _browser.GetEntry(root, string.Empty)).ToArray();
            }
            else
            {
                var address = ResolveAddress(library, parentId);
                entries = _browser.Browse(address.Root, address.RelativePath, cancellationToken);
            }

            Remember(entries, cancellationToken);
            return entries;
        }
    }

    private LiveMediaRoot Mount(IEnumerable<LiveLibraryDefinition> libraries, string path)
    {
        var root = _browser.Mount(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path, path);
        if (ContainsPath(root.FullPath, _dataDirectory) || ContainsPath(_dataDirectory, root.FullPath))
        {
            throw new ArgumentException("Media roots and private server state must not overlap.", nameof(path));
        }

        if (libraries.SelectMany(item => item.Roots).Any(item => ContainsPath(item.FullPath, root.FullPath) || ContainsPath(root.FullPath, item.FullPath)))
        {
            throw new ArgumentException("This path overlaps an existing media root.", nameof(path));
        }

        return root;
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool ContainsPath(string parent, string child)
        => PathsEqual(parent, child) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void EnsureUniqueName(IEnumerable<LiveLibraryDefinition> libraries, string name)
    {
        if (libraries.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A folder group with this name already exists.", nameof(name));
        }
    }

    private LiveLibraryDefinition FindByName(string name)
        => GetLibraries().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new DirectoryNotFoundException("The folder group is not configured.");

    private (LiveMediaRoot Root, string RelativePath) ResolveAddress(LiveLibraryDefinition library, Guid id)
    {
        var root = library.Roots.FirstOrDefault(item => item.Id.Equals(id));
        if (root is not null)
        {
            return (root, string.Empty);
        }

        var address = ReadAddress(id) ?? throw new FileNotFoundException("The item address is unknown.");
        root = library.Roots.FirstOrDefault(item => item.Id.Equals(address.RootId))
            ?? throw new FileNotFoundException("The media root is no longer configured.");
        return (root, address.RelativePath);
    }

    private (Guid RootId, string RelativePath)? ReadAddress(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RootId, RelativePath FROM Addresses WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Guid.Parse(reader.GetString(0)), reader.GetString(1)) : null;
    }

    private void Save(LiveLibraryDefinition library)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Libraries(Id, Definition) VALUES($id, $definition) ON CONFLICT(Id) DO UPDATE SET Definition = excluded.Definition";
        command.Parameters.AddWithValue("$id", library.Id.ToString("N"));
        command.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(library));
        command.ExecuteNonQuery();
    }

    private void Remember(IReadOnlyList<LiveDirectoryEntry> entries, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Addresses(Id, RootId, RelativePath) VALUES($id, $root, $path) ON CONFLICT(Id) DO NOTHING";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var root = command.Parameters.Add("$root", SqliteType.Text);
        var path = command.Parameters.Add("$path", SqliteType.Text);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = entry.Id.ToString("N");
            root.Value = entry.RootId.ToString("N");
            path.Value = entry.RelativePath;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
