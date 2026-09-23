using System.IO;
using System.Threading;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.LocalMetadata.Parsers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.LocalMetadata.Providers
{
    /// <summary>
    /// Reads legacy Emby series.xml sidecars from physical show directories.
    /// </summary>
    public class SeriesXmlProvider : BaseXmlProvider<Series>
    {
        private readonly ILogger<BaseItemXmlParser<Series>> _logger;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="SeriesXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The XML parser logger.</param>
        /// <param name="providerManager">The provider manager.</param>
        public SeriesXmlProvider(
            IFileSystem fileSystem,
            ILogger<BaseItemXmlParser<Series>> logger,
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
        protected override void Fetch(MetadataResult<Series> result, string path, CancellationToken cancellationToken)
        {
            new BaseItemXmlParser<Series>(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            return directoryService.GetFile(Path.Combine(info.Path, "series.xml"));
        }
    }
}
