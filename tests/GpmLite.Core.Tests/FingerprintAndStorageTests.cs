using GpmLite.Core.Browser;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;
using GpmLite.Core.Services;
using GpmLite.Core.Storage;

namespace GpmLite.Core.Tests;

public class FingerprintGeneratorTests
{
    private const string Version = "156.0.8078.76";

    [Fact]
    public void Same_seed_gives_same_fingerprint()
    {
        var a = FingerprintGenerator.Generate(Version, TargetOs.Windows, seed: 42);
        var b = FingerprintGenerator.Generate(Version, TargetOs.Windows, seed: 42);
        Assert.Equal(a.ToJson(), b.ToJson());
    }

    [Fact]
    public void Different_seeds_vary()
    {
        var jsons = Enumerable.Range(1, 30)
            .Select(i => FingerprintGenerator.Generate(Version, TargetOs.Windows, (uint)i).ToJson())
            .ToHashSet();
        Assert.True(jsons.Count > 25);
    }

    [Theory]
    [InlineData(TargetOs.Windows, "Win32", "Windows NT 10.0", "Windows")]
    [InlineData(TargetOs.MacOs, "MacIntel", "Macintosh", "macOS")]
    [InlineData(TargetOs.Linux, "Linux x86_64", "X11; Linux", "Linux")]
    public void Surfaces_are_consistent_with_os(TargetOs os, string platform, string uaToken, string uaPlatform)
    {
        for (uint seed = 1; seed < 50; seed++)
        {
            var fp = FingerprintGenerator.Generate(Version, os, seed);
            Assert.Equal(platform, fp.Platform);
            Assert.Contains(uaToken, fp.UserAgent);
            Assert.Equal(uaPlatform, fp.UaPlatform);
            Assert.Contains("Chrome/156.0.0.0", fp.UserAgent);
            Assert.Equal(156, fp.BrowserMajor);
            Assert.True(fp.AvailHeight <= fp.ScreenHeight && fp.AvailWidth <= fp.ScreenWidth);
            Assert.True(fp.WindowWidth <= fp.AvailWidth && fp.WindowHeight <= fp.AvailHeight);
            Assert.InRange(fp.DeviceMemory, 1, 8); // Chrome caps navigator.deviceMemory at 8
            if (os == TargetOs.MacOs)
            {
                Assert.Contains("Apple", fp.WebGlRenderer);
                Assert.Equal(2, fp.DevicePixelRatio);
            }
            else Assert.DoesNotContain("Apple", fp.WebGlRenderer);
        }
    }

    [Fact]
    public void Regenerate_keeps_user_modes_but_changes_values()
    {
        var fp = FingerprintGenerator.Generate(Version, TargetOs.Windows, 7);
        fp.Canvas = NoiseMode.Real;
        fp.WebRtc = WebRtcMode.Disabled;
        fp.TimezoneMode = AutoMode.Custom;
        fp.Timezone = "Asia/Ho_Chi_Minh";
        var next = FingerprintGenerator.Regenerate(fp, Version);
        Assert.Equal(NoiseMode.Real, next.Canvas);
        Assert.Equal(WebRtcMode.Disabled, next.WebRtc);
        Assert.Equal("Asia/Ho_Chi_Minh", next.Timezone);
        Assert.Equal(TargetOs.Windows, next.Os);
        Assert.NotEqual(fp.Seed, next.Seed);
    }

    [Fact]
    public void Json_roundtrip()
    {
        var fp = FingerprintGenerator.Generate(Version, TargetOs.MacOs, 99);
        Assert.Equal(fp.ToJson(), FingerprintConfig.FromJson(fp.ToJson()).ToJson());
    }

    [Fact]
    public void Invalid_version_throws() =>
        Assert.Throws<ArgumentException>(() => FingerprintGenerator.Generate("abc"));

    [Fact]
    public void Noise_amps_in_range_and_stable_per_seed()
    {
        for (uint seed = 1; seed < 40; seed++)
        {
            var fp = FingerprintGenerator.Generate(Version, TargetOs.Windows, seed);
            Assert.InRange(fp.CanvasRate, 5, 40);
            Assert.InRange(fp.WebglRate, 32, 128);
            Assert.InRange(fp.AudioAmp, 5e-6, 3e-5);
            Assert.InRange(fp.RectsAmp, 0.005, 0.05);

            var again = FingerprintGenerator.Generate(Version, TargetOs.Windows, seed);
            Assert.Equal(fp.CanvasRate, again.CanvasRate);
            Assert.Equal(fp.AudioAmp, again.AudioAmp);
        }
    }
}

