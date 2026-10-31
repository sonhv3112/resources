using System.Net;
using System.Net.Sockets;
using System.Text;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Tests;

public class ProxyConfigTests
{
    [Theory]
    [InlineData("1.2.3.4:8080", ProxyType.Http, "1.2.3.4", 8080, null, null)]
    [InlineData("1.2.3.4:8080:user:pass", ProxyType.Http, "1.2.3.4", 8080, "user", "pass")]
    [InlineData("user:pass@1.2.3.4:8080", ProxyType.Http, "1.2.3.4", 8080, "user", "pass")]
    [InlineData("socks5://user:pass@proxy.example.com:1080", ProxyType.Socks5, "proxy.example.com", 1080, "user", "pass")]
    [InlineData("socks5://proxy.example.com:1080:user:pass", ProxyType.Socks5, "proxy.example.com", 1080, "user", "pass")]
    [InlineData("socks4://1.2.3.4:1080", ProxyType.Socks4, "1.2.3.4", 1080, null, null)]
    [InlineData("https://1.2.3.4:443", ProxyType.Http, "1.2.3.4", 443, null, null)]
    [InlineData("  http://1.2.3.4:3128/  ", ProxyType.Http, "1.2.3.4", 3128, null, null)]
    [InlineData("1.2.3.4:8080:user:pa:ss", ProxyType.Http, "1.2.3.4", 8080, "user", "pa:ss")]
    [InlineData("u%40x:p%3Aw@h:1", ProxyType.Http, "h", 1, "u@x", "p:w")]
    public void Parses_common_formats(string raw, ProxyType type, string host, int port, string? user, string? pass)
    {
        var p = ProxyConfig.Parse(raw)!;
        Assert.Equal(type, p.Type);
        Assert.Equal(host, p.Host);
        Assert.Equal(port, p.Port);
        Assert.Equal(user, p.Username);
        Assert.Equal(pass, p.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_means_direct(string? raw) => Assert.Null(ProxyConfig.Parse(raw));

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("ftp://1.2.3.4:21")]
    [InlineData("user@1.2.3.4:80")]
    [InlineData(":8080")]
    [InlineData("socks4://u:p@1.2.3.4:1080")]
    public void Rejects_invalid(string raw)
    {
        Assert.False(ProxyConfig.TryParse(raw, out var cfg, out var err));
        Assert.Null(cfg);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void ToString_hides_password()
    {
        var p = ProxyConfig.Parse("socks5://alice:secret@h:1")!;
        Assert.DoesNotContain("secret", p.ToString());
    }
}

/// <summary>End-to-end: Chrome-style client -> ProxyRelay -> fake upstream proxy -> fake origin.</summary>
public class ProxyRelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Http_upstream_with_auth_tunnels_connect()
    {
        await using var origin = new EchoOrigin();
        await using var upstream = new FakeHttpProxy("alice", "s3cret");
        await using var relay = new ProxyRelay(new ProxyConfig(ProxyType.Http, "127.0.0.1", upstream.Port, "alice", "s3cret"));

        var reply = await ConnectThroughRelay(relay, $"localhost:{origin.Port}", "ping");
        Assert.Equal("echo:ping", reply);
        Assert.Equal($"localhost:{origin.Port}", upstream.LastTarget); // host name forwarded, not resolved locally
    }

    [Fact]
    public async Task Http_upstream_wrong_password_returns_502()
    {
        await using var upstream = new FakeHttpProxy("alice", "s3cret");
        await using var relay = new ProxyRelay(new ProxyConfig(ProxyType.Http, "127.0.0.1", upstream.Port, "alice", "WRONG"));
        var errors = new List<string>();
        relay.Error += errors.Add;

        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, relay.Port);
        var s = c.GetStream();
        await s.WriteAsync("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n"u8.ToArray());
        var line = await ReadLineAsync(s);
        Assert.StartsWith("HTTP/1.1 502", line);
        Assert.Contains(errors, e => e.Contains("407"));
    }

