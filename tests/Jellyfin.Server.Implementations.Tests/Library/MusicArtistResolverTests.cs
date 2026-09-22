using System.IO;
using Emby.Server.Implementations.Library.Resolvers.Audio;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class MusicArtistResolverTests
{
    [Fact]
    public void ResolvePath_ArtistNfoIdentifiesPhysicalArtistFolder()
    {
        var resolver = new MusicArtistResolver();
        var artistPath = Path.Combine("music", "Artist");
        var args = new ItemResolveArgs(Mock.Of<IServerApplicationPaths>(), Mock.Of<ILibraryManager>())
        {
            Parent = new Folder(),
            CollectionType = CollectionType.music,
            FileInfo = new FileSystemMetadata
            {
                FullName = artistPath,
                IsDirectory = true
            },
            FileSystemChildren =
            [
                new FileSystemMetadata
                {
                    FullName = Path.Combine(artistPath, "artist.nfo"),
                    Name = "artist.nfo"
                }
            ]
        };

        Assert.IsType<MusicArtist>(resolver.ResolvePath(args));
    }
}