public class WebGlPresetsTests
{
    [Theory]
    [InlineData("ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 (0x00002504) Direct3D11 vs_5_0 ps_5_0, D3D11)")]
    [InlineData("ANGLE (Apple, ANGLE Metal Renderer: Apple M1, Unspecified Version)")]
    [InlineData("ANGLE (Intel, Mesa Intel(R) UHD Graphics 620 (KBL GT2), OpenGL 4.6)")]
    [InlineData("totally unknown renderer")]
    public void BuildParamsJson_matches_contract_schema(string renderer)
    {
        var json = WebGlPresets.BuildParamsJson(renderer);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

        var @params = root["params"]!.AsObject();
        Assert.NotEmpty(@params);
        Assert.NotNull(@params["3379"]);        // MAX_TEXTURE_SIZE present as decimal key
        Assert.Equal(2, root["params"]!["3386"]!.AsArray().Count); // MAX_VIEWPORT_DIMS is [x,y]

        Assert.True(root["exts"]!.AsArray().Count > 5);
        Assert.Contains(root["exts"]!.AsArray(), e => e!.GetValue<string>() == "WEBGL_debug_renderer_info");

        var precisions = root["precisions"]!.AsObject();
        Assert.Equal(3, precisions["35632:36338"]!.AsArray().Count); // FRAGMENT_SHADER:HIGH_FLOAT -> [min,max,precision]
    }

    [Theory]
    [InlineData("ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)")]
    [InlineData("ANGLE (Apple, ANGLE Metal Renderer: Apple M1, Unspecified Version)")]
    [InlineData("ANGLE (Intel, Mesa Intel(R) UHD Graphics 620 (KBL GT2), OpenGL 4.6)")]
    [InlineData("totally unknown renderer")]
    public void BuildParamsJson_covers_webgl2_only_keys(string renderer)
    {
        var json = WebGlPresets.BuildParamsJson(renderer);
        var @params = System.Text.Json.Nodes.JsonNode.Parse(json)!["params"]!.AsObject();

        // Every WebGL2-only getParameter key from the WAVE3 contract must be present so a WebGL2
        // context never falls through to the real host value.
        string[] webgl2Keys =
        [
            "32883", // MAX_3D_TEXTURE_SIZE
            "35071", // MAX_ARRAY_TEXTURE_LAYERS
            "34852", // MAX_DRAW_BUFFERS
            "36063", // MAX_COLOR_ATTACHMENTS
            "36183", // MAX_SAMPLES
            "35375", // MAX_UNIFORM_BUFFER_BINDINGS
            "35371", // MAX_VERTEX_UNIFORM_BLOCKS
            "35373", // MAX_FRAGMENT_UNIFORM_BLOCKS
            "35374", // MAX_COMBINED_UNIFORM_BLOCKS
            "35376", // MAX_UNIFORM_BLOCK_SIZE
            "35659", // MAX_VARYING_COMPONENTS
            "37154", // MAX_VERTEX_OUTPUT_COMPONENTS
            "37157", // MAX_FRAGMENT_INPUT_COMPONENTS
            "36203", // MAX_ELEMENT_INDEX
            "34045", // MAX_TEXTURE_LOD_BIAS
        ];
        foreach (var k in webgl2Keys)
            Assert.NotNull(@params[k]);

        // Spot-check a couple of exact decimal values are realistic positive numbers.
        Assert.True(@params["32883"]!.GetValue<int>() >= 256);    // MAX_3D_TEXTURE_SIZE
        Assert.True(@params["35376"]!.GetValue<long>() >= 16384); // MAX_UNIFORM_BLOCK_SIZE
        Assert.True(@params["36203"]!.GetValue<long>() > 0);      // MAX_ELEMENT_INDEX
    }

    [Fact]
    public void Base64_roundtrips_to_the_json()
    {
        var json = WebGlPresets.BuildParamsJson("ANGLE (NVIDIA, NVIDIA GeForce RTX 4060, D3D11)");
        var b64 = WebGlPresets.ToBase64(json);
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Equal(json, decoded);
    }

