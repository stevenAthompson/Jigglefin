using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Emby.Naming.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Books.OpenPackagingFormat
{
    /// <summary>
    /// Provides metadata for book items that have an OPF file in the same directory. Supports the standard
    /// content.opf filename, bespoke metadata.opf name from Calibre libraries, and OPF files that have the
    /// same name as their respective books for directories with several books.
    /// </summary>
    public class OpfProvider : ILocalMetadataProvider<Book>, IHasItemChangeMonitor
    {
        private const string StandardOpfFile = "content.opf";
        private const string CalibreOpfFile = "metadata.opf";

        private readonly IFileSystem _fileSystem;

        private readonly ILogger<OpfProvider> _logger;
        private readonly NamingOptions _namingOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpfProvider"/> class.
        /// </summary>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{OpfProvider}"/> interface.</param>
        /// <param name="namingOptions">The media naming options.</param>
        public OpfProvider(IFileSystem fileSystem, ILogger<OpfProvider> logger, NamingOptions namingOptions)
        {
            _fileSystem = fileSystem;
            _logger = logger;
            _namingOptions = namingOptions;
        }

        /// <inheritdoc />
        public string Name => "Open Packaging Format";

        /// <inheritdoc />
        public bool HasChanged(BaseItem item, IDirectoryService directoryService)
        {
            var file = GetXmlFile(item.Path, directoryService);

            return file.Exists && _fileSystem.GetLastWriteTimeUtc(file) > item.DateLastSaved;
        }

        /// <inheritdoc />
        public Task<MetadataResult<Book>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            var path = GetXmlFile(info.Path, directoryService).FullName;

            try
            {
                return Task.FromResult(ReadOpfData(path, cancellationToken));
            }
            catch (FileNotFoundException)
            {
                return Task.FromResult(new MetadataResult<Book> { HasMetadata = false });
            }
        }

        private FileSystemMetadata GetXmlFile(string path, IDirectoryService directoryService)
        {
            var fileInfo = _fileSystem.GetFileSystemInfo(path);
            var directoryInfo = fileInfo.IsDirectory ? fileInfo : _fileSystem.GetDirectoryInfo(Path.GetDirectoryName(path)!);

            // check for OPF with matching name first since it's the most specific filename
            var specificFile = Path.Combine(directoryInfo.FullName, Path.GetFileNameWithoutExtension(path) + ".opf");
            var file = _fileSystem.GetFileInfo(specificFile);

            if (file.Exists)
            {
                return file;
            }

            // A generic OPF is unambiguous only when this directory contains
            // one book or audiobook file, even if its folder name differs.
            if (!directoryInfo.Exists
                || directoryService.GetFileSystemEntries(directoryInfo.FullName)
                    .Count(entry => !entry.IsDirectory
                        && BookFileExtensions.IsBookOrAudioBookFile(entry.FullName, _namingOptions)) != 1)
            {
                return file;
            }

            file = _fileSystem.GetFileInfo(Path.Combine(directoryInfo.FullName, StandardOpfFile));

            // check metadata.opf last since it's really only used by Calibre
            return file.Exists ? file : _fileSystem.GetFileInfo(Path.Combine(directoryInfo.FullName, CalibreOpfFile));
        }

        private MetadataResult<Book> ReadOpfData(string file, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var doc = new XmlDocument();
            doc.Load(file);

            var utilities = new OpfReader<OpfProvider>(doc, _logger);
            return utilities.ReadOpfData(cancellationToken);
        }
    }
}
