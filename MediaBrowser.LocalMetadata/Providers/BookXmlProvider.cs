using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.LocalMetadata.Parsers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.LocalMetadata.Providers
{
    /// <summary>
    /// Reads legacy-style XML sidecars for physical book files.
    /// </summary>
    public class BookXmlProvider : BaseXmlProvider<Book>
    {
        private readonly ILogger<BaseItemXmlParser<Book>> _logger;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="BookXmlProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="logger">The XML parser logger.</param>
        /// <param name="providerManager">The provider manager.</param>
        public BookXmlProvider(
            IFileSystem fileSystem,
            ILogger<BaseItemXmlParser<Book>> logger,
            IProviderManager providerManager)
            : base(fileSystem)
        {
            _logger = logger;
            _providerManager = providerManager;
        }

        /// <inheritdoc />
        /// <remarks>OPF and embedded metadata keep their default order of 50.</remarks>
        public override int Order => 51;

        /// <inheritdoc />
        protected override void Fetch(MetadataResult<Book> result, string path, CancellationToken cancellationToken)
        {
            new BaseItemXmlParser<Book>(_logger, _providerManager).Fetch(result, path, cancellationToken);
        }

        /// <inheritdoc />
        protected override FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService)
        {
            var specificFile = directoryService.GetFile(Path.ChangeExtension(info.Path, ".xml"));
            if (specificFile is not null)
            {
                return specificFile;
            }

            // A shared sidecar is safe only when the directory contains one
            // supported book file, regardless of whether the folder is a Book.
            if (directoryService.GetFileSystemEntries(info.ContainingFolderPath)
                .Count(entry => !entry.IsDirectory && BookFileExtensions.IsBookFile(entry.FullName)) != 1)
            {
                return null;
            }

            return directoryService.GetFile(Path.Combine(info.ContainingFolderPath, "book.xml"));
        }
    }
}
