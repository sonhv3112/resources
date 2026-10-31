using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using GpmLite.Core.Cdp;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Browser;

public sealed class LaunchException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class LaunchRequest
{
    public required Profile Profile { get; init; }
    public required FingerprintConfig Fingerprint { get; init; }
    public required BrowserCore Core { get; init; }
    /// <summary>CDP/JS injection (stock Chrome) or native --gl-* switches (patched GpmLite core).</summary>
    public BrowserCoreMode Mode { get; init; } = BrowserCoreMode.Cdp;
    public ProxyConfig? Proxy { get; init; }
    /// <summary>Result of a proxy check, used to resolve Auto timezone/language/geo and WebRTC IP.</summary>
    public IpInfo? IpInfo { get; init; }
    public IReadOnlyList<string> StartupUrls { get; init; } = [];
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
    /// <summary>Unpacked extension folders loaded into this profile.</summary>
    public IReadOnlyList<string> ExtensionDirs { get; init; } = [];
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Starts Chrome for a profile and keeps the fingerprint overrides applied to every target it creates.
/// </summary>
public static class BrowserLauncher
{
    public static IReadOnlyList<string> BuildArguments(LaunchRequest req, SpoofPlan plan, ProxyRelay? relay)
    {
        var fp = req.Fingerprint;
        var args = new List<string>
        {
            $"--user-data-dir={req.Profile.StoragePath}",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-features=PrivacySandboxSettings4,MediaRouter",
            $"--window-size={fp.WindowWidth},{fp.WindowHeight}",
        };
        // Hide the webdriver signal. Stock Chrome needs the Blink flag (but it shows an
        // "unsupported flag" warning bar). The GpmLite core forces navigator.webdriver=false
        // natively, so it needs no flag and shows no bar.
        if (req.Mode == BrowserCoreMode.Cdp)
            args.Add("--disable-blink-features=AutomationControlled");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) args.Add("--use-mock-keychain");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) args.Add("--password-store=basic");

        if (relay is not null)
        {
            args.Add($"--proxy-server=http://{relay.Address}");
            args.Add("--proxy-bypass-list=<-loopback>");
        }
        else args.Add("--no-proxy-server");

        // WebRTC: never let UDP bypass the proxy (that is what leaks the real IP).
        if (fp.WebRtc != WebRtcMode.Real) args.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");

        if (req.Mode == BrowserCoreMode.Native)
        {
            // Native core: fingerprint (incl. UA + languages) is applied via switches; no CDP/JS spoofing.
            args.AddRange(GlSwitchBuilder.Build(plan).Args);
        }
        else if (plan.Languages is { } langs)
        {
            args.Add($"--lang={plan.PrimaryLocale}");
            args.Add($"--accept-lang={langs}");
        }

        if (req.ExtensionDirs.Count > 0)
        {
            var list = string.Join(',', req.ExtensionDirs);
            args.Add($"--load-extension={list}");
        }

