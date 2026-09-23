#pragma warning disable CS1591

using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Providers
{
    public class ItemInfo
    {
        // Persist local sidecar provenance without changing Jellyfin's database schema.
        // These internal ids are stripped from client-facing DTOs.
        public const string LocalNfoPathProviderId = "JigglefinLocalNfoPath";

        public const string LocalXmlPathProviderId = "JigglefinLocalXmlPath";

        public const string LocalOpfPathProviderId = "JigglefinLocalOpfPath";

        public ItemInfo(BaseItem item)
        {
            Path = item.Path;
            ParentId = item.ParentId;
            IndexNumber = item.IndexNumber;
            ContainingFolderPath = item.ContainingFolderPath;
            IsInMixedFolder = item.IsInMixedFolder;
            PreviousLocalNfoPath = item.ProviderIds.TryGetValue(LocalNfoPathProviderId, out var nfoPath) ? nfoPath : null;
            PreviousLocalXmlPath = item.ProviderIds.TryGetValue(LocalXmlPathProviderId, out var xmlPath) ? xmlPath : null;
            PreviousLocalOpfPath = item.ProviderIds.TryGetValue(LocalOpfPathProviderId, out var opfPath) ? opfPath : null;

            if (item is Video video)
            {
                VideoType = video.VideoType;
                IsPlaceHolder = video.IsPlaceHolder;
            }

            ItemType = item.GetType();
        }

        public Type ItemType { get; set; }

        public string Path { get; set; }

        public Guid ParentId { get; set; }

        public int? IndexNumber { get; set; }

        public string ContainingFolderPath { get; set; }

        public VideoType VideoType { get; set; }

        public bool IsInMixedFolder { get; set; }

        public bool IsPlaceHolder { get; set; }

        public string? PreviousLocalNfoPath { get; }

        public string? PreviousLocalXmlPath { get; }

        public string? PreviousLocalOpfPath { get; }

        public static bool IsInternalLocalMetadataProviderId(string id)
            => string.Equals(id, LocalNfoPathProviderId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, LocalXmlPathProviderId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, LocalOpfPathProviderId, StringComparison.OrdinalIgnoreCase);
    }
}
