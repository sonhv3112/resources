using System.Globalization;
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
        // navigator.vendor; "Google Inc." on real Chrome but user-overridable. Fall back when blank.
        var vendor = string.IsNullOrWhiteSpace(fp.Vendor) ? "Google Inc." : fp.Vendor;
        args.Add($"--gl-vendor={vendor}");

        // Font whitelist: only restrict the surface when a list is configured.
        if (!string.IsNullOrWhiteSpace(fp.Fonts)) args.Add($"--gl-fonts={fp.Fonts}");

        // Media device counts exposed by enumerateDevices(); always emitted (they are counts).
        args.Add($"--gl-media-audioinputs={fp.AudioInputs}");
        args.Add($"--gl-media-audiooutputs={fp.AudioOutputs}");
        args.Add($"--gl-media-videoinputs={fp.VideoInputs}");

        // Geolocation: emitted when the plan resolved a position (Auto from IP or Custom).
        if (plan.Geo is { } geo)
        {
            args.Add($"--gl-geo-lat={geo.Lat.ToString(CultureInfo.InvariantCulture)}");
            args.Add($"--gl-geo-lon={geo.Lon.ToString(CultureInfo.InvariantCulture)}");
            args.Add($"--gl-geo-accuracy={geo.Accuracy.ToString(CultureInfo.InvariantCulture)}");
        }

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
