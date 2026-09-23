using System.IO;
using System.Threading;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.LocalMetadata.Parsers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.LocalMetadata.Providers
{
    /// <summary>
    /// Reads legacy Emby movie XML sidecars without changing the movie's physical location.
    /// </summary>
    public class MovieXmlProvider : BaseXmlProvider<Movie>
    {
        private readonly ILogger<BaseItemXmlParser<Movie>> _logger;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="MovieXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The XML parser logger.</param>
        /// <param name="providerManager">The provider manager.</param>
        public MovieXmlProvider(
            IFileSystem fileSystem,
            ILogger<BaseItemXmlParser<Movie>> logger,
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
        protected override void Fetch(MetadataResult<Movie> result, string path, CancellationToken cancellationToken)
        {
            new BaseItemXmlParser<Movie>(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            // A loose movie must never inherit the generic movie.xml of another
            // file in the same physical folder. Dedicated movie folders prefer it.
            if (!info.IsInMixedFolder)
            {
                var movieXml = directoryService.GetFile(Path.Combine(info.ContainingFolderPath, "movie.xml"));
                if (movieXml is not null)
                {
                    return movieXml;
                }
            }

            return directoryService.GetFile(Path.ChangeExtension(info.Path, ".xml"));
        }
    }
}
