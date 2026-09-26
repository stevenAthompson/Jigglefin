using System;
using System.IO;
using System.Threading;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class LiveUserDataTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("jigglefin-bookmark-test-");
    private readonly User _user = new("reader", "local", "local");
    private readonly Mock<IDbContextFactory<JellyfinDbContext>> _catalog = new(MockBehavior.Strict);
    private readonly LiveUserDataStore _state;
    private readonly UserDataManager _manager;

    public LiveUserDataTests()
    {
        _state = new LiveUserDataStore(_fixture.FullName);
        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(configuration => configuration.Configuration).Returns(new ServerConfiguration
        {
            MinAudiobookResume = 5,
            MaxAudiobookResume = 5,
            MinResumeDurationSeconds = 120
        });
        _manager = new UserDataManager(config.Object, _catalog.Object, _state);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("book")]
    [InlineData("video")]
    public void ShortFileProgress_IsDurableWithoutCatalogRowsOrThresholds(string type)
    {
        var item = Item(type);
        item.RunTimeTicks = TimeSpan.FromSeconds(2).Ticks;
        var data = _manager.GetUserData(_user, item)!;
        var bookmark = TimeSpan.FromMilliseconds(250).Ticks;
        Assert.False(_manager.UpdatePlayState(item, data, bookmark));
        _manager.SaveUserData(_user, item, data, UserDataSaveReason.PlaybackFinished, TestContext.Current.CancellationToken);

        var restarted = new LiveUserDataStore(_fixture.FullName);
        Assert.Equal(bookmark, restarted.Get(_user.Id, item.Id).PlaybackPositionTicks);
        Assert.False(restarted.Get(_user.Id, item.Id).Played);
        Assert.Equal(0, restarted.Get(Guid.NewGuid(), item.Id).PlaybackPositionTicks);
        _catalog.VerifyNoOtherCalls();
    }

    [Fact]
    public void UnknownDuration_DoesNotEraseProgressOrDeclareCompletion()
    {
        var item = Item("book");
        var data = new UserItemData { Key = "old-name-based-key", PlaybackPositionTicks = TimeSpan.FromHours(4).Ticks };
        Assert.False(_manager.UpdatePlayState(item, data, null));
        Assert.Equal(TimeSpan.FromHours(4).Ticks, data.PlaybackPositionTicks);
        Assert.False(data.Played);
        Assert.False(_manager.UpdatePlayState(item, data, TimeSpan.FromHours(5).Ticks));
        Assert.Equal(TimeSpan.FromHours(5).Ticks, data.PlaybackPositionTicks);
        _catalog.VerifyNoOtherCalls();
    }

    [Fact]
    public void MetadataNameAndAlbumChanges_CannotChangeBookmarkIdentity()
    {
        var item = Item("audio");
        var data = new UserItemData { Key = "old", PlaybackPositionTicks = 123456, IsFavorite = true };
        _manager.SaveUserData(_user, item, data, UserDataSaveReason.UpdateUserData, TestContext.Current.CancellationToken);
        item.Name = "A different title";
        ((Audio)item).Album = "A different album";
        Assert.Equal(123456, _manager.GetUserData(_user, item)!.PlaybackPositionTicks);
        Assert.True(_manager.GetUserDataBatch([item], _user)[item.Id].IsFavorite);
        Assert.Equal(item.Id.ToString("N"), _manager.GetUserData(_user, item)!.Key);
        _catalog.VerifyNoOtherCalls();
    }

    [Fact]
    public void ReachingKnownEnd_MarksCompletionExplicitly()
    {
        var item = Item("book");
        item.RunTimeTicks = 500;
        var data = new UserItemData { Key = "test" };
        Assert.True(_manager.UpdatePlayState(item, data, 500));
        Assert.True(data.Played);
        Assert.Equal(0, data.PlaybackPositionTicks);
    }

    [Fact]
    public void ResettingStreamPreferences_DoesNotEraseResume()
    {
        var item = Item("video");
        _state.Save(_user.Id, item.Id, new UserItemData { Key = "test", PlaybackPositionTicks = 123, AudioStreamIndex = 2, SubtitleStreamIndex = 4 });
        _manager.ResetPlaybackStreamSelections(_user, item);
        var data = _state.Get(_user.Id, item.Id);
        Assert.Equal(123, data.PlaybackPositionTicks);
        Assert.Null(data.AudioStreamIndex);
        Assert.Null(data.SubtitleStreamIndex);
        _catalog.VerifyNoOtherCalls();
    }

    [Fact]
    public void SaveAcknowledgement_AndEventFollowTheDurableCommit()
    {
        var item = Item("book");
        var raised = false;
        _manager.UserDataSaved += (_, args) =>
        {
            raised = true;
            Assert.Equal(77, new LiveUserDataStore(_fixture.FullName).Get(args.UserId, item.Id).PlaybackPositionTicks);
            Assert.Equal(item.Id.ToString("N"), Assert.Single(args.Keys));
        };
        _manager.SaveUserData(_user, item, new UserItemData { Key = "test", PlaybackPositionTicks = 77 }, UserDataSaveReason.PlaybackProgress, TestContext.Current.CancellationToken);
        Assert.True(raised);
        _catalog.VerifyNoOtherCalls();
    }

    public void Dispose() => _fixture.Delete(true);

    private static BaseItem Item(string kind)
    {
        BaseItem item = kind switch { "audio" => new Audio(), "book" => new AudioBook(), _ => new Video() };
        item.Id = Guid.NewGuid();
        item.Name = "Original name";
        item.LiveContext = new LiveItemContext(new LiveLibraryDefinition(Guid.NewGuid(), "Media", []), null);
        return item;
    }
}
