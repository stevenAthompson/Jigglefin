using Jellyfin.Api.Filters;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Server.Integration.Tests;

// Used only by EncodedQueryStringTest to exercise the test assembly's echo
// controller and real decoding/model binding. All production capability tests
// keep the fail-closed filter; no test-only endpoint enters its shipped allowlist.
public sealed class EncodingApplicationFactory : JellyfinApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.PostConfigure<MvcOptions>(options =>
        {
            for (var index = options.Filters.Count - 1; index >= 0; index--)
            {
                if (options.Filters[index] is TypeFilterAttribute filter && filter.ImplementationType == typeof(LiveCapabilityFilter))
                {
                    options.Filters.RemoveAt(index);
                }
            }
        }));
    }
}
