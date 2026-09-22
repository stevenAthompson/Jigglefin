#nullable disable

using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;

namespace Emby.Server.Implementations.Library.Resolvers.Audio
{
    /// <summary>
    /// The music artist resolver.
    /// </summary>
    public class MusicArtistResolver : ItemResolver<MusicArtist>
    {
        /// <summary>
        /// Gets the priority.
        /// </summary>
        /// <value>The priority.</value>
        public override ResolverPriority Priority => ResolverPriority.Second;

        /// <summary>
        /// Resolves the specified resolver arguments.
        /// </summary>
        /// <param name="args">The resolver arguments.</param>
        /// <returns>A <see cref="MusicArtist"/>.</returns>
        protected override MusicArtist Resolve(ItemResolveArgs args)
        {
            if (!args.IsDirectory)
            {
                return null;
            }

            // Don't allow nested artists
            if (args.HasParent<MusicArtist>() || args.HasParent<MusicAlbum>())
            {
                return null;
            }

            var collectionType = args.GetCollectionType();

            var isMusicMediaFolder = collectionType == CollectionType.music;

            // If there's a collection type and it's not music, it can't be a music artist
            if (!isMusicMediaFolder)
            {
                return null;
            }

            if (args.ContainsFileSystemEntryByName("artist.nfo"))
            {
                return new MusicArtist();
            }

            // The presence of an album below a directory does not prove that directory is an
            // artist. It may be an arbitrary physical grouping such as a genre or collection.
            return null;
        }
    }
}
