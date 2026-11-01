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
        BrowserMajor = 156,
        BrowserFullVersion = "156.0.8078.76",
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
            "--gl-dpr=1",
            "--gl-webgl-vendor=Google Inc. (NVIDIA)",
            "--gl-webgl-renderer=ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
            $"--gl-webgl-params={WebGlPresets.BuildParamsBase64(fp.WebGlRenderer)}",
            "--gl-canvas-noise",
            "--gl-canvas-rate=20",
            "--gl-webgl-noise",
            "--gl-webgl-rate=64",
            "--gl-audio-noise",
            "--gl-audio-amp=0.00001",
            "--gl-rects-noise",
            "--gl-rects-amp=0.01",
            "--gl-webrtc=proxy",
            "--gl-vendor=Google Inc.",
            "--gl-media-audioinputs=1",
            "--gl-media-audiooutputs=1",
            "--gl-media-videoinputs=1",
            $"--gl-voices={VoicePresets.BuildVoicesBase64(fp.Os)}",
            "--gl-ua-platform=Windows",
            "--gl-ua-platform-version=15.0.0",
            "--gl-ua-arch=x86",
            "--gl-ua-bitness=64",
            "--gl-ua-model=",
            "--gl-ua-full-version=156.0.8078.76",
            "--gl-ua-mobile=0",
            $"--user-agent={fp.UserAgent}",
            "--gl-languages=en-US,en",
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
    public void Voices_switch_always_emitted_and_matches_os()
    {
        var fp = WindowsSample();
        var args = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args;
        Assert.Contains($"--gl-voices={VoicePresets.BuildVoicesBase64(TargetOs.Windows)}", args);

        fp.Os = TargetOs.MacOs;
        var macArgs = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args;
        Assert.Contains($"--gl-voices={VoicePresets.BuildVoicesBase64(TargetOs.MacOs)}", macArgs);
        // The voice payload differs between Windows and macOS.
        Assert.DoesNotContain($"--gl-voices={VoicePresets.BuildVoicesBase64(TargetOs.Windows)}", macArgs);
    }

    [Fact]
    public void Webrtc_ip_emitted_only_when_altered_and_ip_known()
    {
        // Altered + resolved public IP → switch present.
        var fp = WindowsSample(); // WebRtc = Altered
        fp.WebRtcPublicIp = "203.0.113.7";
        var args = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args;
        Assert.Contains("--gl-webrtc-ip=203.0.113.7", args);

        // Altered but no IP → absent.
        fp.WebRtcPublicIp = "";
        args = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args;
        Assert.DoesNotContain(args, a => a.StartsWith("--gl-webrtc-ip", StringComparison.Ordinal));

        // IP comes from the proxy lookup (Auto) → present.
        var ip = new IpInfo("198.51.100.9", "VN", "Vietnam", "HCM", "HCMC", 10.8, 106.6, "Asia/Ho_Chi_Minh", 10);
        args = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, ip)).Args;
        Assert.Contains("--gl-webrtc-ip=198.51.100.9", args);

        // Not Altered → absent even with an IP.
        fp.WebRtc = WebRtcMode.Real;
        args = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, ip)).Args;
        Assert.DoesNotContain(args, a => a.StartsWith("--gl-webrtc-ip", StringComparison.Ordinal));
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
    public void Fonts_switch_only_when_non_empty()
    {
        var fp = WindowsSample();
        // default sample has no font whitelist
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.DoesNotContain(gl.Args, a => a.StartsWith("--gl-fonts", StringComparison.Ordinal));

        fp.Fonts = "Arial,Segoe UI,Tahoma";
        gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.Contains("--gl-fonts=Arial,Segoe UI,Tahoma", gl.Args);
    }

    [Fact]
    public void Vendor_switch_reflects_fp_vendor_and_falls_back_when_blank()
    {
        var fp = WindowsSample();
        fp.Vendor = "Apple Computer, Inc.";
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.Contains("--gl-vendor=Apple Computer, Inc.", gl.Args);

        fp.Vendor = "";
        gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.Contains("--gl-vendor=Google Inc.", gl.Args);
    }

    [Fact]
    public void Media_device_counts_are_emitted()
    {
        var fp = WindowsSample();
        fp.AudioInputs = 2;
        fp.AudioOutputs = 3;
        fp.VideoInputs = 0;
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.Contains("--gl-media-audioinputs=2", gl.Args);
        Assert.Contains("--gl-media-audiooutputs=3", gl.Args);
        Assert.Contains("--gl-media-videoinputs=0", gl.Args);
    }

    [Fact]
    public void Geo_switches_present_when_plan_geo_set()
    {
        var fp = WindowsSample();
        fp.GeoMode = AutoMode.Custom;
        fp.Latitude = 10.5;
        fp.Longitude = 106.75;
        fp.GeoAccuracy = 35;
        var plan = SpoofPlan.Resolve(fp, null);
        Assert.NotNull(plan.Geo);
        var gl = GlSwitchBuilder.Build(plan);
        Assert.Contains("--gl-geo-lat=10.5", gl.Args);
        Assert.Contains("--gl-geo-lon=106.75", gl.Args);
        Assert.Contains("--gl-geo-accuracy=35", gl.Args);
    }

    [Fact]
    public void Geo_switches_absent_when_geo_real()
    {
        var fp = WindowsSample(); // GeoMode = Real
        var gl = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null));
        Assert.DoesNotContain(gl.Args, a => a.StartsWith("--gl-geo-", StringComparison.Ordinal));
    }

    [Fact]
    public void Webgl_params_switch_only_when_custom()
    {
        var fp = WindowsSample();
        Assert.Contains(GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args,
            a => a.StartsWith("--gl-webgl-params=", StringComparison.Ordinal));

        fp.WebGlMetadataCustom = false;
        Assert.DoesNotContain(GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args,
            a => a.StartsWith("--gl-webgl-params=", StringComparison.Ordinal));
    }

    [Fact]
    public void Noise_amps_paired_with_their_noise_flag()
    {
        var fp = WindowsSample();
        fp.CanvasRate = 33;
        fp.WebglRate = 48;
        fp.AudioAmp = 2e-5;
        fp.RectsAmp = 0.02;
        fp.Audio = NoiseMode.Real; // audio off: no amp either
        var args = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args;

        Assert.Contains("--gl-canvas-rate=33", args);
        Assert.Contains("--gl-webgl-rate=48", args);
        Assert.Contains("--gl-rects-amp=0.02", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--gl-audio-amp", StringComparison.Ordinal));
    }

    [Fact]
    public void Ua_client_hint_switches_reflect_fingerprint()
    {
        var fp = WindowsSample();
        fp.UaMobile = true;
        var args = GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args;
        Assert.Contains("--gl-ua-platform=Windows", args);
        Assert.Contains("--gl-ua-bitness=64", args);
        Assert.Contains("--gl-ua-full-version=156.0.8078.76", args);
        Assert.Contains("--gl-ua-mobile=1", args);
    }

    [Fact]
    public void Dpr_switch_uses_invariant_culture()
    {
        var fp = WindowsSample();
        fp.DevicePixelRatio = 1.25;
        Assert.Contains("--gl-dpr=1.25", GlSwitchBuilder.Build(SpoofPlan.Resolve(fp, null)).Args);
    }

    [Theory]
    [InlineData(TargetOs.Windows)]
    [InlineData(TargetOs.MacOs)]
    [InlineData(TargetOs.Linux)]
    public void Generator_populates_non_empty_fonts_per_os(TargetOs os)
    {
        var fp = FingerprintGenerator.Generate("156.0.8078.76", os, seed: 42);
        Assert.False(string.IsNullOrWhiteSpace(fp.Fonts));
        Assert.True(fp.Fonts.Split(',').Length >= 5);
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
