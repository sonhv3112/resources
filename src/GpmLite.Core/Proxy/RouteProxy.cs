using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GpmLite.Core.Proxy;

/// <summary>One HTTP call a provider makes: a URL template, verb, and optional body template.</summary>
public sealed class RouteRequest
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    [JsonPropertyName("post_data")] public string? PostData { get; set; }
}

/// <summary>
/// A rotating ("route") proxy provider template. Loaded from JSON (built-in or a manifest dropped in the
/// <c>route-providers</c> folder). Describes how to ask the vendor for the current proxy, how to rotate to a
/// new IP, and which dotted JSON paths hold the proxy fields in the response.
/// </summary>
public sealed class RouteProvider
{
    /// <summary>Short id used in the raw syntax <c>route:&lt;prefix&gt;:&lt;apiKey&gt;</c>.</summary>
    [JsonPropertyName("prefix")] public string Prefix { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>"Get current proxy" call. Required.</summary>
    [JsonPropertyName("current_proxy_request")] public RouteRequest? CurrentProxyRequest { get; set; }
    /// <summary>"Rotate / get new proxy" call. Optional; falls back to the current call.</summary>
    [JsonPropertyName("new_proxy_request")] public RouteRequest? NewProxyRequest { get; set; }

    [JsonPropertyName("default_type")] public string DefaultType { get; set; } = "http";
    [JsonPropertyName("type_path")] public string? TypePath { get; set; }
    [JsonPropertyName("host_path")] public string HostPath { get; set; } = "";
    [JsonPropertyName("port_path")] public string PortPath { get; set; } = "";
    [JsonPropertyName("user_path")] public string? UserPath { get; set; }
    [JsonPropertyName("pass_path")] public string? PassPath { get; set; }

    /// <summary>Optional dotted path to a vendor error message, surfaced when host/port are missing.</summary>
    [JsonPropertyName("message_path")] public string? MessagePath { get; set; }

    /// <summary>Minimum milliseconds between two rotate calls (vendor throttle).</summary>
    [JsonPropertyName("min_rotate_ms")] public int MinRotateMs { get; set; } = 60_000;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Prefix : $"{Name} ({Prefix})";
}

/// <summary>Abstraction over the actual HTTP send, so tests can feed canned responses without real network.</summary>
public interface IRouteHttp
{
    Task<string> SendAsync(RouteRequest request, CancellationToken ct);
}

/// <summary>Default HTTP sender, using the same HttpClient style as <see cref="ProxyChecker"/>.</summary>
public sealed class RouteHttp(TimeSpan? timeout = null) : IRouteHttp
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);

    public async Task<string> SendAsync(RouteRequest request, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = _timeout };
        using var http = new HttpClient(handler) { Timeout = _timeout };
        using var msg = new HttpRequestMessage(
            request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get,
            request.Url);
        if (msg.Method == HttpMethod.Post)
            msg.Content = new StringContent(request.PostData ?? "", Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}

/// <summary>Static helpers: raw-syntax parsing, placeholder substitution, dotted JSON extraction, throttle.</summary>
public static class RouteProxy
{
    public const string Scheme = "route:";
    private static readonly Regex IndexedKey = new(@"\{api_key\[(\d+)\]\}", RegexOptions.Compiled);

    /// <summary>True when a raw proxy string refers to a route provider (e.g. <c>route:demo:KEY</c>).</summary>
    public static bool IsRoute([NotNullWhen(true)] string? raw) =>
        raw is not null && raw.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>route:&lt;prefix&gt;:&lt;apiKey&gt;</c>. The api key may itself contain ':' (e.g. <c>id:secret</c>),
    /// so only the first ':' after the scheme splits prefix from key.
    /// </summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out string? prefix, [NotNullWhen(true)] out string? apiKey)
    {
        prefix = null;
        apiKey = null;
        if (!IsRoute(raw)) return false;
        var s = raw!.Trim()[Scheme.Length..];
        var sep = s.IndexOf(':');
        if (sep <= 0 || sep == s.Length - 1) return false;
        prefix = s[..sep].Trim();
        apiKey = s[(sep + 1)..].Trim();
        return prefix.Length > 0 && apiKey.Length > 0;
    }

