using System.IO;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.LocalMetadata.Parsers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.LocalMetadata.Providers
{
    /// <summary>
    /// Reads legacy XML sidecars for home videos without sharing metadata between sibling files.
    /// </summary>
    public class VideoXmlProvider : BaseXmlProvider<Video>
    {
        private readonly ILogger<BaseItemXmlParser<Video>> _logger;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The XML parser logger.</param>
        /// <param name="providerManager">The provider manager.</param>
        public VideoXmlProvider(
            IFileSystem fileSystem,
            ILogger<BaseItemXmlParser<Video>> logger,
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
        protected override void Fetch(MetadataResult<Video> result, string path, CancellationToken cancellationToken)
        {
            new BaseItemXmlParser<Video>(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            return directoryService.GetFile(Path.ChangeExtension(info.Path, ".xml"));
        }
    }
}
