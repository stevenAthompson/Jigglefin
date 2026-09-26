using System;
using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;
using MediaBrowser.Common.Net;
using Xunit;

namespace Jellyfin.Networking.Tests;

public sealed class OfflineHostParsingTests
{
    [Fact]
    public void HostParsing_NeverStartsNameResolution()
    {
        using var listener = new ResolutionListener();
        // Local-only positive control proves the real resolver's events are
        // observable. The application parser below must never enter that path.
        Assert.NotEmpty(Dns.GetHostAddresses("localhost"));
        Assert.True(listener.Starts > 0);
        listener.Reset();

        foreach (var name in new[] { "never-resolve.invalid", "never-resolve.invalid:8096", "metadata.local", "proxy", "https://never-resolve.invalid" })
        {
            Assert.False(NetworkUtils.TryParseHost(name, out var addresses, true, true));
            Assert.Empty(addresses!);
        }

        Assert.True(NetworkUtils.TryParseHost("localhost:8096", out var local, true, true));
        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, local);
        Assert.True(NetworkUtils.TryParseHost("[2001:db8::1234]:8096", out var ipv6, false, true));
        Assert.Equal(IPAddress.Parse("2001:db8::1234"), Assert.Single(ipv6!));
        Assert.Equal(0, listener.Starts);
    }

    [Theory]
    [InlineData("localhost", false, false)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", true, false)]
    [InlineData("[::1]:8096", true, false)]
    [InlineData("127.0.0.1", false, false)]
    public void DisabledAddressFamily_IsNotReturned(string host, bool ipv4, bool ipv6)
        => Assert.False(NetworkUtils.TryParseHost(host, out _, ipv4, ipv6));

    private sealed class ResolutionListener : EventListener
    {
        private int _starts;

        public int Starts => Volatile.Read(ref _starts);

        public void Reset() => Interlocked.Exchange(ref _starts, 0);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Net.NameResolution")
            {
                EnableEvents(eventSource, EventLevel.LogAlways);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "ResolutionStart")
            {
                Interlocked.Increment(ref _starts);
            }
        }
    }
}