    [Fact]
    public async Task Socks5_upstream_with_auth_tunnels_connect()
    {
        await using var origin = new EchoOrigin();
        await using var upstream = new FakeSocks5("bob", "pw");
        await using var relay = new ProxyRelay(new ProxyConfig(ProxyType.Socks5, "127.0.0.1", upstream.Port, "bob", "pw"));

        var reply = await ConnectThroughRelay(relay, $"localhost:{origin.Port}", "hello");
        Assert.Equal("echo:hello", reply);
        Assert.Equal("localhost", upstream.LastHost); // ATYP=domain, so DNS happens on the proxy
    }

    [Fact]
    public async Task Socks5_wrong_password_returns_502()
    {
        await using var upstream = new FakeSocks5("bob", "pw");
        await using var relay = new ProxyRelay(new ProxyConfig(ProxyType.Socks5, "127.0.0.1", upstream.Port, "bob", "nope"));
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, relay.Port);
        var s = c.GetStream();
        await s.WriteAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n"u8.ToArray());
        Assert.StartsWith("HTTP/1.1 502", await ReadLineAsync(s));
    }

    [Fact]
    public async Task Plain_http_via_http_upstream_adds_proxy_authorization()
    {
        await using var upstream = new FakeHttpProxy("alice", "s3cret", answerPlainHttp: true);
        await using var relay = new ProxyRelay(new ProxyConfig(ProxyType.Http, "127.0.0.1", upstream.Port, "alice", "s3cret"));

        using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"http://{relay.Address}"), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        var body = await http.GetStringAsync("http://site.test/path?q=1");
        Assert.Equal("plain-ok GET http://site.test/path?q=1", body);
    }

    [Fact]
    public void SplitHostPort_handles_ipv6_and_defaults()
    {
        Assert.Equal(("example.com", 443), ProxyRelay.SplitHostPort("example.com:443", 80));
        Assert.Equal(("::1", 8443), ProxyRelay.SplitHostPort("[::1]:8443", 80));
        Assert.Equal(("example.com", 80), ProxyRelay.SplitHostPort("example.com", 80));
    }

    private static async Task<string> ConnectThroughRelay(ProxyRelay relay, string target, string payload)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, relay.Port, cts.Token);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"), cts.Token);
        var status = await ReadLineAsync(s);
        Assert.StartsWith("HTTP/1.1 200", status);
        await ReadLineAsync(s); // blank line
        await s.WriteAsync(Encoding.ASCII.GetBytes(payload + "\n"), cts.Token);
        return await ReadLineAsync(s);
    }

    internal static async Task<string> ReadLineAsync(Stream s)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        using var cts = new CancellationTokenSource(Timeout);
        while (await s.ReadAsync(b, cts.Token) == 1)
        {
            if (b[0] == '\n') break;
            if (b[0] != '\r') sb.Append((char)b[0]);
        }
        return sb.ToString();
    }
}

/// <summary>Line echo server: replies "echo:&lt;line&gt;".</summary>
internal sealed class EchoOrigin : IAsyncDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }

    public EchoOrigin()
    {
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = c;
                    var s = c.GetStream();
                    var line = await ProxyRelayTests.ReadLineAsync(s);
                    await s.WriteAsync(Encoding.ASCII.GetBytes($"echo:{line}\n"));
                });
            }
        });
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); _l.Stop(); return ValueTask.CompletedTask; }
}

