using System.IO;
using System.Linq;
using System.Threading;
using Emby.Naming.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.LocalMetadata.Parsers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.LocalMetadata.Providers
{
    /// <summary>
    /// Reads legacy-style XML sidecars for physical audiobook files.
    /// </summary>
    public class AudioBookXmlProvider : BaseXmlProvider<AudioBook>
    {
        private readonly ILogger<BaseItemXmlParser<AudioBook>> _logger;
        private readonly IProviderManager _providerManager;
        private readonly NamingOptions _namingOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioBookXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The XML parser logger.</param>
        /// <param name="providerManager">The provider manager.</param>
        /// <param name="namingOptions">The media naming options.</param>
        public AudioBookXmlProvider(
            IFileSystem fileSystem,
            ILogger<BaseItemXmlParser<AudioBook>> logger,
            IProviderManager providerManager,
            NamingOptions namingOptions)
            : base(fileSystem)
        {
            _logger = logger;
            _providerManager = providerManager;
            _namingOptions = namingOptions;
        }

        /// <inheritdoc />
        public override int Order => 51;

        /// <inheritdoc />
        protected override void Fetch(MetadataResult<AudioBook> result, string path, CancellationToken cancellationToken)
        {
            new BaseItemXmlParser<AudioBook>(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            var specificFile = directoryService.GetFile(Path.ChangeExtension(info.Path, ".xml"));
            if (specificFile is not null)
            {
                return specificFile;
            }

            // Generic sidecars must not give the same metadata to sibling chapters,
            // another audiobook, or an ebook in a mixed physical directory.
            if (directoryService.GetFileSystemEntries(info.ContainingFolderPath)
                .Count(entry => !entry.IsDirectory
                    && BookFileExtensions.IsBookOrAudioBookFile(entry.FullName, _namingOptions)) != 1)
            {
                return null;
            }

            return directoryService.GetFile(Path.Combine(info.ContainingFolderPath, "audiobook.xml"))
                ?? directoryService.GetFile(Path.Combine(info.ContainingFolderPath, "book.xml"));
        }
    }
}
