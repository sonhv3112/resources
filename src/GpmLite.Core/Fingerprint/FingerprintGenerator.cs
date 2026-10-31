using System.Runtime.InteropServices;

namespace GpmLite.Core.Fingerprint;

/// <summary>
/// Builds internally-consistent fingerprints: OS, UA, client hints, platform, GPU, screen and DPR are
/// always picked from the same preset family, so no surface contradicts another.
/// </summary>
public static class FingerprintGenerator
{
    private sealed record Gpu(string Vendor, string Renderer);
    private sealed record Screen(int W, int H, double Dpr);

    private static readonly Gpu[] WindowsGpus =
    [
        new("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 (0x00002504) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        new("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1660 SUPER (0x000021C4) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        new("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 4060 (0x00002882) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        new("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce GTX 1050 Ti (0x00001C8C) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        new("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 630 (0x00003E92) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        new("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics (0x00009A49) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        new("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon RX 6600 (0x000073FF) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
        new("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon(TM) Graphics (0x00001638) Direct3D11 vs_5_0 ps_5_0, D3D11)"),
    ];

    private static readonly Gpu[] MacGpus =
    [
        new("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M1, Unspecified Version)"),
        new("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M1 Pro, Unspecified Version)"),
        new("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M2, Unspecified Version)"),
        new("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M3, Unspecified Version)"),
        new("Google Inc. (Apple)", "ANGLE (Apple, ANGLE Metal Renderer: Apple M3 Pro, Unspecified Version)"),
    ];

    private static readonly Gpu[] LinuxGpus =
    [
        new("Google Inc. (Intel)", "ANGLE (Intel, Mesa Intel(R) UHD Graphics 620 (KBL GT2), OpenGL 4.6)"),
        new("Google Inc. (Intel)", "ANGLE (Intel, Mesa Intel(R) Xe Graphics (TGL GT2), OpenGL 4.6)"),
        new("Google Inc. (AMD)", "ANGLE (AMD, AMD Radeon RX 580 Series (radeonsi, polaris10, LLVM 15.0.7, DRM 3.49, 6.1.0), OpenGL 4.6)"),
    ];

    private static readonly Screen[] WindowsScreens =
    [
        new(1920, 1080, 1), new(1920, 1080, 1.25), new(1366, 768, 1), new(1536, 864, 1.25),
        new(2560, 1440, 1), new(1600, 900, 1), new(1440, 900, 1), new(1280, 720, 1.5),
    ];

    private static readonly Screen[] MacScreens =
    [
        new(1440, 900, 2), new(1512, 982, 2), new(1470, 956, 2), new(1728, 1117, 2), new(1680, 1050, 2), new(1920, 1080, 2),
    ];

    private static readonly Screen[] LinuxScreens = [new(1920, 1080, 1), new(1366, 768, 1), new(2560, 1440, 1)];

    private static readonly int[] WinCores = [4, 6, 8, 8, 12, 16];
    private static readonly int[] MacCores = [8, 8, 10, 12];
    private static readonly int[] Memories = [4, 8, 8, 8];

    public static TargetOs HostOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? TargetOs.MacOs
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TargetOs.Windows
        : TargetOs.Linux;

    /// <param name="browserFullVersion">Version of the Chrome core that will run this profile, e.g. "156.0.8078.76".</param>
    /// <param name="os">Target OS; defaults to the host OS (changing it is detectable by font/rendering checks).</param>
    /// <param name="seed">Fixed seed for reproducible output; random when null.</param>
    public static FingerprintConfig Generate(string browserFullVersion, TargetOs? os = null, uint? seed = null)
    {
        var s = seed ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var rng = new Random(unchecked((int)s));
        var target = os ?? HostOs;
        var major = ParseMajor(browserFullVersion);

        var fp = new FingerprintConfig
        {
            Os = target,
            Seed = s,
            BrowserMajor = major,
            BrowserFullVersion = browserFullVersion,
            DeviceMemory = Pick(rng, Memories),
            AudioInputs = 1,
            AudioOutputs = rng.Next(1, 3),
            VideoInputs = rng.Next(0, 2),
        };

        Gpu gpu;
        Screen screen;
        switch (target)
        {
            case TargetOs.MacOs:
                gpu = Pick(rng, MacGpus);
                screen = Pick(rng, MacScreens);
                fp.Platform = "MacIntel";
                fp.UaPlatform = "macOS";
                fp.UaPlatformVersion = Pick(rng, new[] { "14.6.1", "15.3.0", "15.5.0", "26.0.0" });
                fp.UaArch = "arm";
                fp.HardwareConcurrency = Pick(rng, MacCores);
                fp.UserAgent = $"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
                fp.AvailWidth = screen.W;
                fp.AvailHeight = screen.H - 25 - rng.Next(0, 2) * 70; // menu bar, optionally the dock
                fp.ColorDepth = 30;
                break;
            case TargetOs.Linux:
                gpu = Pick(rng, LinuxGpus);
                screen = Pick(rng, LinuxScreens);
                fp.Platform = "Linux x86_64";
                fp.UaPlatform = "Linux";
                fp.UaPlatformVersion = "6.8.0";
                fp.UaArch = "x86";
                fp.HardwareConcurrency = Pick(rng, WinCores);
                fp.UserAgent = $"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
                fp.AvailWidth = screen.W;
                fp.AvailHeight = screen.H - 27;
                fp.ColorDepth = 24;
                break;
            default:
                gpu = Pick(rng, WindowsGpus);
                screen = Pick(rng, WindowsScreens);
                fp.Platform = "Win32";
                fp.UaPlatform = "Windows";
                fp.UaPlatformVersion = Pick(rng, new[] { "10.0.0", "15.0.0", "19.0.0" }); // Win10, Win11 22H2/24H2
                fp.UaArch = "x86";
                fp.HardwareConcurrency = Pick(rng, WinCores);
                fp.UserAgent = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36";
                fp.AvailWidth = screen.W;
                fp.AvailHeight = screen.H - 40; // taskbar
                fp.ColorDepth = 24;
                break;
        }

        fp.WebGlVendor = gpu.Vendor;
        fp.WebGlRenderer = gpu.Renderer;
        fp.ScreenWidth = screen.W;
        fp.ScreenHeight = screen.H;
        fp.DevicePixelRatio = screen.Dpr;
        fp.WindowWidth = Math.Min(1280 + rng.Next(0, 5) * 40, fp.AvailWidth);
        fp.WindowHeight = Math.Min(760 + rng.Next(0, 4) * 30, fp.AvailHeight);
        return fp;
    }

    /// <summary>Re-randomizes the fingerprint while keeping the user's mode choices (Real/Noise/Auto...) and OS.</summary>
    public static FingerprintConfig Regenerate(FingerprintConfig current, string browserFullVersion)
    {
        var next = Generate(browserFullVersion, current.Os);
        next.Canvas = current.Canvas;
        next.WebGlImage = current.WebGlImage;
        next.WebGlMetadataCustom = current.WebGlMetadataCustom;
        next.Audio = current.Audio;
        next.ClientRects = current.ClientRects;
        next.WebRtc = current.WebRtc;
        next.LanguageMode = current.LanguageMode;
        next.Languages = current.Languages;
        next.TimezoneMode = current.TimezoneMode;
        next.Timezone = current.Timezone;
        next.GeoMode = current.GeoMode;
        next.Latitude = current.Latitude;
        next.Longitude = current.Longitude;
        return next;
    }

    internal static int ParseMajor(string fullVersion)
    {
        var head = fullVersion.Split('.')[0];
        return int.TryParse(head, out var m) && m > 0
            ? m
            : throw new ArgumentException($"Invalid browser version '{fullVersion}'", nameof(fullVersion));
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];
}
