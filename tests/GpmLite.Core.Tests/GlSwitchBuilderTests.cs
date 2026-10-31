using GpmLite.Core.Browser;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Tests;

public class GlSwitchBuilderTests
{
    // A fully-specified Windows profile (no Auto modes) so the switch list is deterministic.
    private static FingerprintConfig WindowsSample() => new()
    {
        Seed = 123456,
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36",
        Platform = "Win32",
        HardwareConcurrency = 8,
        DeviceMemory = 8,
        ScreenWidth = 1920,
        ScreenHeight = 1080,
        AvailWidth = 1920,
        AvailHeight = 1040,
        ColorDepth = 24,
        WebGlMetadataCustom = true,
        WebGlVendor = "Google Inc. (NVIDIA)",
        WebGlRenderer = "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
        Canvas = NoiseMode.Noise,
        WebGlImage = NoiseMode.Noise,
        Audio = NoiseMode.Noise,
        ClientRects = NoiseMode.Noise,
        WebRtc = WebRtcMode.Altered,
        LanguageMode = AutoMode.Custom,
        Languages = "en-US,en",
        TimezoneMode = AutoMode.Custom,
        Timezone = "Asia/Ho_Chi_Minh",
        GeoMode = AutoMode.Real,
    };

    [Fact]
    public void Windows_profile_emits_exact_switch_list()
    {
        var fp = WindowsSample();
        var plan = SpoofPlan.Resolve(fp, null);
        var gl = GlSwitchBuilder.Build(plan);

        string[] expected =
        [
            "--disable-features=MacAppCodeSignClone",
            "--gl-enabled",
            "--gl-seed=123456",
            "--gl-platform=Win32",
            "--gl-hardware-concurrency=8",
            "--gl-device-memory=8",
            "--gl-screen=1920x1080",
            "--gl-avail=1920x1040",
            "--gl-color-depth=24",
            "--gl-webgl-vendor=Google Inc. (NVIDIA)",
            "--gl-webgl-renderer=ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
            "--gl-canvas-noise",
            "--gl-webgl-noise",
            "--gl-audio-noise",
            "--gl-rects-noise",
            "--gl-webrtc=proxy",
            "--gl-vendor=Google Inc.",
            $"--user-agent={fp.UserAgent}",
            "--lang=en-US",
            "--accept-lang=en-US,en",
        ];
        Assert.Equal(expected, gl.Args);
        Assert.Equal("Asia/Ho_Chi_Minh", gl.Timezone);
    }

    [Fact]
    public void Webgl_metadata_only_when_custom()
    {
        var fp = WindowsSample();
        fp.WebGlMetadataCustom = false;
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.DoesNotContain(gl.Args, a => a.StartsWith("--gl-webgl-vendor", StringComparison.Ordinal));
        Assert.DoesNotContain(gl.Args, a => a.StartsWith("--gl-webgl-renderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Noise_flags_present_only_per_mode()
    {
        var fp = WindowsSample();
        fp.Canvas = NoiseMode.Real;      // off
        fp.WebGlImage = NoiseMode.Off;   // off
        fp.Audio = NoiseMode.Noise;      // on
        fp.ClientRects = NoiseMode.Noise; // on
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));

        Assert.DoesNotContain("--gl-canvas-noise", gl.Args);
        Assert.DoesNotContain("--gl-webgl-noise", gl.Args);
        Assert.Contains("--gl-audio-noise", gl.Args);
        Assert.Contains("--gl-rects-noise", gl.Args);
    }

    [Theory]
    [InlineData(WebRtcMode.Real, "real")]
    [InlineData(WebRtcMode.Altered, "proxy")]
    [InlineData(WebRtcMode.Disabled, "disable")]
    public void Webrtc_mode_is_mapped(WebRtcMode mode, string expected)
    {
        var fp = WindowsSample();
        fp.WebRtc = mode;
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.Contains($"--gl-webrtc={expected}", gl.Args);
    }

    [Fact]
    public void Platform_switch_reflects_target_os()
    {
        var fp = WindowsSample();
        fp.Platform = "MacIntel";
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.Contains("--gl-platform=MacIntel", gl.Args);
    }

    [Fact]
    public void Auto_timezone_and_language_come_from_ip_info()
    {
        var fp = WindowsSample();
        fp.TimezoneMode = AutoMode.Auto;
        fp.LanguageMode = AutoMode.Auto;
        var ip = new IpInfo("5.6.7.8", "DE", "Germany", "BE", "Berlin", 52.5, 13.4, "Europe/Berlin", 10);
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, ip));

        Assert.Equal("Europe/Berlin", gl.Timezone);
        Assert.Contains("--lang=de-DE", gl.Args);
        Assert.Contains(gl.Args, a => a.StartsWith("--accept-lang=de-DE", StringComparison.Ordinal));
        Assert.Contains($"--user-agent={fp.UserAgent}", gl.Args);
    }
}
