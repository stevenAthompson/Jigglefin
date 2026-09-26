using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Database.Implementations;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>Imports configuration and saved state only. Never opens, stats, scans or probes media.</summary>
[JellyfinMigration("2026-09-26T10:00:00", nameof(MigrateLiveFolders), RunMigrationOnSetup = true)]
internal sealed class MigrateLiveFolders : IAsyncMigrationRoutine
{
    private readonly IDbContextFactory<JellyfinDbContext> _database;
    private readonly IServerApplicationPaths _paths;
    private readonly IServerApplicationHost _host;
    private readonly ILibraryManager _legacyIds;
    private readonly LiveLibraryStore _folders;
    private readonly LiveUserDataStore _state;
    private readonly ILogger<MigrateLiveFolders> _logger;
    private readonly IServerConfigurationManager _configuration;

    public MigrateLiveFolders(
        IDbContextFactory<JellyfinDbContext> database,
        IServerApplicationPaths paths,
        IServerApplicationHost host,
        ILibraryManager legacyIds,
        LiveLibraryStore folders,
        LiveUserDataStore state,
        IServerConfigurationManager configuration,
        ILogger<MigrateLiveFolders> logger)
    {
        _database = database;
        _paths = paths;
        _host = host;
        _legacyIds = legacyIds;
        _folders = folders;
        _state = state;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var database = await _database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (database.ConfigureAwait(false))
        {
            // Collection rows supply only permission IDs. Directory entries and metadata
            // are never imported from the old catalog or used to discover missing roots.
            var collections = await database.BaseItems.AsNoTracking()
                .Where(item => item.Type == typeof(CollectionFolder).FullName && item.Path != null)
                .Select(item => new { item.Id, item.Path })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var groups = new List<LiveLibraryDefinition>();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var group in LegacyFolderConfiguration.Read(_paths.DefaultUserViewsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matches = collections.Where(item => string.Equals(_host.ExpandVirtualPath(item.Path!), group.ConfigurationPath, comparison)).ToArray();
                if (matches.Length > 1)
                {
                    throw new InvalidDataException($"Multiple legacy permission IDs exist for '{group.Name}'. Resolve the duplicate configuration before migration.");
                }

                var id = matches.Length == 1 ? matches[0].Id : _legacyIds.GetNewItemId(group.ConfigurationPath, typeof(CollectionFolder));
                var roots = group.Paths.Select(_host.ExpandVirtualPath)
                    .Select(path => LiveDirectoryBrowser.DescribeRoot(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } label ? label : path, path))
                    .DistinctBy(root => root.Id).ToArray();
                groups.Add(new LiveLibraryDefinition(id, group.Name, roots) { Enabled = group.Enabled });
            }

            // Validate every group before committing any of them. Overlap/private-path
            // conflicts fail startup explicitly; never discard roots or broaden access.
            _folders.ImportConfiguration(groups);
            var groupIds = groups.Select(group => group.Id).ToHashSet();
            var imported = 0;
            var outsideRoots = 0;
            var duplicates = new HashSet<(Guid User, Guid Item)>();
            var saved = database.UserData.AsNoTracking()
                .Where(data => data.Item != null && data.Item.Path != null
                    && (data.PlaybackPositionTicks > 0 || data.PlayCount > 0 || data.IsFavorite || data.Played || data.Rating != null
                        || data.LastPlayedDate != null || data.AudioStreamIndex != null || data.SubtitleStreamIndex != null))
                .OrderByDescending(data => data.LastPlayedDate).ThenByDescending(data => data.PlayCount).ThenBy(data => data.ItemId).ThenBy(data => data.CustomDataKey)
                .Select(data => new { State = data, Path = data.Item!.Path!, data.Item.RunTimeTicks })
                .AsAsyncEnumerable();
            await foreach (var row in saved.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var data = row.State;
                Guid? id = groupIds.Contains(data.ItemId) ? data.ItemId : _folders.ImportSavedAddress(_host.ExpandVirtualPath(row.Path));
                if (!id.HasValue)
                {
                    outsideRoots++;
                    continue;
                }

                // Multiple old keys/versions can refer to one physical file. The most
                // recently used state wins deterministically, and retries never replace
                // a live value. Source rows remain intact for recovery/auditing.
                if (!duplicates.Add((data.UserId, id.Value)))
                {
                    continue;
                }

                _state.Import(data.UserId, id.Value, new UserItemData
                {
                    Key = id.Value.ToString("N"),
                    PlaybackPositionTicks = data.PlaybackPositionTicks,
                    LastKnownRunTimeTicks = row.RunTimeTicks > 0 ? row.RunTimeTicks : null,
                    PlayCount = data.PlayCount,
                    IsFavorite = data.IsFavorite,
                    Played = data.Played,
                    LastPlayedDate = data.LastPlayedDate,
                    AudioStreamIndex = data.AudioStreamIndex,
                    SubtitleStreamIndex = data.SubtitleStreamIndex,
                    Rating = data.Rating
                });
                imported++;
            }

            _logger.LogInformation("Migrated {Groups} configured folder groups and {States} saved user states without accessing media. {Outside} saved catalog states outside configured paths remain in the original database.", groups.Count, imported, outsideRoots);
            if (_configuration.Configuration.PluginRepositories.Length > 0 || _configuration.Configuration.CastReceiverApplications.Length > 0)
            {
                _configuration.Configuration.PluginRepositories = [];
                _configuration.Configuration.CastReceiverApplications = [];
                _configuration.SaveConfiguration();
            }
        }
    }

}
