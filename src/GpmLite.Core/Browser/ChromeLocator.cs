using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GpmLite.Core.Browser;

public sealed record BrowserCore(string ExecutablePath, string Version)
{
    public int Major => int.TryParse(Version.Split('.')[0], out var m) ? m : 0;
    public override string ToString() => $"Chrome {Version}";
}

/// <summary>How a launch applies the fingerprint: via CDP/JS injection (stock Chrome) or native
/// command-line switches read by the patched "GpmLite core" Chromium.</summary>
public enum BrowserCoreMode { Cdp, Native }

public static partial class ChromeLocator
{
    public static IEnumerable<string> CandidatePaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var root in new[] { "/Applications", Path.Combine(home, "Applications") })
            {
                yield return Path.Combine(root, "Google Chrome.app/Contents/MacOS/Google Chrome");
                yield return Path.Combine(root, "Chromium.app/Contents/MacOS/Chromium");
                yield return Path.Combine(root, "Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "LocalAppData" })
            {
                var root = Environment.GetEnvironmentVariable(env);
                if (root is not null) yield return Path.Combine(root, @"Google\Chrome\Application\chrome.exe");
            }
        }
        else
        {
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/google-chrome-stable";
            yield return "/usr/bin/chromium";
            yield return "/usr/bin/chromium-browser";
        }
    }

    /// <summary>Path to the patched GpmLite core shipped inside the app bundle (next to the executable),
    /// or null when none is bundled. macOS: <c>Contents/Resources/core/Chromium.app/.../Chromium</c>
    /// (the app runs from <c>Contents/MacOS</c>). Windows/Linux: a <c>core/</c> folder beside the exe.</summary>
    public static string? BundledCorePath()
    {
        var baseDir = AppContext.BaseDirectory;
        string candidate;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            candidate = Path.GetFullPath(Path.Combine(
                baseDir, "..", "Resources", "core", "Chromium.app", "Contents", "MacOS", "Chromium"));
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            candidate = Path.GetFullPath(Path.Combine(baseDir, "core", "chrome.exe"));
        else
            candidate = Path.GetFullPath(Path.Combine(baseDir, "core", "chrome"));

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Default Camoufox (Firefox) executable: a <c>camoufox/</c> folder bundled beside the app, else
    /// the camoufox pip cache. Null when none is found.</summary>
    public static string? DefaultCamoufoxPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add(Path.Combine(baseDir, "..", "Resources", "camoufox", "Camoufox.app", "Contents", "MacOS", "camoufox"));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, "Library", "Caches", "camoufox", "Camoufox.app", "Contents", "MacOS", "camoufox"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add(Path.Combine(baseDir, "camoufox", "camoufox.exe"));
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (local is not null) candidates.Add(Path.Combine(local, "camoufox", "camoufox", "camoufox.exe"));
        }
        else
        {
            candidates.Add(Path.Combine(baseDir, "camoufox", "camoufox"));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".cache", "camoufox", "camoufox"));
        }
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>Default path to the frozen standalone Camoufox sidecar binary bundled with the app
    /// (no Python needed). Null when not bundled (dev builds use <see cref="DefaultSidecarPath"/> + Python).</summary>
    public static string? DefaultSidecarExe()
    {
        var baseDir = AppContext.BaseDirectory;
        const string dirName = "gpmlite-camoufox-sidecar";
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? dirName + ".exe" : dirName;
        var candidates = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            candidates.Add(Path.Combine(baseDir, "..", "Resources", "sidecar", dirName, exe));
        candidates.Add(Path.Combine(baseDir, "sidecar", dirName, exe));
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>Default path to the Python sidecar script: <c>sidecar/camoufox_sidecar.py</c> beside the app,
    /// or walking up to the repo's <c>gpm-lite/sidecar/</c> in a dev build. Null when not found.</summary>
    public static string? DefaultSidecarPath()
    {
        const string rel = "sidecar/camoufox_sidecar.py";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Finds the first installed Chrome/Chromium, or the explicitly configured one.</summary>
    public static BrowserCore? Find(string? preferredPath = null)
    {
        var paths = string.IsNullOrWhiteSpace(preferredPath) ? CandidatePaths() : [preferredPath];
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var v = ReadVersion(p);
            if (v is not null) return new BrowserCore(p, v);
        }
        return null;
    }

    public static string? ReadVersion(string executablePath)
    {
        // macOS: read the bundle's Info.plist instead of starting the browser.
        var plist = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executablePath)!, "..", "Info.plist"));
        if (File.Exists(plist))
        {
            var text = File.ReadAllText(plist);
            var m = PlistVersion().Match(text);
            if (m.Success) return m.Groups[1].Value;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo(executablePath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            var m = VersionNumber().Match(output);
            return m.Success ? m.Value : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([\d.]+)</string>")]
    private static partial Regex PlistVersion();

    [GeneratedRegex(@"\d+\.\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();
}
