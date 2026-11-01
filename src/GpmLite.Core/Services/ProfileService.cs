using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GpmLite.Core.Browser;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;
using GpmLite.Core.Storage;

namespace GpmLite.Core.Services;

/// <summary>Which browser engine profiles launch: patched Chromium (CDP / native --gl-*) or Camoufox (Firefox).</summary>
public enum BrowserEngine { Chromium, Camoufox }

public sealed class AppSettings
{
    /// <summary>Which engine to launch. Camoufox uses the Python sidecar + Firefox binary; Chromium uses the core/stock Chrome.</summary>
    public BrowserEngine Engine { get; set; } = BrowserEngine.Camoufox;
    /// <summary>Camoufox (Firefox) executable. Empty = auto-detect the pip-cache / bundled Camoufox.</summary>
    public string CamoufoxPath { get; set; } = "";
    /// <summary>Python interpreter that has the <c>camoufox</c> package installed (runs the sidecar). Empty = "python3" on PATH.</summary>
    public string PythonPath { get; set; } = "";
    /// <summary>Path to <c>camoufox_sidecar.py</c>. Empty = resolve next to the app, else the repo's sidecar/ folder.</summary>
    public string SidecarPath { get; set; } = "";
    /// <summary>Custom Chrome/Chromium executable; empty = auto-detect.</summary>
    public string BrowserPath { get; set; } = "";
    /// <summary>Patched "GpmLite core" Chromium executable. When set and present, profiles launch it with
    /// native --gl-* fingerprint switches instead of stock Chrome + CDP/JS injection. Empty = stock Chrome.</summary>
    public string CustomCorePath { get; set; } = "";
    public string IpLookupUrl { get; set; } = ProxyChecker.DefaultLookupUrl;
    public bool LocalApiEnabled { get; set; } = true;
    public int LocalApiPort { get; set; } = 19995;
    public string Language { get; set; } = "vi";
    /// <summary>Check the proxy before every launch (needed for Auto timezone/geo/language).</summary>
    public bool CheckProxyOnStart { get; set; } = true;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Opts) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException) { return new AppSettings(); }
    }

    public void Save(string file) => File.WriteAllText(file, JsonSerializer.Serialize(this, Opts));
}

public enum ProfileRunState { Stopped, Starting, Running, Stopping }

public sealed record ProfileStatus(string ProfileId, ProfileRunState State, string? Message = null, string? DebuggerAddress = null);

/// <summary>Application service for everything profile-related; UI and the local API both go through it.</summary>
public sealed class ProfileService
{
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly ConcurrentDictionary<string, IRunningBrowser> _running = new();
    private readonly ConcurrentDictionary<string, byte> _starting = new();

    public AppSettings Settings { get; }
    public event Action<ProfileStatus>? StatusChanged;
    public event Action<string, string>? Log; // (profileId, message)

    public ProfileService(AppPaths paths, Repository repo, AppSettings settings)
    {
        _paths = paths;
        _repo = repo;
        Settings = settings;
    }

    public Repository Repo => _repo;
    public AppPaths Paths => _paths;

    public BrowserCore RequireCore() =>
        ChromeLocator.Find(Settings.BrowserPath)
        ?? throw new LaunchException("No Chrome/Chromium found. Install Google Chrome or set a browser path in Settings.");

    /// <summary>Version string used to seed a new profile's stored <see cref="FingerprintConfig"/>. Uses the
    /// detected Chrome core, or a placeholder for the Camoufox engine (which builds its own Firefox UA at
    /// launch via the sidecar — only the config's OS/screen are used, not this Chrome version).</summary>
    private const string CamoufoxPlaceholderVersion = "159.0.0.0";
    private string NewProfileCoreVersion()
    {
        try { return RequireCore().Version; }
        catch (LaunchException) when (Settings.Engine == BrowserEngine.Camoufox) { return CamoufoxPlaceholderVersion; }
    }

