using System.Net.Sockets;
using System.Text;

namespace GpmLite.Core.Proxy;

/// <summary>Opens a TCP tunnel to <c>host:port</c> through an upstream proxy (or directly when proxy is null).</summary>
public static class UpstreamConnector
{
    public static async Task<Stream> ConnectAsync(ProxyConfig? proxy, string host, int port, CancellationToken ct)
    {
        if (proxy is null)
        {
            var direct = new TcpClient { NoDelay = true };
            await direct.ConnectAsync(host, port, ct);
            return direct.GetStream();
        }

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port, ct);
            var stream = tcp.GetStream();
            switch (proxy.Type)
            {
                case ProxyType.Http: await HttpConnectAsync(stream, proxy, host, port, ct); break;
                case ProxyType.Socks5: await Socks5ConnectAsync(stream, proxy, host, port, ct); break;
                case ProxyType.Socks4: await Socks4aConnectAsync(stream, proxy, host, port, ct); break;
            }
            return stream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task HttpConnectAsync(Stream s, ProxyConfig p, string host, int port, CancellationToken ct)
    {
        var target = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        var sb = new StringBuilder($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n");
        if (p.HasAuth) sb.Append($"Proxy-Authorization: Basic {BasicToken(p)}\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);

        var head = await HttpHead.ReadAsync(s, ct) ?? throw new IOException("Upstream proxy closed the connection");
        var status = head.StatusCode;
        if (status == 407) throw new ProxyAuthException("Upstream proxy rejected credentials (407)");
        if (status != 200) throw new IOException($"Upstream proxy CONNECT failed: {head.FirstLine}");
    }

    internal static string BasicToken(ProxyConfig p) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{p.Username}:{p.Password}"));

    private static async Task Socks5ConnectAsync(Stream s, ProxyConfig p, string host, int port, CancellationToken ct)
    {
        byte[] greeting = p.HasAuth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await s.WriteAsync(greeting, ct);
        var choice = await ReadExactAsync(s, 2, ct);
        if (choice[0] != 0x05) throw new IOException("Not a SOCKS5 proxy");

        if (choice[1] == 0x02)
        {
            if (!p.HasAuth) throw new ProxyAuthException("SOCKS5 proxy requires credentials");
            var u = Encoding.UTF8.GetBytes(p.Username!);
            var pw = Encoding.UTF8.GetBytes(p.Password ?? "");
            if (u.Length > 255 || pw.Length > 255) throw new ProxyAuthException("SOCKS5 credentials too long");
            var auth = new byte[3 + u.Length + pw.Length];
            auth[0] = 0x01;
            auth[1] = (byte)u.Length;
            u.CopyTo(auth, 2);
            auth[2 + u.Length] = (byte)pw.Length;
            pw.CopyTo(auth, 3 + u.Length);
            await s.WriteAsync(auth, ct);
            var res = await ReadExactAsync(s, 2, ct);
            if (res[1] != 0x00) throw new ProxyAuthException("SOCKS5 authentication failed");
        }
        else if (choice[1] != 0x00) throw new IOException("SOCKS5 proxy offered no acceptable auth method");

        // CONNECT using a domain name (ATYP 0x03) so DNS resolves on the proxy side.
        var hostBytes = Encoding.ASCII.GetBytes(host);
        if (hostBytes.Length > 255) throw new IOException("Host name too long for SOCKS5");
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03;
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[^2] = (byte)(port >> 8);
        req[^1] = (byte)port;
        await s.WriteAsync(req, ct);

        var rep = await ReadExactAsync(s, 4, ct);
        if (rep[1] != 0x00) throw new IOException($"SOCKS5 CONNECT failed (code {rep[1]})");
        var addrLen = rep[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadExactAsync(s, 1, ct))[0],
            _ => throw new IOException("SOCKS5 reply has unknown address type"),
        };
        await ReadExactAsync(s, addrLen + 2, ct); // bound address + port
    }

    private static async Task Socks4aConnectAsync(Stream s, ProxyConfig p, string host, int port, CancellationToken ct)
    {
        var user = Encoding.ASCII.GetBytes(p.Username ?? "");
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var req = new byte[8 + user.Length + 1 + hostBytes.Length + 1];
        req[0] = 0x04; req[1] = 0x01;
        req[2] = (byte)(port >> 8); req[3] = (byte)port;
        req[4] = 0; req[5] = 0; req[6] = 0; req[7] = 1; // 0.0.0.1 => SOCKS4a, resolve remotely
        user.CopyTo(req, 8);
        hostBytes.CopyTo(req, 8 + user.Length + 1);
        await s.WriteAsync(req, ct);
        var rep = await ReadExactAsync(s, 8, ct);
        if (rep[1] != 0x5A) throw new IOException($"SOCKS4 CONNECT rejected (code 0x{rep[1]:X2})");
    }

    internal static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }
}

public sealed class ProxyAuthException(string message) : IOException(message);

/// <summary>Minimal HTTP head reader that never consumes bytes past the blank line.</summary>
internal sealed record HttpHead(string FirstLine, List<(string Name, string Value)> Headers)
{
    private const int MaxHeadBytes = 64 * 1024;

    public int StatusCode
    {
        get
        {
            var parts = FirstLine.Split(' ', 3);
            return parts.Length >= 2 && int.TryParse(parts[1], out var c) ? c : 0;
        }
    }

    public string? Get(string name) =>
        Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public static async Task<HttpHead?> ReadAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>(1024);
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return buf.Count == 0 ? null : throw new IOException("Connection closed mid-header");
            buf.Add(one[0]);
            if (buf.Count > MaxHeadBytes) throw new IOException("HTTP header too large");
            var c = buf.Count;
            if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n') break;
        }

        var lines = Encoding.Latin1.GetString(buf.ToArray()).Split("\r\n");
        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var idx = line.IndexOf(':');
            if (idx > 0) headers.Add((line[..idx].Trim(), line[(idx + 1)..].Trim()));
        }
        return new HttpHead(lines[0], headers);
    }
}