    public static string Format(string prefix, string apiKey) => $"{Scheme}{prefix}:{apiKey}";

    /// <summary>
    /// Fills a URL/body template: <c>{api_key}</c>, <c>{api_key[0]}</c>/<c>{api_key[1]}</c> (key split by ':' or '|')
    /// and <c>{public_ip}</c> (this machine's public IP, for whitelisting).
    /// </summary>
    public static string Substitute(string? template, string apiKey, string? publicIp)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";
        var parts = apiKey.Split([':', '|'], StringSplitOptions.None);
        var result = IndexedKey.Replace(template, m =>
        {
            var i = int.Parse(m.Groups[1].Value);
            return i >= 0 && i < parts.Length ? parts[i] : "";
        });
        result = result.Replace("{api_key}", apiKey, StringComparison.Ordinal);
        result = result.Replace("{public_ip}", publicIp ?? "", StringComparison.Ordinal);
        return result;
    }

    /// <summary>
    /// Reads a dotted path out of a JSON element. Supports object keys, numeric segments as array indices
    /// (<c>data.0.host</c>) and bracket indices (<c>data[0].host</c>). Returns the element as a string, or null.
    /// </summary>
    public static string? ReadPath(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cur = root;
        foreach (var rawSeg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = rawSeg;
            // Expand "key[2]" into "key" then index 2.
            while (true)
            {
                var br = seg.IndexOf('[');
                if (br < 0) break;
                var key = seg[..br];
                if (key.Length > 0)
                {
                    if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out cur)) return null;
                }
                var close = seg.IndexOf(']', br);
                if (close < 0) return null;
                if (!int.TryParse(seg[(br + 1)..close], out var idx)) return null;
                if (cur.ValueKind != JsonValueKind.Array || idx >= cur.GetArrayLength()) return null;
                cur = cur[idx];
                seg = seg[(close + 1)..];
            }
            if (seg.Length == 0) continue;
            if (int.TryParse(seg, out var arrIdx) && cur.ValueKind == JsonValueKind.Array)
            {
                if (arrIdx >= cur.GetArrayLength()) return null;
                cur = cur[arrIdx];
            }
            else
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur)) return null;
            }
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number => cur.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => cur.GetRawText(),
        };
    }

    /// <summary>Builds a <see cref="ProxyConfig"/> from a vendor response body using a provider's dotted paths.</summary>
    public static ProxyConfig ParseResponse(RouteProvider provider, string body)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException e) { throw new IOException($"Phản hồi provider không phải JSON hợp lệ: {e.Message}"); }

        var host = ReadPath(root, provider.HostPath);
        var portText = ReadPath(root, provider.PortPath);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(portText))
        {
            var msg = ReadPath(root, provider.MessagePath);
            throw new IOException(string.IsNullOrWhiteSpace(msg)
                ? "Provider không trả về host/port proxy"
                : $"Provider báo lỗi: {msg}");
        }
        if (!int.TryParse(portText.Trim(), out var port) || port is < 1 or > 65535)
            throw new IOException($"Provider trả về port không hợp lệ: '{portText}'");

        var typeText = ReadPath(root, provider.TypePath) ?? provider.DefaultType;
        var type = typeText.Trim().ToLowerInvariant() switch
        {
            "socks4" or "socks4a" => ProxyType.Socks4,
            "socks5" or "socks5h" or "socks" => ProxyType.Socks5,
            _ => ProxyType.Http,
        };
        var user = ReadPath(root, provider.UserPath);
        var pass = ReadPath(root, provider.PassPath);
        return new ProxyConfig(type, host.Trim(), port,
            string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(pass) ? null : pass);
    }

    /// <summary>
    /// How long a rotate is still throttled for. Returns <see cref="TimeSpan.Zero"/> when rotation is allowed.
    /// </summary>
    public static TimeSpan RemainingCooldown(DateTimeOffset? lastRotate, int minRotateMs, DateTimeOffset now)
    {
        if (lastRotate is null || minRotateMs <= 0) return TimeSpan.Zero;
        var elapsed = now - lastRotate.Value;
        var min = TimeSpan.FromMilliseconds(minRotateMs);
        return elapsed >= min ? TimeSpan.Zero : min - elapsed;
    }
}

