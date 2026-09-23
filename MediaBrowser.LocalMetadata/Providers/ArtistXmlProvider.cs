using System.IO;
using System.Threading;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.LocalMetadata.Parsers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.LocalMetadata.Providers
{
    /// <summary>
    /// Reads legacy-style artist.xml sidecars from physical music artist directories.
    /// </summary>
    public class ArtistXmlProvider : BaseXmlProvider<MusicArtist>
    {
        private readonly ILogger<BaseItemXmlParser<MusicArtist>> _logger;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ArtistXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The XML parser logger.</param>
        /// <param name="providerManager">The provider manager.</param>
        public ArtistXmlProvider(
            IFileSystem fileSystem,
            ILogger<BaseItemXmlParser<MusicArtist>> logger,
            IProviderManager providerManager)
            : base(fileSystem)
        {
            _logger = logger;
            _providerManager = providerManager;
        }

        /// <inheritdoc />
        /// <remarks>NFO providers use the default order of 50, so NFO wins when both sidecars exist.</remarks>
        public override int Order => 51;

        /// <inheritdoc />
        protected override void Fetch(MetadataResult<MusicArtist> result, string path, CancellationToken cancellationToken)
        {
            new BaseItemXmlParser<MusicArtist>(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            return directoryService.GetFile(Path.Combine(info.Path, "artist.xml"));
        }
    }
}
