using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;

namespace Jellyfin.Server.Infrastructure;

/// <summary>
/// Enforces the offline boundary for every factory-created HTTP client, including
/// unnamed clients. This terminal handler has no transport and cannot resolve DNS.
/// </summary>
public sealed class OfflineHttpClientFilter : IHttpMessageHandlerBuilderFilter
{
    /// <inheritdoc />
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        => builder =>
        {
            next(builder);
            builder.PrimaryHandler.Dispose();
            builder.PrimaryHandler = new OfflineHandler();
        };

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Jigglefin is offline: outbound HTTP requests are disabled."));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Jigglefin is offline: outbound HTTP requests are disabled.");
    }
}
