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