    [Fact]
    public void Apple_and_nvidia_presets_differ()
    {
        var apple = WebGlPresets.BuildParamsJson("ANGLE (Apple, ANGLE Metal Renderer: Apple M2, Unspecified Version)");
        var nvidia = WebGlPresets.BuildParamsJson("ANGLE (NVIDIA, NVIDIA GeForce RTX 3060, D3D11)");
        Assert.NotEqual(apple, nvidia);
    }
}

public class VoicePresetsTests
{
    [Theory]
    [InlineData(TargetOs.Windows)]
    [InlineData(TargetOs.MacOs)]
    [InlineData(TargetOs.Linux)]
    public void BuildVoicesJson_is_non_empty_and_well_formed(TargetOs os)
    {
        var json = VoicePresets.BuildVoicesJson(os);
        var arr = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsArray();
        Assert.True(arr.Count >= 5);
        foreach (var v in arr)
        {
            var o = v!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(o["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(o["lang"]!.GetValue<string>()));
            Assert.NotNull(o["default"]);
            Assert.NotNull(o["local"]);
            _ = o["default"]!.GetValue<bool>();
            _ = o["local"]!.GetValue<bool>();
        }
        // Exactly one default voice.
        Assert.Equal(1, arr.Count(v => v!["default"]!.GetValue<bool>()));
    }

    [Fact]
    public void Windows_and_macos_lists_differ()
    {
        Assert.NotEqual(VoicePresets.BuildVoicesJson(TargetOs.Windows),
                        VoicePresets.BuildVoicesJson(TargetOs.MacOs));
    }

    [Fact]
    public void Windows_has_sapi_voices_and_macos_has_system_voices()
    {
        Assert.Contains("Microsoft David", VoicePresets.BuildVoicesJson(TargetOs.Windows));
        Assert.Contains("Alex", VoicePresets.BuildVoicesJson(TargetOs.MacOs));
    }

    [Fact]
    public void Base64_roundtrips_to_the_json()
    {
        var json = VoicePresets.BuildVoicesJson(TargetOs.Windows);
        var decoded = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(VoicePresets.BuildVoicesBase64(TargetOs.Windows)));
        Assert.Equal(json, decoded);
    }
}

public class SpoofPlanTests
{
    private static readonly IpInfo Ip = new("5.6.7.8", "VN", "Vietnam", "HCM", "HCMC", 10.8, 106.6, "Asia/Ho_Chi_Minh", 120);

    [Fact]
    public void Auto_modes_follow_proxy_ip()
    {
        var fp = FingerprintGenerator.Generate("156.0.0.1", TargetOs.Windows, 1);
        var plan = SpoofPlan.Resolve(fp, Ip);
        Assert.Equal("Asia/Ho_Chi_Minh", plan.Timezone);
        Assert.StartsWith("vi-VN", plan.Languages);
        Assert.Equal("vi-VN", plan.PrimaryLocale);
        Assert.Equal((10.8, 106.6), (plan.Geo!.Value.Lat, plan.Geo.Value.Lon));
        Assert.Equal("5.6.7.8", plan.WebRtcPublicIp);
    }

    [Fact]
    public void Auto_without_lookup_falls_back_to_real()
    {
        var plan = SpoofPlan.Resolve(FingerprintGenerator.Generate("156.0.0.1", TargetOs.Windows, 1), null);
        Assert.Null(plan.Timezone);
        Assert.Null(plan.Languages);
        Assert.Null(plan.Geo);
    }

    [Fact]
    public void Custom_values_win_over_lookup()
    {
        var fp = FingerprintGenerator.Generate("156.0.0.1", TargetOs.Windows, 1);
        fp.TimezoneMode = AutoMode.Custom; fp.Timezone = "Europe/Berlin";
        fp.LanguageMode = AutoMode.Custom; fp.Languages = "de-DE,de";
        fp.WebRtcPublicIp = "9.9.9.9";
        var plan = SpoofPlan.Resolve(fp, Ip);
        Assert.Equal("Europe/Berlin", plan.Timezone);
        Assert.Equal("de-DE,de", plan.Languages);
        Assert.Equal("9.9.9.9", plan.WebRtcPublicIp);
    }

