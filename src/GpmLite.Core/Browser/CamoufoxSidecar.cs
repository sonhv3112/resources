using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GpmLite.Core.Browser;

/// <summary>
/// Bridge to the Python <c>camoufox_sidecar.py</c>, which uses Camoufox's own (BrowserForge-backed)
/// fingerprint generator. The app stores the <c>config</c> that <see cref="GenerateAsync"/> returns so
/// each profile keeps a stable identity, then calls <see cref="SpecAsync"/> at launch to get the concrete
/// executable/args/env for that identity with the current proxy geo applied.
/// </summary>
public sealed class CamoufoxSidecar
{
    /// <summary>The executable to run: a Python interpreter (with <see cref="ScriptPath"/> set) or a frozen
    /// standalone sidecar binary (with <see cref="ScriptPath"/> null).</summary>
    public string PythonPath { get; }
    /// <summary>The sidecar .py script, or null when <see cref="PythonPath"/> is a frozen sidecar binary.</summary>
    public string? ScriptPath { get; }

    /// <summary>Dev mode: run <c>python script.py &lt;command&gt;</c>.</summary>
    public CamoufoxSidecar(string pythonPath, string scriptPath)
    {
        PythonPath = pythonPath;
        ScriptPath = scriptPath;
    }

    private CamoufoxSidecar(string sidecarExe)
    {
        PythonPath = sidecarExe;
        ScriptPath = null;
    }

    /// <summary>Packaged mode: run the frozen standalone sidecar binary directly (<c>sidecar &lt;command&gt;</c>).</summary>
    public static CamoufoxSidecar FromExecutable(string sidecarExe) => new(sidecarExe);

    /// <summary>Installed Camoufox version, or throws if the sidecar/Python/package isn't ready.</summary>
    public async Task<string> VersionAsync(CancellationToken ct = default)
    {
        var r = await RunAsync("version", new JsonObject(), ct);
        return r["camoufox"]?.GetValue<string>() ?? "unknown";
    }

    /// <summary>Generate a fresh, coherent fingerprint config for an OS. Store the returned object per profile.</summary>
    public async Task<JsonObject> GenerateAsync(string os, CancellationToken ct = default)
    {
        var req = new JsonObject { ["os"] = os };
        var r = await RunAsync("generate", req, ct);
        return r["config"] as JsonObject ?? throw new LaunchException("Camoufox sidecar returned no config");
    }

    /// <summary>Build a launch spec: pins <paramref name="config"/> and applies <paramref name="geoip"/>
    /// (a proxy exit IP, or <c>true</c> to geolocate the current connection). The executable to launch is the
    /// app's own binary — not forwarded here, so Camoufox validates the config against its own metadata.</summary>
    public async Task<CamoufoxSpec> SpecAsync(string os, JsonObject config, string? executablePath,
        object? geoip, bool headless, CancellationToken ct = default)
    {
        var req = new JsonObject
        {
            ["os"] = os,
            ["config"] = config.DeepClone(),
            ["headless"] = headless,
        };
        if (geoip is string ip) req["geoip"] = ip;
        else if (geoip is bool b && b) req["geoip"] = true;

        var r = await RunAsync("spec", req, ct);
        var env = new Dictionary<string, string>();
        if (r["env"] is JsonObject eo)
            foreach (var kv in eo)
                if (kv.Value is not null) env[kv.Key] = kv.Value.GetValue<string>();

        var prefs = new Dictionary<string, JsonNode?>();
        if (r["user_prefs"] is JsonObject po)
            foreach (var kv in po) prefs[kv.Key] = kv.Value?.DeepClone();

        var args = new List<string>();
        if (r["args"] is JsonArray aa)
            foreach (var a in aa) if (a is not null) args.Add(a.GetValue<string>());

        return new CamoufoxSpec(
            r["executable_path"]?.GetValue<string>() ?? executablePath ?? "",
            args, env, prefs);
    }

    private async Task<JsonObject> RunAsync(string command, JsonObject request, CancellationToken ct)
    {
        if (!File.Exists(PythonPath))
            throw new LaunchException($"Camoufox sidecar executable not found at {PythonPath}");
        if (ScriptPath is not null && !File.Exists(ScriptPath))
            throw new LaunchException($"Camoufox sidecar script not found at {ScriptPath}");
        var psi = new ProcessStartInfo(PythonPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (ScriptPath is not null) psi.ArgumentList.Add(ScriptPath); // python mode; frozen binary takes none
        psi.ArgumentList.Add(command);
        // Request comes over stdin to avoid argv length limits on large configs.

        using var proc = Process.Start(psi)
            ?? throw new LaunchException($"Could not start Python sidecar ({PythonPath})");
        await proc.StandardInput.WriteAsync(request.ToJsonString());
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Parse every JSON-object line the sidecar printed. A frozen (PyInstaller) sidecar can emit
        // extra stray lines, so don't just trust the last one: take all valid {"ok":...} objects and
        // prefer a successful one; fall back to the last object (which carries a real error message).
        var objects = new List<JsonObject>();
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!raw.StartsWith('{')) continue;
            try { if (JsonNode.Parse(raw) is JsonObject o && o["ok"] is not null) objects.Add(o); }
            catch (JsonException) { /* skip stray non-JSON output */ }
        }
        if (objects.Count == 0)
            throw new LaunchException($"Camoufox sidecar produced no JSON (exit {proc.ExitCode}): {stderr.Trim()}");

        var obj = objects.FirstOrDefault(o => o["ok"]?.GetValue<bool>() == true) ?? objects[^1];
        if (obj["ok"]?.GetValue<bool>() != true)
        {
            var err = obj["error"]?.GetValue<string>() ?? "unknown";
            var trace = obj["trace"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(stderr)) err += $"\n--- sidecar stderr ---\n{stderr.Trim()}";
            if (!string.IsNullOrWhiteSpace(trace)) err += $"\n--- sidecar trace ---\n{trace.Trim()}";
            throw new LaunchException($"Camoufox sidecar error: {err}");
        }
        return obj;
    }
}

/// <summary>The concrete launch inputs the sidecar resolved for one profile.</summary>
public sealed record CamoufoxSpec(
    string ExecutablePath,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, JsonNode?> UserPrefs);
