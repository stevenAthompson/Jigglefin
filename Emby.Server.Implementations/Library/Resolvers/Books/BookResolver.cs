#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using Emby.Naming.Book;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;

namespace Emby.Server.Implementations.Library.Resolvers.Books
{
    public class BookResolver : ItemResolver<Book>
    {
        private static readonly string[] _validExtensions = { ".azw", ".azw3", ".cb7", ".cbr", ".cbt", ".cbz", ".epub", ".mobi", ".pdf" };
        private static readonly string[] _companionExtensions = { ".nfo", ".xml", ".opf", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif", ".txt", ".json" };

        internal static bool IsBookFile(string path)
            => _validExtensions.Contains(Path.GetExtension(path.AsSpan()), StringComparison.OrdinalIgnoreCase);

        protected override Book? Resolve(ItemResolveArgs args)
        {
            var collectionType = args.GetCollectionType();

            // Only process items that are in a collection folder containing books
            if (collectionType != CollectionType.books)
            {
                return null;
            }

            if (args.IsDirectory)
            {
                return GetBook(args);
            }

            if (!IsBookFile(args.Path))
            {
                return null;
            }

            var result = BookFileNameParser.Parse(Path.GetFileNameWithoutExtension(args.Path));

            return new Book
            {
                Path = args.Path,
                Name = result.Name ?? string.Empty,
                IndexNumber = result.Index,
                ParentIndexNumber = result.ParentIndex,
                ProductionYear = result.Year,
                SeriesName = result.SeriesName ?? Path.GetFileName(Path.GetDirectoryName(args.Path)),
                IsInMixedFolder = true,
            };
        }

        private Book? GetBook(ItemResolveArgs args)
        {
            // A book item cannot expose sibling directories to a folder-browsing client.
            // Keep mixed book directories as physical folders so their children remain visible.
            if (args.FileSystemChildren.Any(f => f.IsDirectory))
            {
                return null;
            }

            var bookFiles = args.FileSystemChildren.Where(f => IsBookFile(f.FullName)).ToList();

            // A dedicated book directory has one supported file and no child directories.
            // Other layouts are physical folders whose books are resolved individually.
            if (bookFiles.Count != 1)
            {
                return null;
            }

            if (args.FileSystemChildren.Any(f => !IsBookFile(f.FullName)
                && !_companionExtensions.Contains(Path.GetExtension(f.FullName.AsSpan()), StringComparison.OrdinalIgnoreCase)))
            {
                // A book cannot replace a directory that also contains another media file.
                return null;
            }

            var folderName = Path.GetFileName(args.Path);
            var fileName = Path.GetFileNameWithoutExtension(bookFiles[0].FullName);
            var result = BookFileNameParser.Parse(folderName);
            var fileTitle = BookFileNameParser.Parse(fileName).Name ?? fileName;
            if (!string.Equals(result.Name ?? folderName, fileTitle, StringComparison.OrdinalIgnoreCase))
            {
                // A grouping directory with one loose book is still a physical folder.
                return null;
            }

            return new Book
            {
                Path = bookFiles[0].FullName,
                Name = result.Name ?? folderName,
                IndexNumber = result.Index,
                ParentIndexNumber = result.ParentIndex,
                ProductionYear = result.Year,
                SeriesName = result.SeriesName ?? string.Empty,
            };
        }
    }
}
