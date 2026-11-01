using System.Text.Json;
using System.Text.Json.Nodes;
using GpmLite.Core.Services;
using Xunit;

namespace GpmLite.Core.Tests;

public class FirefoxCookieServiceTests
{
    [Fact]
    public async Task Import_then_export_roundtrips_cookies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpmlite-ffck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var inFile = Path.Combine(dir, "in.json");
        var outFile = Path.Combine(dir, "out.json");
        try
        {
            var cookies = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "sid", ["value"] = "abc123", ["domain"] = ".example.com", ["path"] = "/",
                    ["expires"] = 2000000000, ["secure"] = true, ["httpOnly"] = true, ["sameSite"] = "Lax",
                },
                new JsonObject
                {
                    ["name"] = "theme", ["value"] = "dark", ["domain"] = "example.com", ["path"] = "/",
                    ["expires"] = 2000000000, ["secure"] = false, ["httpOnly"] = false, ["sameSite"] = "None",
                },
            };
            await File.WriteAllTextAsync(inFile, cookies.ToJsonString());

            var imported = await FirefoxCookieService.ImportAsync(dir, inFile);
            Assert.Equal(2, imported);
            Assert.True(File.Exists(Path.Combine(dir, "cookies.sqlite")));

            var exported = await FirefoxCookieService.ExportAsync(dir, outFile);
            Assert.Equal(2, exported);

            var back = JsonNode.Parse(await File.ReadAllTextAsync(outFile))!.AsArray();
            var sid = back.First(c => c!["name"]!.GetValue<string>() == "sid")!.AsObject();
            Assert.Equal("abc123", sid["value"]!.GetValue<string>());
            Assert.Equal(".example.com", sid["domain"]!.GetValue<string>());
            Assert.True(sid["secure"]!.GetValue<bool>());
            Assert.Equal("Lax", sid["sameSite"]!.GetValue<string>());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Import_refuses_when_profile_locked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpmlite-ffck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, ".parentlock"), "");
        await File.WriteAllTextAsync(Path.Combine(dir, "in.json"), "[]");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => FirefoxCookieService.ImportAsync(dir, Path.Combine(dir, "in.json")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
