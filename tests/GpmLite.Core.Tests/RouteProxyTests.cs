using System.Text.Json;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Tests;

public class RouteProxyParsingTests
{
    [Theory]
    [InlineData("route:demo:abc123", "demo", "abc123")]
    [InlineData("  route:demo-flat:KEY  ", "demo-flat", "KEY")]
    [InlineData("route:vendor:id:secret", "vendor", "id:secret")] // key keeps its own ':'
    [InlineData("ROUTE:Demo:Key", "Demo", "Key")]                 // scheme is case-insensitive
    public void Parses_route_syntax(string raw, string prefix, string key)
    {
        Assert.True(RouteProxy.TryParse(raw, out var p, out var k));
        Assert.Equal(prefix, p);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2.3.4:8080")]       // plain proxy, not a route
    [InlineData("route:")]
    [InlineData("route:demo")]          // missing key
    [InlineData("route:demo:")]         // empty key
    [InlineData("route::key")]          // empty prefix
    public void Rejects_non_route(string? raw)
    {
        Assert.False(RouteProxy.TryParse(raw, out _, out _));
    }

    [Fact]
    public void IsRoute_matches_scheme()
    {
        Assert.True(RouteProxy.IsRoute("route:x:y"));
        Assert.False(RouteProxy.IsRoute("socks5://1.2.3.4:1080"));
        Assert.False(RouteProxy.IsRoute(null));
    }

    [Fact]
    public void Format_roundtrips()
    {
        var raw = RouteProxy.Format("demo", "id:secret");
        Assert.Equal("route:demo:id:secret", raw);
        Assert.True(RouteProxy.TryParse(raw, out var p, out var k));
        Assert.Equal("demo", p);
        Assert.Equal("id:secret", k);
    }
}

public class RouteProxySubstitutionTests
{
    [Fact]
    public void Substitutes_full_key_and_public_ip()
    {
        var result = RouteProxy.Substitute("https://h/get?key={api_key}&ip={public_ip}", "secret", "9.9.9.9");
        Assert.Equal("https://h/get?key=secret&ip=9.9.9.9", result);
    }

    [Theory]
    [InlineData("id:secret")]
    [InlineData("id|secret")] // '|' is also a valid separator
    public void Substitutes_indexed_key_parts(string apiKey)
    {
        var result = RouteProxy.Substitute("u={api_key[0]}&p={api_key[1]}", apiKey, null);
        Assert.Equal("u=id&p=secret", result);
    }

    [Fact]
    public void Missing_index_becomes_empty_and_null_ip_is_blank()
    {
        var result = RouteProxy.Substitute("a={api_key[5]}|ip={public_ip}", "only", null);
        Assert.Equal("a=|ip=", result);
    }

    [Fact]
    public void Null_template_is_safe()
    {
        Assert.Equal("", RouteProxy.Substitute(null, "k", "1.1.1.1"));
    }
}

public class RouteProxyJsonPathTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Reads_flat_and_nested_and_array_paths()
    {
        var root = Root("""
            {
              "status": "ok",
              "host": "1.2.3.4",
              "data": { "proxy": { "ip": "5.6.7.8", "port": 1080 } },
              "servers": [ { "addr": "a.example" }, { "addr": "b.example" } ]
            }
            """);
        Assert.Equal("ok", RouteProxy.ReadPath(root, "status"));
        Assert.Equal("5.6.7.8", RouteProxy.ReadPath(root, "data.proxy.ip"));
        Assert.Equal("1080", RouteProxy.ReadPath(root, "data.proxy.port")); // number -> string
        Assert.Equal("b.example", RouteProxy.ReadPath(root, "servers.1.addr"));
        Assert.Equal("a.example", RouteProxy.ReadPath(root, "servers[0].addr"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("data.proxy.nope")]
    [InlineData("servers.9.addr")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_for_missing(string? path)
    {
        var root = Root("""{ "data": { "proxy": { "ip": "x" } }, "servers": [] }""");
        Assert.Null(RouteProxy.ReadPath(root, path));
    }

    [Fact]
    public void ParseResponse_builds_proxy_from_nested_paths()
    {
        var provider = new RouteProvider
        {
            Prefix = "t",
            DefaultType = "socks5",
            TypePath = "data.proxy.protocol",
            HostPath = "data.proxy.ip",
            PortPath = "data.proxy.port",
            UserPath = "data.proxy.user",
            PassPath = "data.proxy.pass",
        };
        var body = """
            { "data": { "proxy": { "protocol": "socks5", "ip": "5.6.7.8", "port": 1080, "user": "u", "pass": "p" } } }
            """;
        var cfg = RouteProxy.ParseResponse(provider, body);
        Assert.Equal(ProxyType.Socks5, cfg.Type);
        Assert.Equal("5.6.7.8", cfg.Host);
        Assert.Equal(1080, cfg.Port);
        Assert.Equal("u", cfg.Username);
        Assert.Equal("p", cfg.Password);
    }

    [Fact]
    public void ParseResponse_uses_default_type_and_allows_no_auth()
    {
        var provider = new RouteProvider { Prefix = "t", DefaultType = "http", HostPath = "host", PortPath = "port" };
        var cfg = RouteProxy.ParseResponse(provider, """{ "host": "1.2.3.4", "port": "8080" }""");
        Assert.Equal(ProxyType.Http, cfg.Type);
        Assert.Equal("1.2.3.4", cfg.Host);
        Assert.Equal(8080, cfg.Port);
        Assert.False(cfg.HasAuth);
    }

    [Fact]
    public void ParseResponse_surfaces_vendor_message_when_missing_fields()
    {
        var provider = new RouteProvider { Prefix = "t", HostPath = "host", PortPath = "port", MessagePath = "message" };
        var e = Assert.Throws<IOException>(() =>
            RouteProxy.ParseResponse(provider, """{ "message": "rate limit exceeded" }"""));
        Assert.Contains("rate limit exceeded", e.Message);
    }

    [Fact]
    public void ParseResponse_rejects_invalid_port()
    {
        var provider = new RouteProvider { Prefix = "t", HostPath = "host", PortPath = "port" };
        Assert.Throws<IOException>(() =>
            RouteProxy.ParseResponse(provider, """{ "host": "1.2.3.4", "port": "70000" }"""));
    }
}

public class RouteProxyThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_previous_rotate_is_allowed()
    {
        Assert.Equal(TimeSpan.Zero, RouteProxy.RemainingCooldown(null, 60_000, Now));
    }

    [Fact]
    public void Within_window_returns_remaining()
    {
        var last = Now - TimeSpan.FromSeconds(20);
        var remaining = RouteProxy.RemainingCooldown(last, 60_000, Now);
        Assert.Equal(TimeSpan.FromSeconds(40), remaining);
    }

    [Fact]
    public void After_window_is_allowed()
    {
        var last = Now - TimeSpan.FromSeconds(90);
        Assert.Equal(TimeSpan.Zero, RouteProxy.RemainingCooldown(last, 60_000, Now));
    }

    [Fact]
    public void Zero_interval_never_throttles()
    {
        Assert.Equal(TimeSpan.Zero, RouteProxy.RemainingCooldown(Now, 0, Now));
    }
}

/// <summary>Fake HTTP sender that returns canned bodies; records the exact (substituted) URLs it was asked for.</summary>
internal sealed class FakeRouteHttp(params string[] responses) : IRouteHttp
{
    private int _i;
    public List<string> RequestedUrls { get; } = [];
    public List<string?> RequestedBodies { get; } = [];

