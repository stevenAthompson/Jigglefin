using System.Threading.Tasks;
using Jellyfin.Server.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Server.Tests;

public sealed class OfflineWebHeadersMiddlewareTests
{
    [Fact]
    public async Task BrowserBoundary_IsAppliedBeforeDownstreamContent()
    {
        var context = new DefaultHttpContext();
        var called = false;
        var middleware = new OfflineWebHeadersMiddleware(next =>
        {
            called = true;
            Assert.Equal(OfflineWebHeadersMiddleware.Policy, next.Response.Headers.ContentSecurityPolicy);
            Assert.Equal("no-referrer", next.Response.Headers["Referrer-Policy"]);
            Assert.Equal("nosniff", next.Response.Headers.XContentTypeOptions);
            Assert.Contains("connect-src 'self'", OfflineWebHeadersMiddleware.Policy, System.StringComparison.Ordinal);
            Assert.Contains("worker-src 'none'", OfflineWebHeadersMiddleware.Policy, System.StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", OfflineWebHeadersMiddleware.Policy, System.StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-inline", OfflineWebHeadersMiddleware.Policy, System.StringComparison.Ordinal);
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);
        Assert.True(called);
    }
}
