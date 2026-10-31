using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GpmLite.Core.Proxy;

/// <summary>
/// Per-profile local HTTP proxy on 127.0.0.1 without auth. Chrome talks to it; the relay forwards every
/// connection through the profile's upstream proxy, handling credentials and SOCKS itself. Host names are
/// passed through unresolved, so DNS happens on the upstream side (no local DNS leak).
/// </summary>
public sealed class ProxyRelay : IAsyncDisposable
{
    private readonly ProxyConfig _upstream;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public int Port { get; }
    public string Address => $"127.0.0.1:{Port}";

    /// <summary>Raised for connection-level errors (bad credentials, refused CONNECT...), for logging/UI.</summary>
    public event Action<string>? Error;

    public ProxyRelay(ProxyConfig upstream)
    {
        _upstream = upstream;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        using var _ = client;
        var cs = client.GetStream();
        try
        {
            var head = await HttpHead.ReadAsync(cs, ct);
            if (head is null) return;
            var parts = head.FirstLine.Split(' ');
            if (parts.Length != 3) { await RespondAsync(cs, "400 Bad Request", ct); return; }
            var (method, target) = (parts[0], parts[1]);

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                var (host, port) = SplitHostPort(target, 443);
                Stream upstream;
                try { upstream = await UpstreamConnector.ConnectAsync(_upstream, host, port, ct); }
                catch (Exception e) when (e is IOException or SocketException)
                {
                    Error?.Invoke($"CONNECT {target}: {e.Message}");
                    await RespondAsync(cs, "502 Bad Gateway", ct);
                    return;
                }
                await using (upstream)
                {
                    await cs.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                    await PipeAsync(cs, upstream, ct);
                }
                return;
            }

            // Plain http:// request in absolute form.
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "http")
            {
                await RespondAsync(cs, "400 Bad Request", ct);
                return;
            }

            await ForwardPlainHttpAsync(cs, head, method, uri, parts[2], ct);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            // Client or upstream went away; nothing to do.
        }
    }

    private async Task ForwardPlainHttpAsync(NetworkStream cs, HttpHead head, string method, Uri uri, string version, CancellationToken ct)
    {
        Stream upstream;
        bool viaHttpProxy = _upstream.Type == ProxyType.Http;
        try
        {
            upstream = viaHttpProxy
                ? await OpenRawAsync(_upstream.Host, _upstream.Port, ct)
                : await UpstreamConnector.ConnectAsync(_upstream, uri.Host, uri.Port, ct);
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            Error?.Invoke($"{method} {uri}: {e.Message}");
            await RespondAsync(cs, "502 Bad Gateway", ct);
            return;
        }

        await using (upstream)
        {
            // HTTP proxies take absolute-form; origin servers (via SOCKS) take origin-form.
            var requestTarget = viaHttpProxy ? uri.AbsoluteUri : uri.PathAndQuery;
            var sb = new StringBuilder($"{method} {requestTarget} {version}\r\n");
            foreach (var (name, value) in head.Headers)
            {
                if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
            }
            // One request per connection keeps the relay stateless: the next request may target another host.
            sb.Append("Connection: close\r\n");
            if (viaHttpProxy && _upstream.HasAuth)
                sb.Append($"Proxy-Authorization: Basic {UpstreamConnector.BasicToken(_upstream)}\r\n");
            sb.Append("\r\n");
            await upstream.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()), ct);
            await PipeAsync(cs, upstream, ct);
        }
    }

    private static async Task<Stream> OpenRawAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct);
        return tcp.GetStream();
    }

    internal static (string Host, int Port) SplitHostPort(string target, int defaultPort)
    {
        if (target.StartsWith('['))
        {
            var end = target.IndexOf(']');
            var h = target[1..end];
            var rest = target[(end + 1)..];
            return (h, rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) ? p6 : defaultPort);
        }
        var idx = target.LastIndexOf(':');
        return idx > 0 && int.TryParse(target[(idx + 1)..], out var p) ? (target[..idx], p) : (target, defaultPort);
    }

    private static async Task RespondAsync(Stream s, string status, CancellationToken ct)
    {
        try { await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct); }
        catch (IOException) { }
    }

    private static async Task PipeAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var t1 = CopyAsync(a, b, linked.Token);
        var t2 = CopyAsync(b, a, linked.Token);
        await Task.WhenAny(t1, t2);
        linked.Cancel(); // when one side closes, tear down the other
        try { await Task.WhenAll(t1, t2); } catch { /* expected on teardown */ }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buf = new byte[81920];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf, ct)) > 0)
                await to.WriteAsync(buf.AsMemory(0, n), ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        _cts.Dispose();
    }
}
