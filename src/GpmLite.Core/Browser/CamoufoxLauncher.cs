using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Browser;

public sealed class CamoufoxLaunchRequest
{
    public required Profile Profile { get; init; }
    /// <summary>Stored, stable Camoufox fingerprint config (from <see cref="CamoufoxSidecar.GenerateAsync"/>).</summary>
    public required JsonObject Config { get; init; }
    /// <summary>"windows" | "macos" | "linux".</summary>
    public required string Os { get; init; }
    /// <summary>Camoufox (Firefox) executable path.</summary>
    public required string ExecutablePath { get; init; }
    public required CamoufoxSidecar Sidecar { get; init; }
    public ProxyConfig? Proxy { get; init; }
    /// <summary>Proxy check result; its IP drives the geo/timezone/locale (geoip).</summary>
    public IpInfo? IpInfo { get; init; }
    public IReadOnlyList<string> StartupUrls { get; init; } = [];
    public bool Headless { get; init; }
    /// <summary>When set (tiling), the window opens at this position/size instead of auto-fitting.</summary>
    public WindowRect? WindowRect { get; init; }
}

/// <summary>A window position and size in logical screen pixels (for tiling profiles into a grid).</summary>
public readonly record struct WindowRect(int X, int Y, int W, int H);

/// <summary>
/// Launches Camoufox (patched Firefox) for a profile. The fingerprint is applied entirely by the
/// <c>CAMOU_CONFIG</c> env the sidecar builds — no CDP, no in-page script. The proxy goes through the
/// same <see cref="ProxyRelay"/> the Chromium path uses, wired in via Firefox proxy prefs.
/// </summary>
public static class CamoufoxLauncher
{
    public static async Task<CamoufoxBrowser> LaunchAsync(CamoufoxLaunchRequest req, CancellationToken ct = default)
    {
        Directory.CreateDirectory(req.Profile.StoragePath);
        ProxyRelay? relay = req.Proxy is null ? null : new ProxyRelay(req.Proxy);
        Process? proc = null;
        try
        {
            // Geo/timezone/locale MUST follow the exit IP, never the host clock. Prefer the exit IP we
            // already checked (in-app proxy); otherwise pass geoip=true so Camoufox looks it up through the
            // current network — which is the system VPN (e.g. HMA) when one is active. This prevents the
            // host timezone from leaking when there is no in-app proxy.
            object geoip = req.IpInfo?.Ip is { Length: > 0 } ip ? ip : true;
            // A tiny per-profile extension prefixes every tab's title with the profile name
            // ("Profile 1 | <page title>"), so each window is identifiable at the tab bar. Camoufox blocks
            // userChrome UI edits, but content scripts run — verified to prepend the title reliably.
            var labelExt = WriteLabelExtension(req.Profile.StoragePath, req.Profile.Name);
            var spec = await req.Sidecar.SpecAsync(req.Os, req.Config, req.ExecutablePath, geoip,
                req.Headless, req.WindowRect, new[] { labelExt }, ct);

            WriteUserJs(req.Profile.StoragePath, spec.UserPrefs, relay);

            // Always launch the app's configured/bundled binary; the sidecar only supplies the fingerprint env.
            var psi = new ProcessStartInfo(req.ExecutablePath) { UseShellExecute = false };
            foreach (var kv in spec.Env) psi.Environment[kv.Key] = kv.Value;
            foreach (var a in spec.Args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-no-remote");
            psi.ArgumentList.Add("-profile");
            psi.ArgumentList.Add(req.Profile.StoragePath);
            if (req.Headless) psi.ArgumentList.Add("-headless");

            // First tab = the profile "name card" (file:// page titled with the profile name) so the window
            // is identifiable immediately, even before navigating. Camoufox doesn't honor newtab overrides
            // and content scripts don't run on about:newtab, so a real file:// tab is the reliable way.
            // Real pages opened afterwards get the "Profile — title" prefix via the content-script extension.
            var urls = new List<string> { new Uri(Path.Combine(labelExt, "newtab.html")).AbsoluteUri };
            urls.AddRange(req.StartupUrls.Where(u => !string.IsNullOrWhiteSpace(u)));
            psi.ArgumentList.Add(urls[0]);
            foreach (var u in urls.Skip(1)) { psi.ArgumentList.Add("-new-tab"); psi.ArgumentList.Add(u); }

            proc = Process.Start(psi) ?? throw new LaunchException("Could not start Camoufox");

            // Firefox has no DevTools-port handshake; just confirm it didn't immediately fail to launch.
            await Task.Delay(400, ct);
            if (proc.HasExited)
                throw new LaunchException(
                    $"Camoufox exited during startup (code {proc.ExitCode}). Is this profile already open?");

            return new CamoufoxBrowser(req.Profile.Id, proc, relay);
        }
        catch (Exception e)
        {
            if (proc is { HasExited: false }) { try { proc.Kill(entireProcessTree: true); } catch { } }
            proc?.Dispose();
            if (relay is not null) await relay.DisposeAsync();
            throw e as LaunchException ?? new LaunchException($"Camoufox launch failed: {e.Message}", e);
        }
    }

    private static void WriteUserJs(string profileDir, IReadOnlyDictionary<string, JsonNode?> prefs, ProxyRelay? relay)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Generated by GpmLite — do not edit.");
        foreach (var kv in prefs)
            sb.AppendLine($"user_pref(\"{kv.Key}\", {kv.Value?.ToJsonString() ?? "null"});");

        // No "import from another browser" / default-browser prompts.
        sb.AppendLine("user_pref(\"browser.shell.checkDefaultBrowser\", false);");
        sb.AppendLine("user_pref(\"datareporting.policy.dataSubmissionEnabled\", false);");
        // Camoufox's bundled Firefox-UI-Fix theme renders the new-tab "+" as an empty tab-shaped button,
        // which users miss. Render it as the standard proton "+" button instead.
        sb.AppendLine("user_pref(\"userChrome.tab.newtab_button_like_tab\", false);");
        sb.AppendLine("user_pref(\"userChrome.tab.newtab_button_proton\", true);");

        if (relay is not null)
        {
            var (host, port) = SplitAddress(relay.Address);
            sb.AppendLine("user_pref(\"network.proxy.type\", 1);");          // manual
            sb.AppendLine($"user_pref(\"network.proxy.http\", \"{host}\");");
            sb.AppendLine($"user_pref(\"network.proxy.http_port\", {port});");
            sb.AppendLine($"user_pref(\"network.proxy.ssl\", \"{host}\");");
            sb.AppendLine($"user_pref(\"network.proxy.ssl_port\", {port});");
            sb.AppendLine("user_pref(\"network.proxy.share_proxy_settings\", true);");
            sb.AppendLine("user_pref(\"network.proxy.socks_remote_dns\", true);");
            // Keep localhost direct so our relay (127.0.0.1) is reachable.
            sb.AppendLine("user_pref(\"network.proxy.allow_hijacking_localhost\", true);");
            sb.AppendLine("user_pref(\"network.proxy.no_proxies_on\", \"\");");
        }
        else
        {
            sb.AppendLine("user_pref(\"network.proxy.type\", 0);"); // direct
        }

        File.WriteAllText(Path.Combine(profileDir, "user.js"), sb.ToString());
    }