/// <summary>Fake HTTP proxy that requires Basic auth and tunnels CONNECT to localhost.</summary>
internal sealed class FakeHttpProxy : IAsyncDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _expectedAuth;
    private readonly bool _answerPlainHttp;
    public int Port { get; }
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _targets = new();
    /// <summary>Every CONNECT/forward target seen, in order (browsers also send background requests).</summary>
    public IReadOnlyCollection<string> Targets => _targets;
    public string? LastTarget => _targets.LastOrDefault();

    public FakeHttpProxy(string user, string pass, bool answerPlainHttp = false)
    {
        _expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        _answerPlainHttp = answerPlainHttp;
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => HandleAsync(c));
        }
    }

    private async Task HandleAsync(TcpClient c)
    {
        using var _ = c;
        var s = c.GetStream();
        var lines = new List<string>();
        string line;
        while ((line = await ProxyRelayTests.ReadLineAsync(s)).Length > 0) lines.Add(line);
        var parts = lines[0].Split(' ');
        var auth = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1].Trim();
        if (auth != _expectedAuth)
        {
            await s.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
            return;
        }

        if (parts[0] == "CONNECT")
        {
            _targets.Enqueue(parts[1]);
            var (host, port) = (parts[1][..parts[1].LastIndexOf(':')], int.Parse(parts[1][(parts[1].LastIndexOf(':') + 1)..]));
            using var up = new TcpClient();
            await up.ConnectAsync(host, port);
            await s.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
            var us = up.GetStream();
            await Task.WhenAny(s.CopyToAsync(us), us.CopyToAsync(s));
            return;
        }

        if (_answerPlainHttp)
        {
            var body = $"plain-ok {parts[0]} {parts[1]}";
            await s.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            return;
        }

        // Behave like a real forward proxy: absolute-form in, origin-form out.
        var uri = new Uri(parts[1]);
        _targets.Enqueue($"{uri.Host}:{uri.Port}");
        using var origin = new TcpClient();
        await origin.ConnectAsync(uri.Host, uri.Port);
        var os = origin.GetStream();
        var head = new StringBuilder($"{parts[0]} {uri.PathAndQuery} {parts[2]}\r\n");
        foreach (var h in lines.Skip(1).Where(l => !l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)))
            head.Append(h).Append("\r\n");
        head.Append("\r\n");
        await os.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await os.CopyToAsync(s);
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); _l.Stop(); return ValueTask.CompletedTask; }
}

/// <summary>Fake SOCKS5 server with username/password auth that tunnels to localhost.</summary>
internal sealed class FakeSocks5 : IAsyncDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _user, _pass;
    public int Port { get; }
    public string? LastHost { get; private set; }

    public FakeSocks5(string user, string pass)
    {
        (_user, _pass) = (user, pass);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => HandleAsync(c));
        }
    }

    private static async Task<byte[]> Read(Stream s, int n)
    {
        var b = new byte[n];
        await s.ReadExactlyAsync(b);
        return b;
    }

    private async Task HandleAsync(TcpClient c)
    {
        using var _ = c;
        var s = c.GetStream();
        var hdr = await Read(s, 2);
        var methods = await Read(s, hdr[1]);
        if (!methods.Contains((byte)0x02)) { await s.WriteAsync(new byte[] { 5, 0xFF }); return; }
        await s.WriteAsync(new byte[] { 5, 2 });

        await Read(s, 1);
        var u = Encoding.UTF8.GetString(await Read(s, (await Read(s, 1))[0]));
        var p = Encoding.UTF8.GetString(await Read(s, (await Read(s, 1))[0]));
        if (u != _user || p != _pass) { await s.WriteAsync(new byte[] { 1, 1 }); return; }
        await s.WriteAsync(new byte[] { 1, 0 });

        var req = await Read(s, 4);
        if (req[3] != 0x03) { await s.WriteAsync(new byte[] { 5, 8, 0, 1, 0, 0, 0, 0, 0, 0 }); return; }
        var host = Encoding.ASCII.GetString(await Read(s, (await Read(s, 1))[0]));
        var portBytes = await Read(s, 2);
        LastHost = host;
        using var up = new TcpClient();
        await up.ConnectAsync(host, portBytes[0] << 8 | portBytes[1]);
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
        var us = up.GetStream();
        await Task.WhenAny(s.CopyToAsync(us), us.CopyToAsync(s));
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); _l.Stop(); return ValueTask.CompletedTask; }
}