/// <summary>
/// Resolves route providers + a user api key into concrete <see cref="ProxyConfig"/>s. Ships a few generic
/// example providers and also loads custom manifests from the <c>route-providers</c> folder. Network calls go
/// through <see cref="IRouteHttp"/> (real HTTP by default; a fake in tests).
/// </summary>
public sealed class RouteProxyService
{
    private readonly IRouteHttp _http;
    private readonly Func<CancellationToken, Task<string?>> _publicIp;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRotate = new();
    private readonly List<RouteProvider> _providers;

    public IReadOnlyList<RouteProvider> Providers => _providers;

    public RouteProxyService(
        IEnumerable<RouteProvider> providers,
        IRouteHttp? http = null,
        Func<CancellationToken, Task<string?>>? publicIpProvider = null)
    {
        _providers = providers.ToList();
        _http = http ?? new RouteHttp();
        _publicIp = publicIpProvider ?? (async ct => (await new ProxyChecker().CheckAsync(null, ct: ct)).Ip);
    }

    /// <summary>Creates a service with the built-ins plus any manifests under <paramref name="providersDir"/>.</summary>
    public static RouteProxyService Create(
        string providersDir,
        IRouteHttp? http = null,
        Func<CancellationToken, Task<string?>>? publicIpProvider = null)
    {
        var all = new List<RouteProvider>(BuiltinProviders);
        foreach (var loaded in LoadFolder(providersDir))
        {
            all.RemoveAll(p => p.Prefix.Equals(loaded.Prefix, StringComparison.OrdinalIgnoreCase));
            all.Add(loaded);
        }
        return new RouteProxyService(all, http, publicIpProvider);
    }

    public RouteProvider? Find(string prefix) =>
        _providers.FirstOrDefault(p => p.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves the current proxy for a provider + api key (does not rotate).</summary>
    public Task<ProxyConfig> ResolveAsync(RouteProvider provider, string apiKey, CancellationToken ct = default) =>
        SendAsync(provider, apiKey, provider.CurrentProxyRequest ?? provider.NewProxyRequest, ct);

    /// <summary>Resolves a raw <c>route:...</c> string to a concrete proxy.</summary>
    public Task<ProxyConfig> ResolveRawAsync(string raw, CancellationToken ct = default)
    {
        if (!RouteProxy.TryParse(raw, out var prefix, out var key))
            throw new FormatException($"Không phải route proxy hợp lệ: '{raw}'");
        var provider = Find(prefix) ?? throw new KeyNotFoundException($"Không tìm thấy provider '{prefix}'");
        return ResolveAsync(provider, key, ct);
    }

    /// <summary>
    /// Rotates to a new IP (respecting <see cref="RouteProvider.MinRotateMs"/>) and returns the new proxy.
    /// Throws <see cref="InvalidOperationException"/> if called again before the throttle elapses.
    /// </summary>
    public async Task<ProxyConfig> ChangeIpAsync(RouteProvider provider, string apiKey, CancellationToken ct = default)
    {
        var slot = $"{provider.Prefix}:{apiKey}";
        _lastRotate.TryGetValue(slot, out var last);
        var remaining = RouteProxy.RemainingCooldown(_lastRotate.ContainsKey(slot) ? last : null, provider.MinRotateMs, DateTimeOffset.UtcNow);
        if (remaining > TimeSpan.Zero)
            throw new InvalidOperationException($"Cần chờ thêm {Math.Ceiling(remaining.TotalSeconds)}s trước khi đổi IP");

        _lastRotate[slot] = DateTimeOffset.UtcNow;
        var cfg = await SendAsync(provider, apiKey, provider.NewProxyRequest ?? provider.CurrentProxyRequest, ct);
        return cfg;
    }

    private async Task<ProxyConfig> SendAsync(RouteProvider provider, string apiKey, RouteRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Url))
            throw new InvalidOperationException($"Provider '{provider.Prefix}' chưa cấu hình request");

