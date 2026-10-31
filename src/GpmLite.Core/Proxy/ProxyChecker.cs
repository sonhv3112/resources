using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace GpmLite.Core.Proxy;

public sealed record IpInfo(
    string Ip, string CountryCode, string Country, string Region, string City,
    double Latitude, double Longitude, string Timezone, long LatencyMs)
{
    public string Summary => $"{CountryCode} · {Ip}";
}

/// <summary>
/// An IP-checker server: a lookup URL plus the dotted JSON paths that locate each field in its response.
/// Lets users pick between several services that all return different JSON shapes.
/// </summary>
public sealed record CheckerServer(
    string Id,
    string Name,
    string Url,
    string IpPath,
    string CountryCodePath,
    string CountryPath,
    string RegionPath,
    string CityPath,
    string LatPath,
    string LonPath,
    string TimezonePath,
    string? StatusPath = null,
    string? StatusOkValue = null);

/// <summary>
/// Looks up the public IP/geo seen through a proxy. The lookup goes through the same <see cref="ProxyRelay"/>
/// path the browser uses, so a successful check proves the browser will connect too. The response is parsed
/// generically via the selected <see cref="CheckerServer"/>'s dotted paths, so several services are supported.
/// </summary>
public sealed class ProxyChecker
{
    public const string DefaultLookupUrl =
        "http://ip-api.com/json/?fields=status,message,query,country,countryCode,regionName,city,lat,lon,timezone";

    /// <summary>Built-in checker services the user can pick from in Settings.</summary>
    public static readonly IReadOnlyList<CheckerServer> BuiltinServers =
    [
        new CheckerServer("ip-api", "ip-api.com", DefaultLookupUrl,
            IpPath: "query", CountryCodePath: "countryCode", CountryPath: "country", RegionPath: "regionName",
            CityPath: "city", LatPath: "lat", LonPath: "lon", TimezonePath: "timezone",
            StatusPath: "status", StatusOkValue: "success"),
        new CheckerServer("ipwho", "ipwho.is", "https://ipwho.is/",
            IpPath: "ip", CountryCodePath: "country_code", CountryPath: "country", RegionPath: "region",
            CityPath: "city", LatPath: "latitude", LonPath: "longitude", TimezonePath: "timezone.id",
            StatusPath: "success", StatusOkValue: "true"),
        new CheckerServer("ipapi-co", "ipapi.co", "https://ipapi.co/json/",
            IpPath: "ip", CountryCodePath: "country_code", CountryPath: "country_name", RegionPath: "region",
            CityPath: "city", LatPath: "latitude", LonPath: "longitude", TimezonePath: "timezone"),
    ];

    private readonly CheckerServer _server;

    public ProxyChecker(CheckerServer server) => _server = server;

    /// <summary>
    /// Backwards-compatible: resolves a lookup URL to a known built-in server (so its JSON paths are used), or
    /// falls back to the ip-api field layout for an unknown URL.
    /// </summary>
    public ProxyChecker(string lookupUrl = DefaultLookupUrl) => _server = ResolveServer(lookupUrl);

    /// <summary>Maps a URL to its built-in server config, or an ip-api-shaped server with that URL.</summary>
    public static CheckerServer ResolveServer(string? lookupUrl)
    {
        if (string.IsNullOrWhiteSpace(lookupUrl)) return BuiltinServers[0];
        var match = BuiltinServers.FirstOrDefault(s => s.Url.Equals(lookupUrl, StringComparison.OrdinalIgnoreCase));
        return match ?? BuiltinServers[0] with { Id = "custom", Name = "Tuỳ chỉnh", Url = lookupUrl };
    }

    public async Task<IpInfo> CheckAsync(ProxyConfig? proxy, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await using var relay = proxy is null ? null : new ProxyRelay(proxy);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = relay is not null,
            Proxy = relay is null ? null : new WebProxy($"http://{relay.Address}"),
            ConnectTimeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        using var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };

        var sw = Stopwatch.StartNew();
        using var resp = await http.GetAsync(_server.Url, ct);
        if (resp.StatusCode == HttpStatusCode.BadGateway)
            throw new IOException("Proxy refused the connection or rejected credentials");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        return Parse(_server, body, sw.ElapsedMilliseconds);
    }

    /// <summary>Parses a lookup response body with a server's dotted paths. Exposed for testing.</summary>
    public static IpInfo Parse(CheckerServer server, string body, long latencyMs)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException e) { throw new IOException($"IP lookup returned invalid JSON: {e.Message}"); }

        if (!string.IsNullOrEmpty(server.StatusPath))
        {
            var status = RouteProxy.ReadPath(root, server.StatusPath);
            if (!string.Equals(status, server.StatusOkValue, StringComparison.OrdinalIgnoreCase))
            {
                var msg = RouteProxy.ReadPath(root, "message") ?? RouteProxy.ReadPath(root, "error");
                throw new IOException($"IP lookup failed: {msg ?? status ?? "unknown error"}");
            }
        }

        var ip = RouteProxy.ReadPath(root, server.IpPath);
        if (string.IsNullOrWhiteSpace(ip))
            throw new IOException("IP lookup response had no IP address");

        return new IpInfo(
            ip,
            RouteProxy.ReadPath(root, server.CountryCodePath) ?? "",
            RouteProxy.ReadPath(root, server.CountryPath) ?? "",
            RouteProxy.ReadPath(root, server.RegionPath) ?? "",
            RouteProxy.ReadPath(root, server.CityPath) ?? "",
            ParseDouble(RouteProxy.ReadPath(root, server.LatPath)),
            ParseDouble(RouteProxy.ReadPath(root, server.LonPath)),
            RouteProxy.ReadPath(root, server.TimezonePath) ?? "",
            latencyMs);
    }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>Maps a country code to a plausible Accept-Language list (used for LanguageMode.Auto).</summary>
public static class CountryLanguages
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VN"] = "vi-VN,vi,en-US,en", ["US"] = "en-US,en", ["GB"] = "en-GB,en", ["CA"] = "en-CA,en,fr-CA",
        ["AU"] = "en-AU,en", ["DE"] = "de-DE,de,en-US,en", ["FR"] = "fr-FR,fr,en-US,en", ["ES"] = "es-ES,es,en",
        ["IT"] = "it-IT,it,en", ["NL"] = "nl-NL,nl,en", ["BR"] = "pt-BR,pt,en", ["PT"] = "pt-PT,pt,en",
        ["JP"] = "ja-JP,ja,en-US,en", ["KR"] = "ko-KR,ko,en-US,en", ["CN"] = "zh-CN,zh,en", ["TW"] = "zh-TW,zh,en",
        ["HK"] = "zh-HK,zh,en", ["TH"] = "th-TH,th,en", ["ID"] = "id-ID,id,en", ["MY"] = "ms-MY,ms,en",
        ["PH"] = "en-PH,en,fil", ["SG"] = "en-SG,en,zh", ["IN"] = "en-IN,en,hi", ["RU"] = "ru-RU,ru,en",
        ["UA"] = "uk-UA,uk,ru,en", ["PL"] = "pl-PL,pl,en", ["TR"] = "tr-TR,tr,en", ["MX"] = "es-MX,es,en",
    };

    public static string ForCountry(string? countryCode) =>
        countryCode is not null && Map.TryGetValue(countryCode, out var langs) ? langs : "en-US,en";
}
