using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpmLite.Core.Fingerprint;

public enum TargetOs { Windows, MacOs, Linux }

/// <summary>Real = leave untouched, Noise = deterministic per-profile noise, Off = block/blank.</summary>
public enum NoiseMode { Real, Noise, Off }

/// <summary>Real = expose real IPs, Altered = replace candidates with the proxy's public IP, Disabled = no non-proxied UDP.</summary>
public enum WebRtcMode { Real, Altered, Disabled }

/// <summary>Auto = derive from proxy IP lookup, Custom = use configured value, Real = host value.</summary>
public enum AutoMode { Auto, Custom, Real }

/// <summary>
/// Everything a profile presents to websites. Serialized as JSON into the profiles table.
/// All noise is derived from <see cref="Seed"/> so a profile's fingerprint is stable across launches.
/// </summary>
public sealed class FingerprintConfig
{
    public int Version { get; set; } = 1;
    public TargetOs Os { get; set; } = TargetOs.Windows;

    /// <summary>Per-profile seed; every noise source is derived from it.</summary>
    public uint Seed { get; set; }

    // Navigator / UA
    public string UserAgent { get; set; } = "";
    /// <summary>Chrome major version used in UA + client hints; must match the actual browser core.</summary>
    public int BrowserMajor { get; set; }
    public string BrowserFullVersion { get; set; } = "";
    public string Platform { get; set; } = "Win32";
    public string UaPlatform { get; set; } = "Windows";
    public string UaPlatformVersion { get; set; } = "15.0.0";
    public string UaArch { get; set; } = "x86";
    public int HardwareConcurrency { get; set; } = 8;
    public int DeviceMemory { get; set; } = 8;

    // Locale
    public AutoMode LanguageMode { get; set; } = AutoMode.Auto;
    public string Languages { get; set; } = "en-US,en";
    public AutoMode TimezoneMode { get; set; } = AutoMode.Auto;
    public string Timezone { get; set; } = "";
    public AutoMode GeoMode { get; set; } = AutoMode.Auto;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double GeoAccuracy { get; set; } = 50;

    // Screen
    public int ScreenWidth { get; set; } = 1920;
    public int ScreenHeight { get; set; } = 1080;
    public int AvailWidth { get; set; } = 1920;
    public int AvailHeight { get; set; } = 1040;
    public int ColorDepth { get; set; } = 24;
    public double DevicePixelRatio { get; set; } = 1;

    // Graphics / hardware surfaces
    public NoiseMode Canvas { get; set; } = NoiseMode.Noise;
    public NoiseMode WebGlImage { get; set; } = NoiseMode.Noise;
    public bool WebGlMetadataCustom { get; set; } = true;
    public string WebGlVendor { get; set; } = "Google Inc. (NVIDIA)";
    public string WebGlRenderer { get; set; } = "";
    public NoiseMode Audio { get; set; } = NoiseMode.Noise;
    public NoiseMode ClientRects { get; set; } = NoiseMode.Noise;

    // Network
    public WebRtcMode WebRtc { get; set; } = WebRtcMode.Altered;
    /// <summary>Public IP used for <see cref="WebRtcMode.Altered"/>; filled from proxy check when empty.</summary>
    public string WebRtcPublicIp { get; set; } = "";

    // Media devices exposed by enumerateDevices()
    public int AudioInputs { get; set; } = 1;
    public int AudioOutputs { get; set; } = 1;
    public int VideoInputs { get; set; } = 1;

    // Window
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 800;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static FingerprintConfig FromJson(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new FingerprintConfig()
            : JsonSerializer.Deserialize<FingerprintConfig>(json, JsonOpts) ?? new FingerprintConfig();

    public FingerprintConfig Clone() => FromJson(ToJson());
}
