using System.Reflection;
using System.Text.Json;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Browser;

/// <summary>
/// The fully-resolved overrides for one launch: Auto modes are replaced with values from the proxy's
/// IP lookup (or dropped to Real when no lookup is available).
/// </summary>
public sealed record SpoofPlan(
    FingerprintConfig Fp,
    string? Timezone,
    string? Languages,
    (double Lat, double Lon, double Accuracy)? Geo,
    string? WebRtcPublicIp)
{
    public string? PrimaryLocale => Languages?.Split(',')[0].Trim();

    public static SpoofPlan Resolve(FingerprintConfig fp, IpInfo? ip)
    {
        string? tz = fp.TimezoneMode switch
        {
            AutoMode.Custom when !string.IsNullOrWhiteSpace(fp.Timezone) => fp.Timezone,
            AutoMode.Auto when !string.IsNullOrWhiteSpace(ip?.Timezone) => ip!.Timezone,
            _ => null,
        };
        string? langs = fp.LanguageMode switch
        {
            AutoMode.Custom when !string.IsNullOrWhiteSpace(fp.Languages) => fp.Languages,
            AutoMode.Auto when ip is not null => CountryLanguages.ForCountry(ip.CountryCode),
            _ => null,
        };
        (double, double, double)? geo = fp.GeoMode switch
        {
            AutoMode.Custom => (fp.Latitude, fp.Longitude, fp.GeoAccuracy),
            AutoMode.Auto when ip is not null => (ip.Latitude, ip.Longitude, fp.GeoAccuracy),
            _ => null,
        };
        var rtcIp = !string.IsNullOrWhiteSpace(fp.WebRtcPublicIp) ? fp.WebRtcPublicIp : ip?.Ip;
        return new SpoofPlan(fp, tz, langs, geo, string.IsNullOrWhiteSpace(rtcIp) ? null : rtcIp);
    }

    private static readonly Lazy<string> Template = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GpmLite.spoof.js")
                      ?? throw new InvalidOperationException("spoof.js resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    /// <summary>The in-page script with this plan's config baked in.</summary>
    public string BuildScript()
    {
        var cfg = new
        {
            seed = Fp.Seed,
            hardwareConcurrency = Fp.HardwareConcurrency,
            deviceMemory = Fp.DeviceMemory,
            screen = new { availWidth = Fp.AvailWidth, availHeight = Fp.AvailHeight, colorDepth = Fp.ColorDepth },
            canvas = Fp.Canvas.ToString(),
            webglImage = Fp.WebGlImage.ToString(),
            webglMeta = Fp.WebGlMetadataCustom,
            webglVendor = Fp.WebGlVendor,
            webglRenderer = Fp.WebGlRenderer,
            audio = Fp.Audio.ToString(),
            clientRects = Fp.ClientRects.ToString(),
            media = new { audioInputs = Fp.AudioInputs, audioOutputs = Fp.AudioOutputs, videoInputs = Fp.VideoInputs },
            webrtc = Fp.WebRtc.ToString(),
            webrtcIp = WebRtcPublicIp,
        };
        // JSON is a valid JS expression; escape "</" defensively in case of exotic values.
        var json = JsonSerializer.Serialize(cfg).Replace("</", "<\\/");
        return Template.Value.Replace("__CFG__", json);
    }

    /// <summary>Brand lists read from the real browser core (GREASE brand differs per Chrome version).</summary>
    public System.Text.Json.Nodes.JsonNode? RealBrands { get; set; }
    public System.Text.Json.Nodes.JsonNode? RealFullVersionList { get; set; }

    /// <summary>Payload for Emulation.setUserAgentOverride, including UA client hints.</summary>
    public object UserAgentOverride()
    {
        var major = Fp.BrowserMajor.ToString();
        var full = string.IsNullOrWhiteSpace(Fp.BrowserFullVersion) ? $"{major}.0.0.0" : Fp.BrowserFullVersion;
        // Chrome's GREASE brand rotates per version; this matches the shape Chrome itself sends.
        var brands = new[]
        {
            new { brand = "Not)A;Brand", version = "8" },
            new { brand = "Chromium", version = major },
            new { brand = "Google Chrome", version = major },
        };
        var fullList = new[]
        {
            new { brand = "Not)A;Brand", version = "8.0.0.0" },
            new { brand = "Chromium", version = full },
            new { brand = "Google Chrome", version = full },
        };
        return new
        {
            userAgent = Fp.UserAgent,
            acceptLanguage = Languages,
            platform = Fp.Platform,
            userAgentMetadata = new
            {
                brands = (object?)RealBrands?.DeepClone() ?? brands,
                fullVersionList = (object?)RealFullVersionList?.DeepClone() ?? fullList,
                fullVersion = full,
                platform = Fp.UaPlatform,
                platformVersion = Fp.UaPlatformVersion,
                architecture = Fp.UaArch,
                model = "",
                mobile = false,
                bitness = "64",
                wow64 = false,
            },
        };
    }
}
