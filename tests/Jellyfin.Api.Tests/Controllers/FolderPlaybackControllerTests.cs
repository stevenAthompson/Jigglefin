using System;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public sealed class FolderPlaybackControllerTests
{
    [Fact]
    public void OperationsWithoutAPersonalUser_ReturnNotFoundBeforeLookingUpAnyState()
    {
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        var state = new Mock<ILiveUserDataStore>(MockBehavior.Strict);
        var users = new Mock<IUserManager>(MockBehavior.Strict);
        var controller = new FolderPlaybackController(library.Object, state.Object, users.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        Assert.IsType<NotFoundResult>(controller.ReadPlaylist(Guid.NewGuid()).Result);
        Assert.IsType<NotFoundResult>(controller.DismissContinue());
        library.VerifyNoOtherCalls();
        state.VerifyNoOtherCalls();
        users.VerifyNoOtherCalls();
    }
}