    /// <summary>Writes a tiny unpacked WebExtension that prefixes every tab's &lt;title&gt; with the profile
    /// name, and returns its directory (passed to Camoufox as an extra addon). Content scripts run per tab,
    /// so when one tab closes the others keep their own "Profile — title". Verified to work on Camoufox.</summary>
    private static string WriteLabelExtension(string profileDir, string name)
    {
        var display = string.IsNullOrWhiteSpace(name) ? "Profile" : name;
        var dir = Path.Combine(profileDir, "gpmlite-label");
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"manifest_version\":2,\"name\":\"GpmLite Profile Label\",\"version\":\"1.0\"," +
            "\"browser_specific_settings\":{\"gecko\":{\"id\":\"label@gpmlite.local\"}}," +
            "\"chrome_url_overrides\":{\"newtab\":\"newtab.html\"}," +
            // (Default search engine = Google is set via Camoufox's policies.json, not here.)
            "\"content_scripts\":[{\"matches\":[\"<all_urls>\"],\"js\":[\"label.js\"]," +
            "\"run_at\":\"document_start\",\"all_frames\":false}]}");

        // Custom New Tab page so even an empty tab shows the profile name (content scripts can't touch
        // the built-in about:newtab). Its <title> is the profile name; the page shows it big and colored.
        var safe = System.Net.WebUtility.HtmlEncode(display);
        var hue = Math.Abs(display.Aggregate(17, (h, c) => unchecked(h * 31 + c))) % 360;
        File.WriteAllText(Path.Combine(dir, "newtab.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            $"<title>{safe}</title>" +
            "<style>html,body{margin:0;height:100%}body{display:flex;align-items:center;justify-content:center;" +
            $"background:hsl({hue},45%,16%);color:#fff;font-family:-apple-system,'Segoe UI',system-ui,sans-serif}}" +
            $".n{{font-size:min(10vw,84px);font-weight:800;letter-spacing:1px;opacity:.9}}</style></head>" +
            $"<body><div class=\"n\">{safe}</div></body></html>");

        // JSON-encode the name so it is a safe JS string literal (handles quotes, backslashes, unicode).
        var jsName = JsonSerializer.Serialize(display);
        var js =
            $"const P = {jsName} + \" | \";\n" +
            "function apply(){try{var t=document.title||\"\";" +
            "if(t===P.slice(0,-3)) return;" +
            "if(!t.startsWith(P)) document.title = P + t;}catch(e){}}\n" +
            "apply();\n" +
            "document.addEventListener('DOMContentLoaded', apply);\n" +
            "try{new MutationObserver(apply).observe(document.documentElement,{subtree:true,childList:true,characterData:true});}catch(e){}\n";
        File.WriteAllText(Path.Combine(dir, "label.js"), js);
        return dir;
    }

    private static (string Host, int Port) SplitAddress(string address)
    {
        var i = address.LastIndexOf(':');
        return i < 0 ? (address, 0) : (address[..i], int.TryParse(address[(i + 1)..], out var p) ? p : 0);
    }
}
