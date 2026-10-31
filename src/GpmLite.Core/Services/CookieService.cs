using System.Text.Json;
using System.Text.Json.Nodes;
using GpmLite.Core.Browser;

namespace GpmLite.Core.Services;

/// <summary>
/// Cookie import/export for a running profile over CDP. Uses the browser-wide
/// <c>Storage.getCookies</c> / <c>Storage.setCookies</c> commands, so no page needs to be attached.
/// The profile must be running (we talk to its live DevTools endpoint).
/// </summary>
public static class CookieService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>Exports every cookie from a running profile to <paramref name="filePath"/> as a JSON array.
    /// Returns the number of cookies written.</summary>
    public static async Task<int> ExportAsync(RunningBrowser? browser, string filePath, CancellationToken ct = default)
    {
        EnsureRunning(browser);
        var result = await browser!.SendCdpAsync("Storage.getCookies", null, ct);
        var cookies = result["cookies"] as JsonArray ?? new JsonArray();
        var json = cookies.ToJsonString(WriteOpts);
        await File.WriteAllTextAsync(filePath, json, ct);
        return cookies.Count;
    }

    /// <summary>Imports cookies from a JSON file (a bare array, or an object with a <c>cookies</c> array)
    /// into a running profile. Returns the number of cookies sent.</summary>
    public static async Task<int> ImportAsync(RunningBrowser? browser, string filePath, CancellationToken ct = default)
    {
        EnsureRunning(browser);
        var text = await File.ReadAllTextAsync(filePath, ct);
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException e) { throw new FormatException($"File cookie không hợp lệ: {e.Message}"); }

        var cookies = node as JsonArray ?? node?["cookies"] as JsonArray
            ?? throw new FormatException("File cookie phải là một mảng JSON hoặc có trường \"cookies\".");
        if (cookies.Count == 0) return 0;

        // DeepClone detaches the nodes from the parsed document so they can be re-parented into the params.
        await browser!.SendCdpAsync("Storage.setCookies",
            new JsonObject { ["cookies"] = cookies.DeepClone() }, ct);
        return cookies.Count;
    }

    private static void EnsureRunning(RunningBrowser? browser)
    {
        if (browser is null || !browser.IsRunning)
            throw new InvalidOperationException("Hồ sơ phải đang chạy để nhập/xuất cookie.");
    }
}
