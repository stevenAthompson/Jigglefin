using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Api.Helpers;

internal static class LibraryOptionDefaults
{
    public static string[] GetRepresentativeItemTypes(CollectionType? contentType)
    {
        return contentType switch
        {
            CollectionType.boxsets => ["BoxSet"],
            CollectionType.playlists => ["Playlist"],
            CollectionType.movies => ["Movie"],
            CollectionType.tvshows => ["Series", "Season", "Episode"],
            CollectionType.books => ["Book", "AudioBook"],
            CollectionType.music => ["MusicArtist", "MusicAlbum", "Audio", "MusicVideo"],
            CollectionType.homevideos => ["Video", "Photo"],
            CollectionType.photos => ["Video", "Photo"],
            CollectionType.musicvideos => ["MusicVideo"],
            _ => ["Series", "Season", "Episode", "Movie"]
        };
    }

    public static void ApplyToMinimalNewLibrary(LibraryOptions options, CollectionTypeOptions? collectionType)
    {
        if (options.TypeOptions is { Length: > 0 })
        {
            return;
        }

        CollectionType? contentType = Enum.TryParse<CollectionType>(collectionType?.ToString(), out var parsedType)
            ? parsedType
            : null;

        options.TypeOptions = Array.ConvertAll(GetRepresentativeItemTypes(contentType), type => new TypeOptions
        {
            Type = type,
            // These providers produce images from local media and do not contact online services.
            ImageFetchers = ["Screen Grabber", "Image Extractor"]
        });
    }
}
