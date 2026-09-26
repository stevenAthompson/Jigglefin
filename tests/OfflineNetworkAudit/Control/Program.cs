using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

// Positive controls use loopback only and their own child, never public hosts.
if (args.Length != 1)
{
    throw new ArgumentException("Supply the exact test ffprobe executable.");
}

_ = await Dns.GetHostAddressesAsync("localhost");
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var incoming = await listener.AcceptTcpClientAsync();
    }

    using (var udp = new UdpClient())
    {
        await udp.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, port));
    }

    var start = new ProcessStartInfo(args[0])
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardError = true, RedirectStandardOutput = true
    };
    foreach (var argument in new[] { "-v", "error", $"http://127.0.0.1:{port}/local-control" })
    {
        start.ArgumentList.Add(argument);
    }

    using var helper = Process.Start(start)!;
    using var request = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(20));
    await using (var stream = request.GetStream())
    {
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
    }

    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    if (helper.ExitCode == 0)
    {
        throw new InvalidOperationException("The deliberate loopback 404 should not be playable.");
    }

    Console.WriteLine("Managed DNS/TCP/UDP and native child HTTP loopback controls completed.");
}
finally
{
    listener.Stop();
}