    [Fact]
    public void Script_embeds_config_and_has_no_placeholder()
    {
        var fp = FingerprintGenerator.Generate("156.0.0.1", TargetOs.Windows, 1);
        fp.WebGlRenderer = "</script><script>alert(1)</script>";
        var js = SpoofPlan.Resolve(fp, null).BuildScript();
        Assert.DoesNotContain("__CFG__", js);
        Assert.DoesNotContain("</script>", js);
        Assert.Contains($"\"seed\":{fp.Seed}", js);
    }

    [Fact]
    public async Task Launch_args_never_put_credentials_on_the_command_line()
    {
        var fp = FingerprintGenerator.Generate("156.0.0.1", TargetOs.Windows, 1);
        var proxy = new ProxyConfig(ProxyType.Socks5, "127.0.0.1", 1, "user", "topsecret");
        var req = new LaunchRequest
        {
            Profile = new Profile { StoragePath = "/tmp/x" },
            Fingerprint = fp,
            Core = new BrowserCore("/bin/chrome", "156.0.0.1"),
            Proxy = proxy,
        };
        var plan = SpoofPlan.Resolve(fp, Ip);
        // Relay port is irrelevant here; build args with a live relay to verify the proxy flag.
        await using var relay = new ProxyRelay(proxy);
        {
            var args = BrowserLauncher.BuildArguments(req, plan, relay);
            Assert.DoesNotContain(args, a => a.Contains("topsecret"));
            Assert.Contains($"--proxy-server=http://{relay.Address}", args);
            Assert.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", args);
            Assert.Contains("--accept-lang=vi-VN,vi,en-US,en", args);
            Assert.DoesNotContain(args, a => a.Contains("enable-automation"));
            Assert.Contains("--disable-blink-features=AutomationControlled", args);
            Assert.Equal("about:blank", args[^1]);
        }
    }
}

public sealed class RepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpmlite-test-" + Guid.NewGuid().ToString("N"));
    private readonly Repository _repo;

    public RepositoryTests()
    {
        var paths = new AppPaths(_dir);
        _repo = new Repository(new Database(paths.DatabaseFile));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Profile_crud_and_search()
    {
        var g = new Group { Name = "Ads" };
        _repo.UpsertGroup(g);
        var p = new Profile { Name = "FB 01", GroupId = g.Id, Proxy = "1.2.3.4:80", FingerprintJson = "{}", StoragePath = "/x" };
        _repo.UpsertProfile(p);

        Assert.Single(_repo.ListProfiles(g.Id));
        Assert.Single(_repo.ListProfiles(search: "FB"));
        Assert.Empty(_repo.ListProfiles(search: "zzz"));

        p.Name = "FB 02";
        p.LastRunAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _repo.UpsertProfile(p);
        var loaded = _repo.GetProfile(p.Id)!;
        Assert.Equal("FB 02", loaded.Name);
        Assert.Equal(p.LastRunAt, loaded.LastRunAt);

        _repo.DeleteGroup(g.Id); // ON DELETE SET NULL
        Assert.Null(_repo.GetProfile(p.Id)!.GroupId);

        _repo.DeleteProfile(p.Id);
        Assert.Null(_repo.GetProfile(p.Id));
    }

    [Fact]
    public void Tags_attach_and_cascade()
    {
        var p = new Profile { Name = "a", FingerprintJson = "{}", StoragePath = "/x" };
        _repo.UpsertProfile(p);
        var t1 = new Tag { Name = "vip" };
        var t2 = new Tag { Name = "new" };
        _repo.UpsertTag(t1);
        _repo.UpsertTag(t2);
        _repo.SetProfileTags(p.Id, [t1.Id, t2.Id, t1.Id]);
        Assert.Equal(2, _repo.TagsByProfile()[p.Id].Count);

        _repo.DeleteTag(t1.Id);
        Assert.Single(_repo.TagsByProfile()[p.Id]);
        _repo.DeleteProfile(p.Id);
        Assert.False(_repo.TagsByProfile().ContainsKey(p.Id));
    }
}

public class LocalApiTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("http://127.0.0.1:3000", true)]
    [InlineData("http://localhost", true)]
    [InlineData("http://127.0.0.1.evil.com", false)]
    [InlineData("http://localhost.evil.com", false)]
    [InlineData("https://evil.com", false)]
    [InlineData("null", false)]
    public void Origin_check(string? origin, bool allowed) =>
        Assert.Equal(allowed, LocalApiServer.IsAllowedOrigin(origin));
}