        args.AddRange(req.ExtraArgs);
        args.Add("about:blank"); // real URLs are opened only after overrides are in place
        return args;
    }

    public static async Task<RunningBrowser> LaunchAsync(LaunchRequest req, CancellationToken ct = default)
    {
        var plan = SpoofPlan.Resolve(req.Fingerprint, req.IpInfo);
        Directory.CreateDirectory(req.Profile.StoragePath);
        var portFile = Path.Combine(req.Profile.StoragePath, "DevToolsActivePort");
        if (File.Exists(portFile)) File.Delete(portFile);

        KillStaleInstance(req.Profile.StoragePath);
        ProxyRelay? relay = req.Proxy is null ? null : new ProxyRelay(req.Proxy);
        Process? proc = null;
        CdpConnection? cdp = null;
        try
        {
            var psi = new ProcessStartInfo(req.Core.ExecutablePath) { UseShellExecute = false };
            foreach (var a in BuildArguments(req, plan, relay)) psi.ArgumentList.Add(a);
            // Native core: Chromium honours TZ for the timezone (there is no --gl-timezone patch).
            if (req.Mode == BrowserCoreMode.Native && plan.Timezone is { } tz) psi.Environment["TZ"] = tz;
            proc = Process.Start(psi) ?? throw new LaunchException("Could not start the browser process");

            var wsUrl = await WaitForDevToolsAsync(portFile, proc, req.StartupTimeout, ct);
            cdp = await CdpConnection.ConnectAsync(wsUrl, ct);
            var running = new RunningBrowser(req.Profile.Id, proc, cdp, relay, plan, wsUrl, req.Mode);
            await running.InitializeAsync(req.StartupUrls, ct);
            return running;
        }
        catch (Exception e)
        {
            if (cdp is not null) await cdp.DisposeAsync();
            if (proc is { HasExited: false }) { try { proc.Kill(entireProcessTree: true); } catch { } }
            proc?.Dispose();
            if (relay is not null) await relay.DisposeAsync();
            throw e as LaunchException ?? new LaunchException($"Browser launch failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// A browser left over from a crashed/killed app still owns the profile (its proxy relay is gone, so it has
    /// no network). Chrome records the owner in the SingletonLock symlink as "host-pid". Only a process whose
    /// command line contains this exact user-data-dir is terminated.
    /// </summary>
    internal static void KillStaleInstance(string profileDir)
    {
        var lockFile = new FileInfo(Path.Combine(profileDir, "SingletonLock"));
        string? target;
        try { target = lockFile.LinkTarget; }
        catch (IOException) { return; }
        if (target is null) return;
        var dash = target.LastIndexOf('-');
        if (dash < 0 || !int.TryParse(target[(dash + 1)..], out var pid)) return;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!CommandLineOf(pid).Contains($"--user-data-dir={profileDir}", StringComparison.Ordinal)) return;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
        }
        catch (ArgumentException) { /* process already gone */ }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string CommandLineOf(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return ""; // lock file is not a symlink on Windows
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("ps", $"-p {pid} -ww -o command=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (ps is null) return "";
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(3000);
            return output;
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
    }

    private static async Task<Uri> WaitForDevToolsAsync(string portFile, Process proc, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
                throw new LaunchException(
                    $"Browser exited during startup (code {proc.ExitCode}). Is this profile already open in another window?");
            if (File.Exists(portFile))
            {
                string[] lines;
                try { lines = await File.ReadAllLinesAsync(portFile, ct); }
                catch (IOException) { lines = []; }
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port))
                    return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
            }
            await Task.Delay(100, ct);
        }
        throw new LaunchException("Timed out waiting for the browser's DevTools endpoint");
    }
}

/// <summary>A live browser for one profile: process + CDP + proxy relay.</summary>
public sealed class RunningBrowser : IAsyncDisposable
{
    private static readonly HashSet<string> PageTypes = ["page", "iframe"];
    private static readonly HashSet<string> WorkerTypes = ["worker", "service_worker", "shared_worker"];

    private readonly Process _process;
    private readonly CdpConnection _cdp;
    private readonly ProxyRelay? _relay;
    private readonly SpoofPlan _plan;
    private readonly BrowserCoreMode _mode;
    private readonly string _script;
    private int _exitedRaised;
    private bool _autoAttachEnabled;
    // One configuration task per CDP session; whoever sees the session first starts it, others await it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>> _configTasks = new();

    public string ProfileId { get; }
    public Uri BrowserWebSocketUrl { get; }
    /// <summary>"127.0.0.1:port" — what Selenium (debuggerAddress) / Puppeteer (browserURL) connect to.</summary>
    public string DebuggerAddress => $"{BrowserWebSocketUrl.Host}:{BrowserWebSocketUrl.Port}";
    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;

    /// <summary>Sends a raw CDP command on the browser-level session. Used by cookie import/export
    /// (e.g. Storage.getCookies / Storage.setCookies), which are browser-wide and need no page attach.</summary>
    public Task<JsonObject> SendCdpAsync(string method, object? parameters = null, CancellationToken ct = default) =>
        _cdp.SendAsync(method, parameters, ct: ct);

    public event Action<RunningBrowser>? Exited;
    /// <summary>Non-fatal problems (an override the browser refused, a relay error...).</summary>
    public event Action<string>? Warning;

