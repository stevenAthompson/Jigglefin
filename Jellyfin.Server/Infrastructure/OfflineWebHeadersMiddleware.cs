using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Server.Infrastructure;

/// <summary>All browser-rendered server pages may load only this origin's assets.</summary>
public sealed class OfflineWebHeadersMiddleware
{
    /// <summary>The offline browser boundary, including any alternate HTML routes.</summary>
    public const string Policy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; media-src 'self' blob:; font-src 'self'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    private readonly RequestDelegate _next;

    /// <summary>Initializes a new instance of the <see cref="OfflineWebHeadersMiddleware"/> class.</summary>
    /// <param name="next">The next middleware.</param>
    public OfflineWebHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Adds offline browser restrictions before static files and endpoints run.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>The request task.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.ContentSecurityPolicy = Policy;
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        return _next(context);
    }
}
