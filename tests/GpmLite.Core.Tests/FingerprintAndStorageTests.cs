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
