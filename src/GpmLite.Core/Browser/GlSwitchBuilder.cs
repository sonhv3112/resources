using GpmLite.Core.Fingerprint;

namespace GpmLite.Core.Browser;

/// <summary>Native switches for the patched "GpmLite core": the <c>--gl-*</c> family plus the
/// non-gl natives (<c>--user-agent</c>, <c>--lang</c>/<c>--accept-lang</c>) and the <c>TZ</c> env value.</summary>
public sealed record GlSwitches(IReadOnlyList<string> Args, string? Timezone);

/// <summary>
/// Turns a resolved <see cref="SpoofPlan"/> into the command-line switches the patched Chromium reads
/// and applies natively in Blink — replacing the CDP/JS spoofing used with stock Chrome.
/// </summary>
public static class GlSwitchBuilder
{
    public static GlSwitches Build(SpoofPlan plan)
    {
        var fp = plan.Fp;
        var args = new List<string>
        {
            // Dev-signed local build: Chrome's code-sign-clone self-copy can't find the
            // Framework via @rpath and aborts at launch. Disabling the feature avoids it.
            "--disable-features=MacAppCodeSignClone",
            "--gl-enabled",
            $"--gl-seed={fp.Seed}",
            $"--gl-platform={fp.Platform}",
            $"--gl-hardware-concurrency={fp.HardwareConcurrency}",
            $"--gl-device-memory={fp.DeviceMemory}",
            $"--gl-screen={fp.ScreenWidth}x{fp.ScreenHeight}",
            $"--gl-avail={fp.AvailWidth}x{fp.AvailHeight}",
            $"--gl-color-depth={fp.ColorDepth}",
        };

        // WebGL vendor/renderer are only spoofed when the profile overrides the metadata.
        if (fp.WebGlMetadataCustom)
        {
            args.Add($"--gl-webgl-vendor={fp.WebGlVendor}");
            args.Add($"--gl-webgl-renderer={fp.WebGlRenderer}");
        }

        // Noise flags: present only when the matching surface is in Noise mode.
        if (fp.Canvas == NoiseMode.Noise) args.Add("--gl-canvas-noise");
        if (fp.WebGlImage == NoiseMode.Noise) args.Add("--gl-webgl-noise");
        if (fp.Audio == NoiseMode.Noise) args.Add("--gl-audio-noise");
        if (fp.ClientRects == NoiseMode.Noise) args.Add("--gl-rects-noise");

        args.Add($"--gl-webrtc={WebRtcSwitch(fp.WebRtc)}");
        // navigator.vendor is always "Google Inc." on Chrome; phase-2 switch, harmless if unknown.
        args.Add("--gl-vendor=Google Inc.");

        // Non-gl natives that replace the CDP overrides (built-in Chromium switches).
        if (!string.IsNullOrWhiteSpace(fp.UserAgent)) args.Add($"--user-agent={fp.UserAgent}");
        if (plan.Languages is { } langs)
        {
            args.Add($"--lang={plan.PrimaryLocale}");
            args.Add($"--accept-lang={langs}");
        }

        // Timezone has no native switch; Chromium honours the TZ env var on the child process.
        return new GlSwitches(args, plan.Timezone);
    }

    private static string WebRtcSwitch(WebRtcMode mode) => mode switch
    {
        WebRtcMode.Real => "real",
        WebRtcMode.Altered => "proxy",
        WebRtcMode.Disabled => "disable",
        _ => "real",
    };
}
