using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Emby.Server.Implementations.Library.Live;

/// <summary>Bookmarks have no foreign key to Jellyfin's catalog or disposable caches.</summary>
public sealed class LiveUserDataStore : ILiveUserDataStore
{
    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="LiveUserDataStore"/> class.</summary>
    /// <param name="dataDirectory">Private durable server state, not the cache directory.</param>
    public LiveUserDataStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "live-user-state.db"), Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS UserState (UserId TEXT NOT NULL, ItemId TEXT NOT NULL, Data TEXT NOT NULL, LastPlayed INTEGER NOT NULL, PRIMARY KEY(UserId, ItemId));";
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public UserItemData Get(Guid userId, Guid itemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM UserState WHERE UserId = $user AND ItemId = $item";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        return command.ExecuteScalar() is string json ? Deserialize(json) : new UserItemData { Key = itemId.ToString("N") };
    }

    /// <inheritdoc />
    public void Save(Guid userId, Guid itemId, UserItemData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Key = itemId.ToString("N");
        data.PlaybackPositionTicks = Math.Max(0, data.PlaybackPositionTicks);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserState(UserId, ItemId, Data, LastPlayed) VALUES($user, $item, $data, $played)
            ON CONFLICT(UserId, ItemId) DO UPDATE SET Data = excluded.Data, LastPlayed = excluded.LastPlayed
            """;
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data));
        command.Parameters.AddWithValue("$played", data.LastPlayedDate?.ToUniversalTime().Ticks ?? 0);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<(Guid ItemId, UserItemData Data)> GetSaved(Guid userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemId, Data FROM UserState WHERE UserId = $user ORDER BY LastPlayed DESC, rowid DESC";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        using var reader = command.ExecuteReader();
        var result = new List<(Guid, UserItemData)>();
        while (reader.Read())
        {
            result.Add((Guid.Parse(reader.GetString(0)), Deserialize(reader.GetString(1))));
        }

        return result;
    }

    private static UserItemData Deserialize(string json)
        => JsonSerializer.Deserialize<UserItemData>(json) ?? throw new InvalidDataException("Invalid saved playback state.");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
