using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpmLite.Core.Proxy;

public sealed record IpInfo(
    string Ip, string CountryCode, string Country, string Region, string City,
    double Latitude, double Longitude, string Timezone, long LatencyMs)
{
    public string Summary => $"{CountryCode} · {Ip}";
}

/// <summary>
/// Looks up the public IP/geo seen through a proxy. The lookup goes through the same <see cref="ProxyRelay"/>
/// path the browser uses, so a successful check proves the browser will connect too.
/// </summary>
public sealed class ProxyChecker(string lookupUrl = ProxyChecker.DefaultLookupUrl)
{
    public const string DefaultLookupUrl =
        "http://ip-api.com/json/?fields=status,message,query,country,countryCode,regionName,city,lat,lon,timezone";

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
        using var resp = await http.GetAsync(lookupUrl, ct);
        if (resp.StatusCode == HttpStatusCode.BadGateway)
            throw new IOException("Proxy refused the connection or rejected credentials");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        var r = JsonSerializer.Deserialize<IpApiResponse>(body) ?? throw new IOException("Empty IP lookup response");
        if (!string.Equals(r.Status, "success", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"IP lookup failed: {r.Message ?? "unknown error"}");

        return new IpInfo(r.Query ?? "", r.CountryCode ?? "", r.Country ?? "", r.RegionName ?? "", r.City ?? "",
            r.Lat, r.Lon, r.Timezone ?? "", sw.ElapsedMilliseconds);
    }

    private sealed class IpApiResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("query")] public string? Query { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
        [JsonPropertyName("regionName")] public string? RegionName { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("lat")] public double Lat { get; set; }
        [JsonPropertyName("lon")] public double Lon { get; set; }
        [JsonPropertyName("timezone")] public string? Timezone { get; set; }
    }
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
