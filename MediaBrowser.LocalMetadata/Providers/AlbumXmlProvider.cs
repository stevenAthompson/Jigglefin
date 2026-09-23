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
    /// Reads legacy-style album.xml sidecars from physical music album directories.
    /// </summary>
    public class AlbumXmlProvider : BaseXmlProvider<MusicAlbum>
    {
        private readonly ILogger<BaseItemXmlParser<MusicAlbum>> _logger;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AlbumXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The XML parser logger.</param>
        /// <param name="providerManager">The provider manager.</param>
        public AlbumXmlProvider(
            IFileSystem fileSystem,
            ILogger<BaseItemXmlParser<MusicAlbum>> logger,
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
        protected override void Fetch(MetadataResult<MusicAlbum> result, string path, CancellationToken cancellationToken)
        {
            new BaseItemXmlParser<MusicAlbum>(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            return directoryService.GetFile(Path.Combine(info.Path, "album.xml"));
        }
    }
}