    public Task<string> SendAsync(RouteRequest request, CancellationToken ct)
    {
        RequestedUrls.Add(request.Url);
        RequestedBodies.Add(request.PostData);
        var body = responses[Math.Min(_i, responses.Length - 1)];
        _i++;
        return Task.FromResult(body);
    }
}

public class RouteProxyServiceTests
{
    private static RouteProvider SplitKeyProvider() => new()
    {
        Prefix = "demo",
        Name = "Demo",
        CurrentProxyRequest = new RouteRequest { Url = "https://h/current?id={api_key[0]}&secret={api_key[1]}&ip={public_ip}" },
        NewProxyRequest = new RouteRequest { Url = "https://h/rotate?id={api_key[0]}" },
        HostPath = "host",
        PortPath = "port",
        MinRotateMs = 60_000,
    };

    [Fact]
    public async Task ResolveAsync_substitutes_key_and_public_ip_then_parses()
    {
        var http = new FakeRouteHttp("""{ "host": "1.2.3.4", "port": 8080 }""");
        var svc = new RouteProxyService([SplitKeyProvider()], http, publicIpProvider: _ => Task.FromResult<string?>("9.9.9.9"));

        var cfg = await svc.ResolveAsync(svc.Providers[0], "myid:mysecret");

        Assert.Equal("1.2.3.4", cfg.Host);
        Assert.Equal(8080, cfg.Port);
        Assert.Equal("https://h/current?id=myid&secret=mysecret&ip=9.9.9.9", http.RequestedUrls.Single());
    }

    [Fact]
    public async Task ResolveRawAsync_finds_provider_by_prefix()
    {
        var http = new FakeRouteHttp("""{ "host": "5.5.5.5", "port": 3128 }""");
        var svc = new RouteProxyService([SplitKeyProvider()], http, publicIpProvider: _ => Task.FromResult<string?>("1.1.1.1"));

        var cfg = await svc.ResolveRawAsync("route:demo:a:b");
        Assert.Equal("5.5.5.5", cfg.Host);
    }

    [Fact]
    public async Task ChangeIpAsync_throttles_second_call()
    {
        var http = new FakeRouteHttp("""{ "host": "1.1.1.1", "port": 80 }""");
        var svc = new RouteProxyService([SplitKeyProvider()], http);

        var first = await svc.ChangeIpAsync(svc.Providers[0], "a:b");
        Assert.Equal("1.1.1.1", first.Host);
        // Second immediate rotate hits the min-rotate throttle.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ChangeIpAsync(svc.Providers[0], "a:b"));
    }

    [Fact]
    public void Create_includes_builtins()
    {
        var svc = RouteProxyService.Create(Path.Combine(Path.GetTempPath(), "gpmlite-no-such-dir-" + Guid.NewGuid()));
        Assert.NotEmpty(svc.Providers);
        Assert.Contains(svc.Providers, p => p.Prefix == "demo-flat");
    }
}
