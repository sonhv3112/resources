using System.Text.Json.Nodes;
using GpmLite.Core.Browser;
using Xunit;

namespace GpmLite.Core.Tests;

/// <summary>
/// End-to-end bridge tests for the Python Camoufox sidecar. Skipped unless the dev venv
/// (.camoufox-venv) and the sidecar script are present, so CI without them is unaffected.
/// </summary>
public class CamoufoxSidecarTests
{
    // Repo layout: <repo>/gpm-lite/tests/bin/... -> walk up to <repo>.
    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gpm-lite", "sidecar", "camoufox_sidecar.py")))
                return d.FullName;
        return null;
    }

    private static (string Python, string Script)? Env()
    {
        var root = RepoRoot();
        if (root is null) return null;
        var py = Path.Combine(root, ".camoufox-venv", "bin", "python");
        var script = Path.Combine(root, "gpm-lite", "sidecar", "camoufox_sidecar.py");
        return File.Exists(py) && File.Exists(script) ? (py, script) : null;
    }

    [Fact]
    public async Task Generate_then_spec_produces_windows_identity_and_env()
    {
        if (Env() is not var (python, script)) return; // sidecar/venv not available — skip
        var sidecar = new CamoufoxSidecar(python, script);

        var version = await sidecar.VersionAsync();
        Assert.False(string.IsNullOrWhiteSpace(version));

        var config = await sidecar.GenerateAsync("windows");
        Assert.Equal("Win32", config["navigator.platform"]?.GetValue<string>());
        Assert.Contains("Windows", config["navigator.userAgent"]!.GetValue<string>());
        // Volatile (proxy-driven) keys must be stripped so they follow geoip at launch.
        Assert.DoesNotContain(config, kv => kv.Key.StartsWith("timezone") || kv.Key.StartsWith("geolocation:"));

        var spec = await sidecar.SpecAsync("windows", config, executablePath: null, geoip: null, headless: true);
        Assert.Contains("CAMOU_CONFIG_1", spec.Env.Keys);
        Assert.False(string.IsNullOrEmpty(spec.ExecutablePath));
    }

    [Fact]
    public async Task Launcher_starts_and_stops_camoufox_headless()
    {
        if (Env() is not var (python, script)) return;
        var binary = ChromeLocator.DefaultCamoufoxPath();
        if (binary is null) return; // no camoufox binary installed — skip
        var sidecar = new CamoufoxSidecar(python, script);
        var config = await sidecar.GenerateAsync("windows");

        var dir = Path.Combine(Path.GetTempPath(), "gpmlite-cfx-" + Guid.NewGuid().ToString("N")[..8]);
        var profile = new Models.Profile { Name = "t", StoragePath = dir, FingerprintJson = "" };
        try
        {
            var browser = await CamoufoxLauncher.LaunchAsync(new CamoufoxLaunchRequest
            {
                Profile = profile,
                Config = config,
                Os = "windows",
                ExecutablePath = binary,
                Sidecar = sidecar,
                Headless = true,
                StartupUrls = ["about:blank"],
            });
            Assert.True(browser.IsRunning);
            Assert.Null(browser.DebuggerAddress); // Firefox: no CDP
            Assert.True(File.Exists(Path.Combine(dir, "user.js")));
            await browser.StopAsync(TimeSpan.FromSeconds(10));
            await browser.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Spec_pins_a_stored_config_stably()
    {
        if (Env() is not var (python, script)) return;
        var sidecar = new CamoufoxSidecar(python, script);
        var config = await sidecar.GenerateAsync("windows");
        var renderer = config["webGl:renderer"]?.GetValue<string>();

        // Re-feeding the same config must round-trip the same identity into the env chunks.
        var spec = await sidecar.SpecAsync("windows", config, null, null, true);
        var joined = string.Concat(spec.Env.Where(e => e.Key.StartsWith("CAMOU_CONFIG")).OrderBy(e => e.Key).Select(e => e.Value));
        var reparsed = JsonNode.Parse(joined) as JsonObject;
        Assert.Equal(renderer, reparsed?["webGl:renderer"]?.GetValue<string>());
    }
}