    internal RunningBrowser(string profileId, Process process, CdpConnection cdp, ProxyRelay? relay, SpoofPlan plan, Uri wsUrl,
        BrowserCoreMode mode = BrowserCoreMode.Cdp)
    {
        ProfileId = profileId;
        _process = process;
        _cdp = cdp;
        _relay = relay;
        _plan = plan;
        _mode = mode;
        BrowserWebSocketUrl = wsUrl;
        // Native core spoofs in Blink from --gl-* switches; no in-page script is injected.
        _script = mode == BrowserCoreMode.Native ? "" : plan.BuildScript();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _ = OnExitedAsync();
        if (_relay is not null) _relay.Error += msg => Warning?.Invoke($"proxy: {msg}");
        _cdp.EventReceived += OnEvent;
    }

    internal async Task InitializeAsync(IReadOnlyList<string> startupUrls, CancellationToken ct)
    {
        // Native core already applied the fingerprint from switches; just open the startup URLs.
        if (_mode == BrowserCoreMode.Native)
        {
            await InitializeNativeAsync(startupUrls, ct);
            return;
        }

        // 1) The initial about:blank tab: read the real UA brands first, then configure it.
        var targets = await _cdp.SendAsync("Target.getTargets", ct: ct);
        var firstPage = (targets["targetInfos"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(t => t["type"]?.GetValue<string>() == "page");
        if (firstPage is null) throw new LaunchException("Browser started without a tab");

        var targetId = firstPage["targetId"]!.GetValue<string>();
        var attach = await _cdp.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, ct: ct);
        var sessionId = attach["sessionId"]!.GetValue<string>();
        await CaptureRealBrandsAsync(sessionId);
        await EnsureConfiguredAsync(sessionId, "page");

        // 2) From now on every new target is paused until configured.
        Volatile.Write(ref _autoAttachEnabled, true);
        await _cdp.SendAsync("Target.setAutoAttach",
            new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, ct: ct);

        // 3) Only now load real content.
        var urls = startupUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0) urls.Add("chrome://newtab");
        await _cdp.TrySendAsync("Page.navigate", new { url = urls[0] }, sessionId);
        foreach (var url in urls.Skip(1))
            await _cdp.TrySendAsync("Target.createTarget", new { url });
    }

    /// <summary>Native core: no CDP spoofing, no auto-attach. Just navigate the first tab and open the rest.</summary>
    private async Task InitializeNativeAsync(IReadOnlyList<string> startupUrls, CancellationToken ct)
    {
        var urls = startupUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0) urls.Add("chrome://newtab");

