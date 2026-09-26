using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

// NFO/Emby/OPF content is selected-item decoration, never directory membership,
// folder classification, a playable replacement for a folder, or an online lookup.
public sealed class LocalSidecarLibraryTests
{
    private static readonly byte[] _png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII=");

    [Theory]
    [InlineData(".mp4", ".nfo", "nfo", BaseItemKind.Video)]
    [InlineData(".mp4", ".xml", "xml", BaseItemKind.Video)]
    [InlineData(".m4b", ".xml", "xml", BaseItemKind.AudioBook)]
    [InlineData(".pdf", ".xml", "xml", BaseItemKind.Book)]
    [InlineData(".pdf", ".opf", "opf", BaseItemKind.Book)]
    [InlineData(".m4a", ".nfo", "audio", BaseItemKind.Audio)]
    public async Task SpecificSidecar_IsLoadedOnlyOnSelectionAndEditsDoNotRenameListings(string extension, string sidecarExtension, string format, BaseItemKind kind)
    {
        using var fixture = new LiveFolderFixture();
        var media = fixture.Write("Mixed/Chosen" + extension, [1, 2, 3]);
        fixture.Write("Mixed/Other" + extension, [4, 5, 6]);
        var sidecar = fixture.Write("Mixed/Chosen" + sidecarExtension, Encoding.UTF8.GetBytes(Metadata(format, "Local title", "First description")));
        var originalTime = File.GetLastWriteTimeUtc(media);
        await fixture.Start();
        var group = await fixture.AddGroup();
        Guid id;
        using (fixture.LockFiles())
        {
            var file = await fixture.Navigate(group.Id, "Mixed", "Chosen" + extension);
            id = file.Id;
            Assert.Equal(kind, file.Type);
            Assert.Null(file.Overview);
            Assert.Empty(file.ImageTags);
        }

        var user = await AuthHelper.GetUserDtoAsync(fixture.Client);
        var saved = fixture.Services.GetRequiredService<ILiveUserDataStore>();
        saved.Save(user.Id, id, new UserItemData { Key = id.ToString("N"), PlaybackPositionTicks = 6500000 });
        using (var lockedMedia = File.Open(media, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var selected = await fixture.Details(id);
            Assert.Equal(kind, selected.Type);
            Assert.Equal("Local title", selected.Name);
            Assert.Equal("First description", selected.Overview);
            Assert.Equal(2022, selected.ProductionYear);
            Assert.Contains("Test genre", selected.Genres);
            Assert.Null(selected.RunTimeTicks);
            LiveFolderFixture.AssertUnprobedSource(selected);
            Assert.True(selected.ProviderIds is null or { Count: 0 });
            Assert.Equal(6500000, selected.UserData.PlaybackPositionTicks);
            if (format == "audio")
            {
                Assert.Equal("Local album", selected.Album);
                Assert.Equal(new[] { "Local artist" }, selected.Artists);
            }

            await File.WriteAllTextAsync(sidecar, Metadata(format, "Edited local title", "Edited description"), TestContext.Current.CancellationToken);
            var changed = await fixture.Details(id);
            Assert.Equal("Edited local title", changed.Name);
            Assert.Equal("Edited description", changed.Overview);
            fixture.Services.GetRequiredService<ILiveItemService>().ClearCache();
            Assert.Equal("Edited local title", (await fixture.Details(id)).Name);
            File.Delete(sidecar);
            var removed = await fixture.Details(id);
            Assert.Equal("Chosen" + extension, removed.Name);
            Assert.Null(removed.Overview);
            Assert.Null(removed.ProductionYear);
            Assert.Empty(removed.Genres);
            Assert.Equal(6500000, removed.UserData.PlaybackPositionTicks);
        }

        var listing = await fixture.Navigate(group.Id, "Mixed", "Chosen" + extension);
        Assert.Equal(id, listing.Id);
        Assert.Equal("Chosen" + extension, listing.Name);
        var other = await fixture.Navigate(group.Id, "Mixed", "Other" + extension);
        Assert.Equal("Other" + extension, (await fixture.Details(other.Id)).Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(media, TestContext.Current.CancellationToken));
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(media));
        await fixture.AssertNoCatalogImport();
    }

