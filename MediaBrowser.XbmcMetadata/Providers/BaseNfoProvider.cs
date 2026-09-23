#pragma warning disable CS1591

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.XbmcMetadata.Savers;

namespace MediaBrowser.XbmcMetadata.Providers
{
    public abstract class BaseNfoProvider<T> : ILocalMetadataProvider<T>, IHasItemChangeMonitor
        where T : BaseItem, new()
    {
        private readonly IFileSystem _fileSystem;

        protected BaseNfoProvider(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        /// <inheritdoc />
        public string Name => BaseNfoSaver.SaverName;

        /// <inheritdoc />
        public Task<MetadataResult<T>> GetMetadata(
            ItemInfo info,
            IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<T>();

            var file = GetXmlFile(info, directoryService);

            if (file?.Exists is not true)
            {
                result.RemovedLocalSidecarProviderId = !string.IsNullOrEmpty(info.PreviousLocalNfoPath)
                    ? ItemInfo.LocalNfoPathProviderId
                    : null;
                return Task.FromResult(result);
            }

            var path = file.FullName;

            try
            {
                result.Item = new T
                {
                    IndexNumber = info.IndexNumber
                };

                Fetch(result, path, cancellationToken);
                result.Item.ProviderIds[ItemInfo.LocalNfoPathProviderId] = path;
                result.HasMetadata = true;
            }
            catch (FileNotFoundException)
            {
                result.HasMetadata = false;
            }
            catch (IOException)
            {
                result.HasMetadata = false;
            }

            return Task.FromResult(result);
        }

        /// <inheritdoc />
        public bool HasChanged(BaseItem item, IDirectoryService directoryService)
        {
            var info = new ItemInfo(item);
            var file = GetXmlFile(info, directoryService);

            if (file?.Exists is not true)
            {
                return !string.IsNullOrEmpty(info.PreviousLocalNfoPath);
            }

            if (!string.IsNullOrEmpty(info.PreviousLocalNfoPath)
                && !string.Equals(file.FullName, info.PreviousLocalNfoPath, StringComparison.Ordinal))
            {
                return true;
            }

            var fileTime = _fileSystem.GetLastWriteTimeUtc(file);

            // The tolerance only protects installations that save NFOs themselves.
            // Folder-first libraries normally read local sidecars without writing them,
            // so an edit immediately after scanning must be picked up on the next scan.
            var tolerance = item.IsSaveLocalMetadataEnabled() ? TimeSpan.FromMinutes(1) : TimeSpan.Zero;
            return (fileTime - item.DateLastSaved) > tolerance;
        }

        protected abstract void Fetch(MetadataResult<T> result, string path, CancellationToken cancellationToken);

        protected abstract FileSystemMetadata? GetXmlFile(ItemInfo info, IDirectoryService directoryService);
    }
}