        var targets = await _cdp.SendAsync("Target.getTargets", ct: ct);
        var firstPage = (targets["targetInfos"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(t => t["type"]?.GetValue<string>() == "page");
        if (firstPage is not null)
        {
            var targetId = firstPage["targetId"]!.GetValue<string>();
            var attach = await _cdp.SendAsync("Target.attachToTarget", new { targetId, flatten = true }, ct: ct);
            var sessionId = attach["sessionId"]!.GetValue<string>();
            // Geolocation is the one surface with no native --gl-* switch: apply it via CDP during init.
            if (_plan.Geo is { } g)
                await _cdp.TrySendAsync("Emulation.setGeolocationOverride",
                    new { latitude = g.Lat, longitude = g.Lon, accuracy = g.Accuracy }, sessionId);
            await _cdp.TrySendAsync("Page.navigate", new { url = urls[0] }, sessionId);
        }
        else
        {
            await _cdp.TrySendAsync("Target.createTarget", new { url = urls[0] });
        }
        foreach (var url in urls.Skip(1))
            await _cdp.TrySendAsync("Target.createTarget", new { url });
    }

    private async Task CaptureRealBrandsAsync(string sessionId)
    {
        try
        {
            var r = await _cdp.SendAsync("Runtime.evaluate", new
            {
                expression = "navigator.userAgentData ? navigator.userAgentData.getHighEntropyValues(['fullVersionList'])" +
                             ".then(v => JSON.stringify({brands: v.brands, full: v.fullVersionList})) : null",
                awaitPromise = true,
                returnByValue = true,
            }, sessionId);
            if (r["result"]?["value"]?.GetValue<string>() is { } json && JsonNode.Parse(json) is JsonObject o)
            {
                _plan.RealBrands = o["brands"];
                _plan.RealFullVersionList = o["full"];
            }
        }
        catch (Exception e) when (e is CdpException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Warning?.Invoke($"could not read real UA brands: {e.Message}");
        }
    }

    private void OnEvent(CdpEvent ev)
    {
        if (ev.Method != "Target.attachedToTarget" || !Volatile.Read(ref _autoAttachEnabled)) return;
        var sessionId = ev.Params["sessionId"]?.GetValue<string>();
        var type = (ev.Params["targetInfo"] as JsonObject)?["type"]?.GetValue<string>();
        if (sessionId is null || type is null) return;

        _ = Task.Run(async () =>
        {
            try { await EnsureConfiguredAsync(sessionId, type); }
            finally
            {
                // Always resume, otherwise a paused target would hang forever.
                await _cdp.TrySendAsync("Runtime.runIfWaitingForDebugger", null, sessionId);
            }
        });
    }

    private Task EnsureConfiguredAsync(string sessionId, string type) =>
        _configTasks.GetOrAdd(sessionId, _ => new Lazy<Task>(async () =>
        {
            try
            {
                if (PageTypes.Contains(type)) await ConfigurePageAsync(sessionId, isTopLevel: type == "page");
                else if (WorkerTypes.Contains(type)) await ConfigureWorkerAsync(sessionId);
            }
            catch (Exception e) { Warning?.Invoke($"configure {type}: {e.Message}"); }
        })).Value;

    private async Task ConfigurePageAsync(string sessionId, bool isTopLevel)
    {
        var fp = _plan.Fp;
        // Child OOPIFs and dedicated workers of this page get paused too.
        await _cdp.TrySendAsync("Target.setAutoAttach",
            new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, sessionId);

        await Must("Page.addScriptToEvaluateOnNewDocument", new { source = _script, runImmediately = true }, sessionId);
        await Must("Emulation.setUserAgentOverride", _plan.UserAgentOverride(), sessionId);
        await _cdp.TrySendAsync("Emulation.setHardwareConcurrencyOverride",
            new { hardwareConcurrency = fp.HardwareConcurrency }, sessionId);

        if (_plan.Timezone is { } tz) await Must("Emulation.setTimezoneOverride", new { timezoneId = tz }, sessionId);
        if (_plan.PrimaryLocale is { } locale)
            await _cdp.TrySendAsync("Emulation.setLocaleOverride", new { locale = locale.Replace('_', '-') }, sessionId);
        if (_plan.Geo is { } g)
            await _cdp.TrySendAsync("Emulation.setGeolocationOverride",
                new { latitude = g.Lat, longitude = g.Lon, accuracy = g.Accuracy }, sessionId);

        if (isTopLevel)
        {
            // width/height 0 = keep the real viewport; only screen size and DPR are overridden.
            await _cdp.TrySendAsync("Emulation.setDeviceMetricsOverride", new
            {
                width = 0,
                height = 0,
                deviceScaleFactor = fp.DevicePixelRatio,
                mobile = false,
                screenWidth = fp.ScreenWidth,
                screenHeight = fp.ScreenHeight,
            }, sessionId);
        }
    }

    private async Task ConfigureWorkerAsync(string sessionId)
    {
        await _cdp.TrySendAsync("Network.setUserAgentOverride", _plan.UserAgentOverride(), sessionId);
        await _cdp.TrySendAsync("Runtime.evaluate", new { expression = _script }, sessionId);
    }

    private async Task Must(string method, object payload, string sessionId)
    {
        if (!await _cdp.TrySendAsync(method, payload, sessionId))
            Warning?.Invoke($"{method} was rejected by the browser");
    }

    /// <summary>Graceful close (lets Chrome flush cookies/session), falling back to kill.</summary>
    public async Task StopAsync(TimeSpan? grace = null)
    {
        if (_process.HasExited) return;
        await _cdp.TrySendAsync("Browser.close");
        try { await _process.WaitForExitAsync().WaitAsync(grace ?? TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private async Task OnExitedAsync()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) == 1) return;
        await DisposeResourcesAsync();
        Exited?.Invoke(this);
    }

    private async Task DisposeResourcesAsync()
    {
        _cdp.EventReceived -= OnEvent;
        await _cdp.DisposeAsync();
        if (_relay is not null) await _relay.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await OnExitedAsync();
        _process.Dispose();
    }
}