    /// <summary>Picks the core for a launch. Priority: (1) an explicit <see cref="AppSettings.CustomCorePath"/>,
    /// (2) the patched GpmLite core shipped inside the app bundle, both launched in native --gl-* mode;
    /// otherwise (3) stock Chrome via CDP/JS injection.</summary>
    public (BrowserCore Core, BrowserCoreMode Mode) ResolveCore()
    {
        if (!string.IsNullOrWhiteSpace(Settings.CustomCorePath))
        {
            if (!File.Exists(Settings.CustomCorePath))
                throw new LaunchException($"GpmLite core not found at {Settings.CustomCorePath}");
            var custom = ChromeLocator.Find(Settings.CustomCorePath)
                ?? throw new LaunchException($"Could not read the GpmLite core version at {Settings.CustomCorePath}");
            return (custom, BrowserCoreMode.Native);
        }

        // No explicit core set: prefer the patched core bundled in the app, if present.
        if (ChromeLocator.BundledCorePath() is { } bundled && ChromeLocator.Find(bundled) is { } bundledCore)
            return (bundledCore, BrowserCoreMode.Native);

        return (RequireCore(), BrowserCoreMode.Cdp);
    }

    // ---------- CRUD ----------
    public Profile Create(string name, string? groupId = null, string proxy = "", TargetOs? os = null,
        FingerprintConfig? fingerprint = null, string note = "", string startupUrls = "")
    {
        if (!string.IsNullOrWhiteSpace(proxy) && !ProxyConfig.TryParse(proxy, out _, out var err))
            throw new ArgumentException($"Invalid proxy: {err}", nameof(proxy));

        var coreVersion = NewProfileCoreVersion();
        var p = new Profile
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Profile {DateTime.Now:yyMMdd-HHmmss}" : name.Trim(),
            GroupId = groupId,
            Proxy = proxy.Trim(),
            Note = note,
            StartupUrls = startupUrls,
        };
        p.StoragePath = _paths.ProfileDir(p.Id);
        p.FingerprintJson = (fingerprint ?? FingerprintGenerator.Generate(coreVersion, os)).ToJson();
        _repo.UpsertProfile(p);
        return p;
    }

    /// <summary>Creates <paramref name="count"/> profiles named "{prefix} 1..N", optionally one proxy per line.</summary>
    public List<Profile> CreateBulk(string prefix, int count, string? groupId, IReadOnlyList<string> proxies, TargetOs? os)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count), "1..1000");
        var start = _repo.CountProfiles() + 1;
        var list = new List<Profile>(count);
        for (var i = 0; i < count; i++)
        {
            var proxy = proxies.Count > 0 ? proxies[i % proxies.Count] : "";
            list.Add(Create($"{prefix} {start + i}", groupId, proxy, os));
        }
        return list;
    }

    /// <summary>
    /// Duplicates a profile verbatim: same fingerprint AND a copy of its Chrome user-data-dir, so cookies
    /// and sessions carry over. The copy gets a new id and a new storage path. Refuses while the source runs
    /// (copying a live profile dir would capture a half-written/locked state).
    /// </summary>
    public Profile Clone(string id)
    {
        var src = _repo.GetProfile(id) ?? throw new KeyNotFoundException(id);
        if (IsRunning(id))
            throw new InvalidOperationException("Hãy dừng hồ sơ nguồn trước khi nhân bản (cookie/phiên đang được dùng).");

        var p = new Profile
        {
            Name = $"{src.Name} (copy)",
            GroupId = src.GroupId,
            Proxy = src.Proxy,
            Note = src.Note,
            StartupUrls = src.StartupUrls,
            FingerprintJson = src.FingerprintJson, // verbatim: no regenerate
        };
        p.StoragePath = _paths.ProfileDir(p.Id);
        CopyProfileDir(src.StoragePath, p.StoragePath);
        _repo.UpsertProfile(p);
        return p;
    }

    /// <summary>Recursively copies a profile's user-data-dir, skipping Chrome's Singleton* lock markers
    /// (they point at the source's old pid) and any file locked at copy time.</summary>
    private static void CopyProfileDir(string sourceDir, string destDir)
    {
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) return;
        var root = Path.GetFullPath(sourceDir);
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("Singleton", StringComparison.Ordinal)) continue;
            var rel = Path.GetRelativePath(root, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try { File.Copy(file, dest, overwrite: true); }
            catch (IOException) { /* a locked file is skipped, like backup does */ }
        }
    }

    public void Update(Profile p)
    {
        if (!string.IsNullOrWhiteSpace(p.Proxy) && !ProxyConfig.TryParse(p.Proxy, out _, out var err))
            throw new ArgumentException($"Invalid proxy: {err}");
        _repo.UpsertProfile(p);
    }

    public void Delete(string id)
    {
        if (IsRunning(id)) throw new InvalidOperationException("Stop the profile before deleting it");
        var p = _repo.GetProfile(id);
        _repo.DeleteProfile(id);
        if (p is not null) SafeDeleteProfileDir(p.StoragePath);
    }

    /// <summary>Only ever deletes directories inside our own profiles folder.</summary>
    private void SafeDeleteProfileDir(string dir)
    {
        var full = Path.GetFullPath(dir);
        var root = Path.GetFullPath(_paths.ProfilesDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal) || !Directory.Exists(full)) return;
        try { Directory.Delete(full, recursive: true); }
        catch (IOException e) { Log?.Invoke("", $"Could not delete {full}: {e.Message}"); }
    }

    public void RandomizeFingerprints(IEnumerable<string> ids)
    {
        var coreVersion = NewProfileCoreVersion();
        foreach (var id in ids)
        {
            var p = _repo.GetProfile(id);
            if (p is null) continue;
            var fp = FingerprintGenerator.Regenerate(FingerprintConfig.FromJson(p.FingerprintJson), coreVersion);
            p.FingerprintJson = fp.ToJson();
            _repo.UpsertProfile(p);
        }
    }

    // ---------- cookies (engine-aware) ----------
    /// <summary>Exports cookies for a profile. Camoufox reads <c>cookies.sqlite</c> directly (works while
    /// running); Chromium uses CDP (profile must be running).</summary>
    public async Task<int> ExportCookiesAsync(string profileId, string filePath, CancellationToken ct = default)
    {
        var p = _repo.GetProfile(profileId) ?? throw new KeyNotFoundException($"Profile {profileId} not found");
        if (Settings.Engine == BrowserEngine.Camoufox)
            return await FirefoxCookieService.ExportAsync(p.StoragePath, filePath, ct);
        return await CookieService.ExportAsync(GetRunning(profileId), filePath, ct);
    }

    /// <summary>Imports cookies for a profile. Camoufox writes <c>cookies.sqlite</c> (profile must be CLOSED);
    /// Chromium uses CDP (profile must be running).</summary>
    public async Task<int> ImportCookiesAsync(string profileId, string filePath, CancellationToken ct = default)
    {
        var p = _repo.GetProfile(profileId) ?? throw new KeyNotFoundException($"Profile {profileId} not found");
        if (Settings.Engine == BrowserEngine.Camoufox)
            return await FirefoxCookieService.ImportAsync(p.StoragePath, filePath, ct);
        return await CookieService.ImportAsync(GetRunning(profileId), filePath, ct);
    }

    // ---------- proxy ----------
    public async Task<IpInfo> CheckProxyAsync(string? rawProxy, CancellationToken ct = default)
    {
        var cfg = ProxyConfig.Parse(rawProxy);
        return await new ProxyChecker(Settings.IpLookupUrl).CheckAsync(cfg, ct: ct);
    }

    // ---------- run ----------
    public bool IsRunning(string id) => _running.ContainsKey(id) || _starting.ContainsKey(id);
    public IRunningBrowser? GetRunning(string id) => _running.GetValueOrDefault(id);
    public IReadOnlyCollection<IRunningBrowser> RunningBrowsers => _running.Values.ToList();

    public async Task<IRunningBrowser> StartAsync(string id, CancellationToken ct = default)
    {
        if (_running.TryGetValue(id, out var existing)) return existing;
        if (!_starting.TryAdd(id, 0)) throw new InvalidOperationException("Profile is already starting");
        try
        {
            var p = _repo.GetProfile(id) ?? throw new KeyNotFoundException($"Profile {id} not found");
            StatusChanged?.Invoke(new ProfileStatus(id, ProfileRunState.Starting));

            if (Settings.Engine == BrowserEngine.Camoufox)
                return await StartCamoufoxAsync(p, ct);

            var (core, mode) = ResolveCore();
            var fp = FingerprintConfig.FromJson(p.FingerprintJson);
            if (fp.BrowserMajor != core.Major)
            {
                // Keep UA consistent with the core that actually runs (a mismatch is an easy detection signal).
                Log?.Invoke(id, $"Browser core is {core.Version}; updating UA from Chrome {fp.BrowserMajor}");
                fp = AlignToCore(fp, core);
                p.FingerprintJson = fp.ToJson();
            }

            var proxy = ProxyConfig.Parse(p.Proxy);
            IpInfo? ip = null;
            var needsLookup = fp.TimezoneMode == AutoMode.Auto || fp.LanguageMode == AutoMode.Auto ||
                              fp.GeoMode == AutoMode.Auto ||
                              (fp.WebRtc == WebRtcMode.Altered && string.IsNullOrWhiteSpace(fp.WebRtcPublicIp));
            if (needsLookup && (proxy is not null || Settings.CheckProxyOnStart))
            {
                try
                {
                    ip = await new ProxyChecker(Settings.IpLookupUrl).CheckAsync(proxy, ct: ct);
                    p.ProxyStatus = ip.Summary;
                }
                catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException)
                {
                    // A dead proxy must not silently fall back to a direct connection.
                    if (proxy is not null) throw new LaunchException($"Proxy check failed: {e.Message}", e);
                    Log?.Invoke(id, $"IP lookup failed, using host locale: {e.Message}");
                }
            }

            var urls = p.StartupUrls.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var running = await BrowserLauncher.LaunchAsync(new LaunchRequest
            {
                Profile = p,
                Fingerprint = fp,
                Core = core,
                Mode = mode,
                Proxy = proxy,
                IpInfo = ip,
                StartupUrls = urls,
                ExtensionDirs = ListExtensionDirs(),
            }, ct);

            running.Warning += msg => Log?.Invoke(id, msg);
            running.Exited += OnBrowserExited;
            _running[id] = running;
            p.LastRunAt = DateTime.UtcNow;
            _repo.UpsertProfile(p);
            StatusChanged?.Invoke(new ProfileStatus(id, ProfileRunState.Running, DebuggerAddress: running.DebuggerAddress));
            return running;
        }
        catch (Exception e)
        {
            StatusChanged?.Invoke(new ProfileStatus(id, ProfileRunState.Stopped, e.Message));
            throw;
        }
        finally
        {
            _starting.TryRemove(id, out _);
        }
    }

    public async Task StopAsync(string id)
    {
        if (!_running.TryGetValue(id, out var rb)) return;
        StatusChanged?.Invoke(new ProfileStatus(id, ProfileRunState.Stopping));
        await rb.StopAsync();
    }

    public async Task StopAllAsync()
    {
        await Task.WhenAll(_running.Keys.ToList().Select(StopAsync));
    }

    private void OnBrowserExited(IRunningBrowser rb)
    {
        _running.TryRemove(rb.ProfileId, out _);
        StatusChanged?.Invoke(new ProfileStatus(rb.ProfileId, ProfileRunState.Stopped));
    }

    // ---------- Camoufox engine ----------

    /// <summary>Launches a profile with the Camoufox (Firefox) engine. Generates & stores a stable
    /// fingerprint on first run; applies the proxy's geo via the sidecar's geoip each launch.</summary>
    private async Task<IRunningBrowser> StartCamoufoxAsync(Profile p, CancellationToken ct)
    {
        var id = p.Id;
        var (binary, sidecar) = ResolveCamoufox();
        var fp = FingerprintConfig.FromJson(p.FingerprintJson);
        var os = fp.Os switch
        {
            TargetOs.Windows => "windows",
            TargetOs.MacOs => "macos",
            TargetOs.Linux => "linux",
            _ => "windows",
        };

        // Stable identity: generate once, then reuse the stored config.
        JsonNode? stored = string.IsNullOrWhiteSpace(p.CamoufoxConfigJson) ? null : JsonNode.Parse(p.CamoufoxConfigJson);
        if (stored is not JsonObject config)
        {
            Log?.Invoke(id, $"Generating Camoufox fingerprint ({os})...");
            config = await sidecar.GenerateAsync(os, ct);
            p.CamoufoxConfigJson = config.ToJsonString();
        }

        var proxy = ProxyConfig.Parse(p.Proxy);
        IpInfo? ip = null;
        if (proxy is not null || Settings.CheckProxyOnStart)
        {
            try
            {
                ip = await new ProxyChecker(Settings.IpLookupUrl).CheckAsync(proxy, ct: ct);
                p.ProxyStatus = ip.Summary;
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException)
            {
                if (proxy is not null) throw new LaunchException($"Proxy check failed: {e.Message}", e);
                Log?.Invoke(id, $"IP lookup failed, using host geo: {e.Message}");
            }
        }

        var urls = p.StartupUrls.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var running = await CamoufoxLauncher.LaunchAsync(new CamoufoxLaunchRequest
        {
            Profile = p,
            Config = config,
            Os = os,
            ExecutablePath = binary,
            Sidecar = sidecar,
            Proxy = proxy,
            IpInfo = ip,
            StartupUrls = urls,
        }, ct);

        running.Warning += msg => Log?.Invoke(id, msg);
        running.Exited += OnBrowserExited;
        _running[id] = running;
        p.LastRunAt = DateTime.UtcNow;
        _repo.UpsertProfile(p);
        StatusChanged?.Invoke(new ProfileStatus(id, ProfileRunState.Running));
        return running;
    }

    /// <summary>Resolves the Camoufox binary and the Python sidecar from settings, with sensible fallbacks.</summary>
    private (string Binary, CamoufoxSidecar Sidecar) ResolveCamoufox()
    {
        var binary = Settings.CamoufoxPath;
        if (string.IsNullOrWhiteSpace(binary)) binary = ChromeLocator.DefaultCamoufoxPath() ?? "";
        if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
            throw new LaunchException(
                "Camoufox binary not found. Set it in Settings (CamoufoxPath) or install the camoufox package.");

        // Prefer the frozen standalone sidecar bundled with the app (no Python needed).
        if (string.IsNullOrWhiteSpace(Settings.PythonPath) && string.IsNullOrWhiteSpace(Settings.SidecarPath)
            && ChromeLocator.DefaultSidecarExe() is { } frozen)
            return (binary, CamoufoxSidecar.FromExecutable(frozen));

        var python = string.IsNullOrWhiteSpace(Settings.PythonPath) ? "python3" : Settings.PythonPath;
        var script = Settings.SidecarPath;
        if (string.IsNullOrWhiteSpace(script)) script = ChromeLocator.DefaultSidecarPath() ?? "";
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
            throw new LaunchException(
                "Camoufox sidecar not found. Install the bundled build, or set Python + SidecarPath in Settings.");

        return (binary, new CamoufoxSidecar(python, script));
    }

    private static FingerprintConfig AlignToCore(FingerprintConfig fp, BrowserCore core)
    {
        var next = fp.Clone();
        next.UserAgent = fp.UserAgent.Replace($"Chrome/{fp.BrowserMajor}.0.0.0", $"Chrome/{core.Major}.0.0.0");
        next.BrowserMajor = core.Major;
        next.BrowserFullVersion = core.Version;
        return next;
    }

    /// <summary>Every sub-folder of the extensions dir that contains a manifest is loaded in all profiles.</summary>
    private List<string> ListExtensionDirs() =>
        Directory.Exists(_paths.ExtensionsDir)
            ? Directory.GetDirectories(_paths.ExtensionsDir)
                .Where(d => File.Exists(Path.Combine(d, "manifest.json"))).ToList()
            : [];
}
