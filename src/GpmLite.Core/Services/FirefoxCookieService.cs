using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace GpmLite.Core.Services;

/// <summary>
/// Cookie import/export for the Camoufox (Firefox) engine. Unlike <see cref="CookieService"/>,
/// which talks to a running Chromium profile over CDP, this reads and writes the Firefox
/// <c>cookies.sqlite</c> file (table <c>moz_cookies</c>) directly.
/// The JSON shape is kept interoperable with the Chromium cookie JSON so profiles can be
/// migrated between engines.
/// </summary>
public static class FirefoxCookieService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// The standard Firefox <c>moz_cookies</c> schema. Used verbatim so a freshly created file is
    /// valid for the browser to open.
    /// </summary>
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS moz_cookies(
          id INTEGER PRIMARY KEY,
          originAttributes TEXT NOT NULL DEFAULT '',
          name TEXT, value TEXT, host TEXT, path TEXT,
          expiry INTEGER, lastAccessed INTEGER, creationTime INTEGER,
          isSecure INTEGER, isHttpOnly INTEGER, inBrowserElement INTEGER DEFAULT 0,
          sameSite INTEGER DEFAULT 0, rawSameSite INTEGER DEFAULT 0, schemeMap INTEGER DEFAULT 0,
          isPartitionedAttributeSet INTEGER DEFAULT 0,
          CONSTRAINT moz_uniqueid UNIQUE (name, host, path, originAttributes)
        );
        """;

    /// <summary>
    /// Exports every cookie from <c>&lt;profileDir&gt;/cookies.sqlite</c> to <paramref name="filePath"/>
    /// as a JSON array (Chromium-compatible shape). Returns the number of cookies written.
    /// Does not require the browser to be stopped.
    /// </summary>
    public static async Task<int> ExportAsync(string profileDir, string filePath, CancellationToken ct = default)
    {
        var dbPath = Path.Combine(profileDir, "cookies.sqlite");
        if (!File.Exists(dbPath))
            throw new InvalidOperationException("Hồ sơ chưa có cookie để xuất.");

        var cookies = new JsonArray();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        await using (var c = new SqliteConnection(connectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT name, value, host, path, expiry, isSecure, isHttpOnly, sameSite
                FROM moz_cookies
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                cookies.Add(new JsonObject
                {
                    ["name"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    ["value"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ["domain"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    ["path"] = reader.IsDBNull(3) ? "/" : reader.GetString(3),
                    ["expires"] = reader.IsDBNull(4) ? 0L : reader.GetInt64(4),
                    ["secure"] = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                    ["httpOnly"] = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                    ["sameSite"] = SameSiteToString(reader.IsDBNull(7) ? 0 : reader.GetInt64(7)),
                });
            }
        }

        await File.WriteAllTextAsync(filePath, cookies.ToJsonString(WriteOpts), ct);
        return cookies.Count;
    }

    /// <summary>
    /// Imports cookies from a JSON file (a bare array, or an object with a <c>cookies</c> array)
    /// into <c>&lt;profileDir&gt;/cookies.sqlite</c>, upserting into <c>moz_cookies</c>.
    /// Creates the file and table if missing. Returns the number of cookies imported.
    /// Refuses to run while the profile is open (detected via lock files).
    /// </summary>
    public static async Task<int> ImportAsync(string profileDir, string filePath, CancellationToken ct = default)
    {
        if (File.Exists(Path.Combine(profileDir, ".parentlock")) ||
            File.Exists(Path.Combine(profileDir, "lock")))
            throw new InvalidOperationException("Hãy đóng hồ sơ trước khi nhập cookie.");

        var text = await File.ReadAllTextAsync(filePath, ct);
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException e) { throw new FormatException($"File cookie không hợp lệ: {e.Message}"); }

        var cookies = node as JsonArray ?? node?["cookies"] as JsonArray
            ?? throw new FormatException("File cookie phải là một mảng JSON hoặc có trường \"cookies\".");
        if (cookies.Count == 0) return 0;

        Directory.CreateDirectory(profileDir);
        var dbPath = Path.Combine(profileDir, "cookies.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        var nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        var count = 0;

        await using var c = new SqliteConnection(connectionString);
        await c.OpenAsync(ct);

        await using (var create = c.CreateCommand())
        {
            create.CommandText = CreateTableSql;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO moz_cookies
                (name, value, host, path, expiry, lastAccessed, creationTime,
                 isSecure, isHttpOnly, inBrowserElement, sameSite, rawSameSite,
                 schemeMap, originAttributes)
            VALUES
                (@name, @value, @host, @path, @expiry, @lastAccessed, @creationTime,
                 @isSecure, @isHttpOnly, 0, @sameSite, @sameSite,
                 0, '')
            """;
        var pName = cmd.Parameters.Add("@name", SqliteType.Text);
        var pValue = cmd.Parameters.Add("@value", SqliteType.Text);
        var pHost = cmd.Parameters.Add("@host", SqliteType.Text);
        var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
        var pExpiry = cmd.Parameters.Add("@expiry", SqliteType.Integer);
        var pLastAccessed = cmd.Parameters.Add("@lastAccessed", SqliteType.Integer);
        var pCreationTime = cmd.Parameters.Add("@creationTime", SqliteType.Integer);
        var pIsSecure = cmd.Parameters.Add("@isSecure", SqliteType.Integer);
        var pIsHttpOnly = cmd.Parameters.Add("@isHttpOnly", SqliteType.Integer);
        var pSameSite = cmd.Parameters.Add("@sameSite", SqliteType.Integer);

        foreach (var item in cookies)
        {
            ct.ThrowIfCancellationRequested();
            if (item is not JsonObject obj) continue;

            var host = GetString(obj, "domain") ?? GetString(obj, "host") ?? "";
            if (host.Length == 0) continue;

            pName.Value = GetString(obj, "name") ?? "";
            pValue.Value = GetString(obj, "value") ?? "";
            pHost.Value = host;
            pPath.Value = GetString(obj, "path") ?? "/";
            pExpiry.Value = GetExpiry(obj);
            pLastAccessed.Value = nowMicros;
            pCreationTime.Value = nowMicros;
            pIsSecure.Value = GetBool(obj, "secure") ? 1 : 0;
            pIsHttpOnly.Value = GetBool(obj, "httpOnly") ? 1 : 0;
            pSameSite.Value = SameSiteToInt(GetString(obj, "sameSite"));

            await cmd.ExecuteNonQueryAsync(ct);
            count++;
        }

        await tx.CommitAsync(ct);
        return count;
    }

    /// <summary>Maps a Firefox <c>sameSite</c> integer to a Chromium-style string.</summary>
    private static string SameSiteToString(long value) => value switch
    {
        1 => "Lax",
        2 => "Strict",
        _ => "None",
    };

    /// <summary>
    /// Maps a Chromium-style <c>sameSite</c> string to the Firefox integer
    /// (0=None, 1=Lax, 2=Strict). Unknown / unspecified values default to 0.
    /// </summary>
    private static int SameSiteToInt(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "lax" => 1,
        "strict" => 2,
        "none" or "no_restriction" or "unspecified" => 0,
        _ => 0,
    };

    private static string? GetString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var v) && v is not null ? v.GetValue<JsonElement>().ToString() : null;

    private static bool GetBool(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var v) || v is null) return false;
        var el = v.GetValue<JsonElement>();
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetInt64(out var n) && n != 0,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) && b,
            _ => false,
        };
    }

    /// <summary>Reads the <c>expires</c> field as unix seconds, tolerating numeric or string encodings.</summary>
    private static long GetExpiry(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("expires", out var v) || v is null) return 0;
        var el = v.GetValue<JsonElement>();
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var n) => n,
            JsonValueKind.Number when el.TryGetDouble(out var d) => (long)d,
            JsonValueKind.String when long.TryParse(el.GetString(), out var s) => s,
            _ => 0,
        };
    }
}