    [Theory]
    [InlineData(".mp4", "movie.nfo", "nfo")]
    [InlineData(".mp4", "movie.xml", "xml")]
    [InlineData(".pdf", "metadata.opf", "opf")]
    [InlineData(".pdf", "content.opf", "opf")]
    [InlineData(".pdf", "book.xml", "xml")]
    [InlineData(".m4b", "audiobook.xml", "xml")]
    [InlineData(".m4b", "book.xml", "xml")]
    public async Task SharedSidecars_RequireOneMatchingImmediateFileAndRecheckAmbiguity(string extension, string sidecarName, string format)
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Shelf/Chosen" + extension, [1, 2, 3]);
        var metadata = Metadata(format, "Shared title", "Only when unambiguous");
        var sidecar = fixture.Write("Shelf/" + sidecarName, Encoding.UTF8.GetBytes(metadata));
        fixture.Write("Shelf/Unvisited/Deep/Other" + extension, [4, 5, 6]);
        fixture.Write("Shelf/Snapshot.png", _png);
        await fixture.Start();
        var group = await fixture.AddGroup();
        var selected = await fixture.Navigate(group.Id, "Shelf", "Chosen" + extension);
        var before = fixture.Reader.Enumerations.Count;
        Assert.Equal("Shared title", (await fixture.Details(selected.Id)).Name);
        Assert.Equal(new[] { fixture.PathFor("Shelf") }, fixture.Reader.Enumerations.Skip(before));
        var otherExtension = extension == ".mp4" ? ".mp4" : extension == ".pdf" ? ".m4b" : ".pdf";
        var other = fixture.Write("Shelf/Another" + otherExtension, [9, 8, 7]);
        Assert.Equal("Chosen" + extension, (await fixture.Details(selected.Id)).Name);
        File.Delete(other);
        Assert.Equal("Shared title", (await fixture.Details(selected.Id)).Name);
        Assert.DoesNotContain(fixture.PathFor("Shelf/Unvisited"), fixture.Reader.Enumerations);
        Assert.Equal(metadata, await File.ReadAllTextAsync(sidecar, TestContext.Current.CancellationToken));
        await fixture.AssertNoCatalogImport();
    }

    [Theory]
    [InlineData("folder.nfo", "nfo")]
    [InlineData("movie.nfo", "nfo")]
    [InlineData("tvshow.nfo", "nfo")]
    [InlineData("season.nfo", "nfo")]
    [InlineData("artist.nfo", "artist")]
    [InlineData("album.nfo", "nfo")]
    [InlineData("metadata.opf", "opf")]
    [InlineData("movie.xml", "xml")]
    [InlineData("series.xml", "xml")]
    [InlineData("season.xml", "xml")]
    [InlineData("artist.xml", "xml")]
    [InlineData("album.xml", "xml")]
    [InlineData("book.xml", "xml")]
    public async Task FolderSidecars_DecorateSelectedFolderWithoutCollapsingItsChildren(string filename, string format)
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Physical Folder/Child/Untouched.mp4", [1, 2, 3]);
        var sidecar = fixture.Write("Physical Folder/" + filename, Encoding.UTF8.GetBytes(Metadata(format, "Selected folder title", "Local folder description")));
        await fixture.Start();
        var group = await fixture.AddGroup();
        var folder = await fixture.Navigate(group.Id, "Physical Folder");
        var count = fixture.Reader.Enumerations.Count;
        var selected = await fixture.Details(folder.Id);
        Assert.Equal("Selected folder title", selected.Name);
        Assert.Equal("Local folder description", selected.Overview);
        Assert.Equal(BaseItemKind.Folder, selected.Type);
        Assert.True(selected.IsFolder);
        Assert.Equal(count, fixture.Reader.Enumerations.Count);
        var child = (await fixture.Browse(folder.Id, fixture.PathFor("Physical Folder"))).Items.Single(item => item.Name == "Child");
        Assert.Equal(BaseItemKind.Folder, child.Type);
        Assert.Equal("Physical Folder", (await fixture.Navigate(group.Id, "Physical Folder")).Name);
        File.Delete(sidecar);
        Assert.Equal("Physical Folder", (await fixture.Details(folder.Id)).Name);
        Assert.DoesNotContain(fixture.PathFor("Physical Folder/Child"), fixture.Reader.Enumerations);
        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task NfoThenOpfThenXml_PreferenceIsLocalAndChangesImmediatelyWhenFilesAreRemoved()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Book/Chosen.pdf", [1, 2, 3]);
        var nfo = fixture.Write("Book/Chosen.nfo", Encoding.UTF8.GetBytes(Metadata("nfo", "NFO", "NFO wins")));
        var opf = fixture.Write("Book/Chosen.opf", Encoding.UTF8.GetBytes(Metadata("opf", "Specific OPF", "OPF wins over XML")));
        var shared = fixture.Write("Book/metadata.opf", Encoding.UTF8.GetBytes(Metadata("opf", "Shared OPF", "Shared unambiguous OPF")));
        var xml = fixture.Write("Book/Chosen.xml", Encoding.UTF8.GetBytes(Metadata("xml", "Specific XML", "XML fallback")));
        await fixture.Start();
        var group = await fixture.AddGroup();
        var file = await fixture.Navigate(group.Id, "Book", "Chosen.pdf");
        Assert.Equal("NFO", (await fixture.Details(file.Id)).Name);
        File.Delete(nfo);
        Assert.Equal("Specific OPF", (await fixture.Details(file.Id)).Name);
        File.Delete(opf);
        Assert.Equal("Shared OPF", (await fixture.Details(file.Id)).Name);
        File.Delete(shared);
        Assert.Equal("Specific XML", (await fixture.Details(file.Id)).Name);
        File.Delete(xml);
        Assert.Equal("Chosen.pdf", (await fixture.Details(file.Id)).Name);
        await fixture.AssertNoCatalogImport();
    }

    [Theory]
    [InlineData("<movie><title>Unclosed")]
    [InlineData("<!DOCTYPE movie [<!ENTITY remote SYSTEM 'http://127.0.0.1:1/private'>]><movie><title>&remote;</title></movie>")]
    [InlineData("<unrelated><title>Not recognized metadata</title></unrelated>")]
    public async Task UnsafeOrMalformedNfo_DoesNotHideFilesAndCanFallBackToLocalXml(string nfo)
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Chosen.mp4", [1, 2, 3]);
        fixture.Write("Chosen.nfo", Encoding.UTF8.GetBytes(nfo));
        await fixture.Start();
        var group = await fixture.AddGroup();
        var file = await fixture.Navigate(group.Id, "Chosen.mp4");
        Assert.Equal("Chosen.mp4", (await fixture.Details(file.Id)).Name);
        fixture.Write("Chosen.xml", Encoding.UTF8.GetBytes(Metadata("xml", "Valid XML", "Safe fallback")));
        Assert.Equal("Valid XML", (await fixture.Details(file.Id)).Name);
        Assert.Equal("Chosen.mp4", (await fixture.Navigate(group.Id, "Chosen.mp4")).Name);
        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task OversizedMetadata_IsNotReadAndDoesNotHideTheFile()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Chosen.mp4", [1, 2, 3]);
        var sidecar = fixture.Write("Chosen.nfo", Encoding.UTF8.GetBytes("<movie><plot>" + new string('x', 1024 * 1024) + "</plot></movie>"));
        await fixture.Start();
        var group = await fixture.AddGroup();
        var file = await fixture.Navigate(group.Id, "Chosen.mp4");
        using var locked = File.Open(sidecar, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal("Chosen.mp4", (await fixture.Details(file.Id)).Name);
        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task SelectedArtworkAndPhotos_UseStandardImageApiAndRefreshAfterRemoval()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Movies/Chosen.mp4", [1, 2, 3]);
        var poster = fixture.Write("Movies/Chosen-poster.png", _png);
        fixture.Write("Movies/Snapshot.png", _png);
        await fixture.Start();
        var group = await fixture.AddGroup();
        var file = await fixture.Navigate(group.Id, "Movies", "Chosen.mp4");
        Assert.Empty(file.ImageTags);
        var selected = await fixture.Details(file.Id);
        Assert.Contains(ImageType.Primary, selected.ImageTags.Keys);
        using var image = await fixture.Client.GetAsync($"Items/{file.Id}/Images/Primary", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(_png[..8], (await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken))[..8]);
        var photo = await fixture.Navigate(group.Id, "Movies", "Snapshot.png");
        Assert.Equal(BaseItemKind.Photo, photo.Type);
        using var photoImage = await fixture.Client.GetAsync($"Items/{photo.Id}/Images/Primary", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, photoImage.StatusCode);
        File.Delete(poster);
        Assert.Empty((await fixture.Details(file.Id)).ImageTags);
        using var missing = await fixture.Client.GetAsync($"Items/{file.Id}/Images/Primary", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await fixture.AssertNoCatalogImport();
    }

    private static string Metadata(string format, string title, string description)
        => format switch
        {
            "xml" => $"<Item><LocalTitle>{title}</LocalTitle><ProductionYear>2022</ProductionYear><Overview>{description}</Overview><Genres><Genre>Test genre</Genre></Genres></Item>",
            "opf" => $"<package xmlns='http://www.idpf.org/2007/opf'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>{title}</dc:title><dc:description>{description}</dc:description><dc:date>2022-01-01</dc:date><dc:subject>Test genre</dc:subject></metadata></package>",
            "artist" => $"<artist><name>{title}</name><plot>{description}</plot><year>2022</year><genre>Test genre</genre></artist>",
            "audio" => $"<album><title>{title}</title><plot>{description}</plot><year>2022</year><genre>Test genre</genre><album>Local album</album><artist>Local artist</artist></album>",
            _ => $"<movie><title>{title}</title><plot>{description}</plot><year>2022</year><genre>Test genre</genre><uniqueid>ignored</uniqueid><thumb>http://127.0.0.1:1/image</thumb><resume><position>0</position></resume></movie>"
        };
}
