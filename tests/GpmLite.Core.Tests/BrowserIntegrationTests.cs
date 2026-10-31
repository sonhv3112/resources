using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GpmLite.Core.Browser;
using GpmLite.Core.Cdp;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Tests;

/// <summary>
/// Launches the locally installed Chrome (headless, throwaway profile dir) and checks what a web page sees.
/// Skipped automatically when no Chrome is installed.
/// </summary>
[Collection("browser")]
public sealed class BrowserIntegrationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpmlite-it-" + Guid.NewGuid().ToString("N"));
    private readonly BrowserCore? _core = ChromeLocator.Find();
    private HttpListener? _origin;
    private int _originPort;

    public Task InitializeAsync()
    {
        // Tiny origin server; Chrome reaches it through relay -> authenticated fake proxy.
        for (var attempt = 0; attempt < 5 && _origin is null; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); _origin = l; _originPort = port; }
            catch (HttpListenerException) { l.Close(); }
        }
        _ = Task.Run(async () =>
        {
            while (_origin is { IsListening: true })
            {
                HttpListenerContext ctx;
                try { ctx = await _origin.GetContextAsync(); } catch { return; }
                var html = "<!doctype html><html><body><canvas id=c width=200 height=50></canvas></body></html>"u8.ToArray();
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = html.Length;
                await ctx.Response.OutputStream.WriteAsync(html);
                ctx.Response.Close();
            }
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _origin?.Close();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private const string Probe = """
        (async () => {
          const gl = document.createElement('canvas').getContext('webgl');
          const dbg = gl && gl.getExtension('WEBGL_debug_renderer_info');
          const c = document.getElementById('c'), x = c.getContext('2d');
          x.font = '20px Arial'; x.fillStyle = '#f60'; x.fillText('GpmLite fp test 😀', 2, 30);
          const w = new Worker(URL.createObjectURL(new Blob(
            ['postMessage({hc: navigator.hardwareConcurrency, ua: navigator.userAgent})'], {type: 'text/javascript'})));
          const worker = await new Promise(r => { w.onmessage = e => r(e.data); });
          const uad = await navigator.userAgentData.getHighEntropyValues(['platform', 'platformVersion', 'fullVersionList']);
          return JSON.stringify({
            ua: navigator.userAgent, platform: navigator.platform, hc: navigator.hardwareConcurrency,
            mem: navigator.deviceMemory, webdriver: navigator.webdriver, langs: navigator.languages,
            sw: screen.width, sh: screen.height, ah: screen.availHeight, dpr: devicePixelRatio,
            tz: Intl.DateTimeFormat().resolvedOptions().timeZone,
            renderer: dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : null,
            nativeToString: Function.prototype.toString.call(HTMLCanvasElement.prototype.toDataURL),
            toStringOfToString: Function.prototype.toString.toString(),
            hcOwnDesc: Object.getOwnPropertyDescriptor(navigator, 'hardwareConcurrency') === undefined,
            canvas1: c.toDataURL(), canvas2: c.toDataURL(),
            worker, uaPlatform: uad.platform, brands: uad.brands.map(b => b.brand),
          });
        })()
        """;

    private async Task<JsonObject> RunProbe(FingerprintConfig fp, ProxyConfig? proxy, IpInfo? ip, string profileId)
    {
        var profile = new Profile { Id = profileId, Name = profileId, StoragePath = Path.Combine(_dir, profileId) };
        await using var rb = await BrowserLauncher.LaunchAsync(new LaunchRequest
        {
            Profile = profile,
            Fingerprint = fp,
            Core = _core!,
            Proxy = proxy,
            IpInfo = ip,
            StartupUrls = [$"http://127.0.0.1:{_originPort}/"],
            ExtraArgs = ["--headless=new"],
        });

        await using var cdp = await CdpConnection.ConnectAsync(rb.BrowserWebSocketUrl);
        string? pageTarget = null;
        for (var i = 0; i < 100 && pageTarget is null; i++)
        {
            var t = await cdp.SendAsync("Target.getTargets");
            pageTarget = (t["targetInfos"] as JsonArray)!.OfType<JsonObject>()
                .FirstOrDefault(x => x["type"]!.GetValue<string>() == "page" &&
                                     x["url"]!.GetValue<string>().Contains($":{_originPort}"))?["targetId"]?.GetValue<string>();
            if (pageTarget is null) await Task.Delay(100);
        }
        Assert.NotNull(pageTarget);
        var session = (await cdp.SendAsync("Target.attachToTarget", new { targetId = pageTarget, flatten = true }))["sessionId"]!.GetValue<string>();

        JsonObject? result = null;
        string? lastError = null;
        for (var i = 0; i < 50 && result is null; i++)
        {
            var r = await cdp.SendAsync("Runtime.evaluate", new { expression = Probe, awaitPromise = true, returnByValue = true }, session);
            if (r["result"]?["value"] is JsonValue v && v.TryGetValue<string>(out var json)) result = JsonNode.Parse(json) as JsonObject;
            else
            {
                lastError = r["exceptionDetails"]?["exception"]?["description"]?.ToString() ?? r.ToJsonString();
                await Task.Delay(200); // page may still be loading
            }
        }
        Assert.True(result is not null, $"probe failed: {lastError}");
        return result!;
    }

    [Fact]
    public async Task Page_and_worker_see_the_spoofed_fingerprint_through_authenticated_proxy()
    {
        if (_core is null) return; // no Chrome installed
        Assert.NotNull(_origin);

        await using var upstream = new FakeHttpProxy("alice", "s3cret");
        var proxy = new ProxyConfig(ProxyType.Http, "127.0.0.1", upstream.Port, "alice", "s3cret");
        var ip = new IpInfo("5.6.7.8", "DE", "Germany", "BE", "Berlin", 52.5, 13.4, "Europe/Berlin", 10);

        var fp = FingerprintGenerator.Generate(_core.Version, TargetOs.Windows, seed: 1234);
        fp.HardwareConcurrency = 6;
        fp.DeviceMemory = 4;
        var r = await RunProbe(fp, proxy, ip, "p1");
        Assert.False(r["webdriver"]!.GetValue<bool>(), "navigator.webdriver is true: " + r.ToJsonString());

        Assert.Equal(fp.UserAgent, r["ua"]!.GetValue<string>());
        Assert.Equal("Win32", r["platform"]!.GetValue<string>());
        Assert.Equal("Windows", r["uaPlatform"]!.GetValue<string>());
        Assert.Equal(6, r["hc"]!.GetValue<int>());
        Assert.Equal(4, r["mem"]!.GetValue<double>());
        Assert.Equal(fp.ScreenWidth, r["sw"]!.GetValue<int>());
        Assert.Equal(fp.ScreenHeight, r["sh"]!.GetValue<int>());
        Assert.Equal(fp.AvailHeight, r["ah"]!.GetValue<int>());
        Assert.Equal("Europe/Berlin", r["tz"]!.GetValue<string>());
        Assert.Equal("de-DE", r["langs"]![0]!.GetValue<string>());
        Assert.Equal("function toDataURL() { [native code] }", r["nativeToString"]!.GetValue<string>());
        Assert.Equal("function toString() { [native code] }", r["toStringOfToString"]!.GetValue<string>());
        Assert.True(r["hcOwnDesc"]!.GetValue<bool>()); // override lives on the prototype, like the real one
        Assert.Equal(r["canvas1"]!.GetValue<string>(), r["canvas2"]!.GetValue<string>()); // stable within a profile
        Assert.Equal(6, r["worker"]!["hc"]!.GetValue<int>());
        Assert.Equal(fp.UserAgent, r["worker"]!["ua"]!.GetValue<string>());
        Assert.Contains("Google Chrome", r["brands"]!.AsArray().Select(b => b!.GetValue<string>()));
        if (r["renderer"] is JsonValue renderer) Assert.Equal(fp.WebGlRenderer, renderer.GetValue<string>());
        // The page really went through the authenticated upstream proxy.
        Assert.Contains($"127.0.0.1:{_originPort}", upstream.Targets);
    }

    [Fact]
    public async Task Different_profiles_get_different_canvas_hashes()
    {
        if (_core is null) return;
        var a = await RunProbe(FingerprintGenerator.Generate(_core.Version, null, 1), null, null, "a");
        var b = await RunProbe(FingerprintGenerator.Generate(_core.Version, null, 2), null, null, "b");
        Assert.NotEqual(a["canvas1"]!.GetValue<string>(), b["canvas1"]!.GetValue<string>());
    }
}