        string? publicIp = null;
        var needsIp = (request.Url + request.PostData).Contains("{public_ip}", StringComparison.Ordinal);
        if (needsIp) publicIp = await _publicIp(ct);

        var filled = new RouteRequest
        {
            Url = RouteProxy.Substitute(request.Url, apiKey, publicIp),
            Method = request.Method,
            PostData = RouteProxy.Substitute(request.PostData, apiKey, publicIp),
        };
        var body = await _http.SendAsync(filled, ct);
        return RouteProxy.ParseResponse(provider, body);
    }

    private static IEnumerable<RouteProvider> LoadFolder(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            RouteProvider? p = null;
            try { p = JsonSerializer.Deserialize<RouteProvider>(File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or JsonException) { /* skip malformed manifests */ }
            if (p is not null && !string.IsNullOrWhiteSpace(p.Prefix) && !string.IsNullOrWhiteSpace(p.HostPath))
                yield return p;
        }
    }

    /// <summary>
    /// A small set of generic example providers. These are templates showing the manifest shape; real vendors are
    /// added by dropping a JSON manifest into the <c>route-providers</c> folder (see <see cref="Create"/>).
    /// </summary>
    public static readonly IReadOnlyList<RouteProvider> BuiltinProviders =
    [
        // Flat response: {"status":"ok","host":"1.2.3.4","port":8080,"username":"u","password":"p"}
        new RouteProvider
        {
            Prefix = "demo-flat",
            Name = "Demo (flat JSON)",
            CurrentProxyRequest = new RouteRequest { Url = "https://api.example.com/current?key={api_key}" },
            NewProxyRequest = new RouteRequest { Url = "https://api.example.com/rotate?key={api_key}" },
            DefaultType = "http",
            TypePath = "type",
            HostPath = "host",
            PortPath = "port",
            UserPath = "username",
            PassPath = "password",
            MessagePath = "message",
            MinRotateMs = 60_000,
        },
        // Nested response: {"data":{"proxy":{"ip":"1.2.3.4","port":1080,"user":"u","pass":"p"}}}
        new RouteProvider
        {
            Prefix = "demo-nested",
            Name = "Demo (nested data)",
            CurrentProxyRequest = new RouteRequest { Url = "https://proxy.example.net/api/get?token={api_key}" },
            NewProxyRequest = new RouteRequest { Url = "https://proxy.example.net/api/new?token={api_key}" },
            DefaultType = "socks5",
            TypePath = "data.proxy.protocol",
            HostPath = "data.proxy.ip",
            PortPath = "data.proxy.port",
            UserPath = "data.proxy.user",
            PassPath = "data.proxy.pass",
            MessagePath = "error",
            MinRotateMs = 120_000,
        },
        // Split key + whitelist flow: key is "id:secret"; needs this machine's public IP for whitelisting.
        new RouteProvider
        {
            Prefix = "demo-whitelist",
            Name = "Demo (split key + IP whitelist)",
            CurrentProxyRequest = new RouteRequest { Url = "https://api.example.org/v1/current?id={api_key[0]}&secret={api_key[1]}&ip={public_ip}" },
            NewProxyRequest = new RouteRequest
            {
                Url = "https://api.example.org/v1/rotate",
                Method = "POST",
                PostData = "{\"id\":\"{api_key[0]}\",\"secret\":\"{api_key[1]}\",\"whitelist_ip\":\"{public_ip}\"}",
            },
            DefaultType = "http",
            HostPath = "result.server",
            PortPath = "result.port",
            MessagePath = "msg",
            MinRotateMs = 90_000,
        },
    ];
}
