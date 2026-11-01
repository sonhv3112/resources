using System.Runtime.InteropServices;
using Dapper;
using GpmLite.Core.Models;
using Microsoft.Data.Sqlite;

namespace GpmLite.Core.Storage;

public sealed class AppPaths
{
    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "gpmlite.db");
    public string ProfilesDir => Path.Combine(Root, "profiles");
    public string ExtensionsDir => Path.Combine(Root, "extensions");
    /// <summary>Folder for custom rotating-proxy provider manifests (*.json). See <c>RouteProxyService</c>.</summary>
    public string RouteProvidersDir => Path.Combine(Root, "route-providers");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    /// <summary>Diagnostic log (launch errors etc.). Shown to users who ask "where are the logs".</summary>
    public string LogFile => Path.Combine(Root, "gpmlite.log");

    public AppPaths(string? root = null)
    {
        Root = root ?? DefaultRoot();
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(ExtensionsDir);
        Directory.CreateDirectory(RouteProvidersDir);
    }

    public static string DefaultRoot()
    {
        // Lets tests and portable setups keep data elsewhere.
        if (Environment.GetEnvironmentVariable("GPMLITE_HOME") is { Length: > 0 } custom) return custom;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "GpmLite");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpmLite");
    }

    public string ProfileDir(string profileId) => Path.Combine(ProfilesDir, profileId);
}

public sealed class Database
{
    private readonly string _connectionString;

    static Database()
    {
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
        SqlMapper.AddTypeHandler(new NullableUtcDateTimeHandler());
    }

    public Database(string file)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, ForeignKeys = true }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        var version = c.ExecuteScalar<long>("PRAGMA user_version;");
        if (version < 1)
        {
            using var tx = c.BeginTransaction();
            c.Execute("""
                CREATE TABLE groups (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    sort_order INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE profiles (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    group_id TEXT REFERENCES groups(id) ON DELETE SET NULL,
                    proxy TEXT NOT NULL DEFAULT '',
                    note TEXT NOT NULL DEFAULT '',
                    startup_urls TEXT NOT NULL DEFAULT '',
                    fingerprint_json TEXT NOT NULL,
                    storage_path TEXT NOT NULL,
                    proxy_status TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_run_at TEXT
                );
                CREATE INDEX ix_profiles_group ON profiles(group_id);
                CREATE TABLE tags (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    color TEXT NOT NULL
                );
                CREATE TABLE profile_tags (
                    profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
                    tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                    PRIMARY KEY (profile_id, tag_id)
                );
                CREATE TABLE proxies (
                    id TEXT PRIMARY KEY,
                    raw TEXT NOT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    last_check TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );
                PRAGMA user_version = 1;
                """, transaction: tx);
            tx.Commit();
        }
        if (version < 2)
        {
            using var tx = c.BeginTransaction();
            // Last concrete proxy a route:... entry resolved to (for display / quick reuse).
            c.Execute("ALTER TABLE proxies ADD COLUMN route_resolved TEXT NOT NULL DEFAULT '';", transaction: tx);
            c.Execute("PRAGMA user_version = 2;", transaction: tx);
            tx.Commit();
        }
        if (version < 3)
        {
            using var tx = c.BeginTransaction();
            // Stored Camoufox fingerprint config (stable identity) for the Camoufox engine.
            c.Execute("ALTER TABLE profiles ADD COLUMN camoufox_config_json TEXT NOT NULL DEFAULT '';", transaction: tx);
            c.Execute("PRAGMA user_version = 3;", transaction: tx);
            tx.Commit();
        }
    }

    private sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(System.Data.IDbDataParameter p, DateTime v) => p.Value = v.ToUniversalTime().ToString("O");
        public override DateTime Parse(object v) => DateTime.Parse((string)v, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private sealed class NullableUtcDateTimeHandler : SqlMapper.TypeHandler<DateTime?>
    {
        public override void SetValue(System.Data.IDbDataParameter p, DateTime? v) =>
            p.Value = v is null ? DBNull.Value : v.Value.ToUniversalTime().ToString("O");
        public override DateTime? Parse(object v) =>
            v is null or DBNull ? null : DateTime.Parse((string)v, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }
}

public sealed class Repository(Database db)
{
    private const string ProfileCols = """
        id AS Id, name AS Name, group_id AS GroupId, proxy AS Proxy, note AS Note, startup_urls AS StartupUrls,
        fingerprint_json AS FingerprintJson, camoufox_config_json AS CamoufoxConfigJson,
        storage_path AS StoragePath, proxy_status AS ProxyStatus,
        created_at AS CreatedAt, updated_at AS UpdatedAt, last_run_at AS LastRunAt
        """;

    // ---- profiles ----
    public List<Profile> ListProfiles(string? groupId = null, string? search = null)
    {
        using var c = db.Open();
        var sql = $"SELECT {ProfileCols} FROM profiles WHERE 1=1";
        if (groupId is not null) sql += " AND group_id = @groupId";
        if (!string.IsNullOrWhiteSpace(search)) sql += " AND (name LIKE @q OR note LIKE @q OR proxy LIKE @q)";
        sql += " ORDER BY created_at DESC";
        return c.Query<Profile>(sql, new { groupId, q = $"%{search}%" }).ToList();
    }

    public Profile? GetProfile(string id)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Profile>($"SELECT {ProfileCols} FROM profiles WHERE id = @id", new { id });
    }

    public void UpsertProfile(Profile p)
    {
        p.UpdatedAt = DateTime.UtcNow;
        using var c = db.Open();
        c.Execute("""
            INSERT INTO profiles (id, name, group_id, proxy, note, startup_urls, fingerprint_json, camoufox_config_json,
                                  storage_path, proxy_status, created_at, updated_at, last_run_at)
            VALUES (@Id, @Name, @GroupId, @Proxy, @Note, @StartupUrls, @FingerprintJson, @CamoufoxConfigJson,
                    @StoragePath, @ProxyStatus, @CreatedAt, @UpdatedAt, @LastRunAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, group_id = excluded.group_id, proxy = excluded.proxy, note = excluded.note,
                startup_urls = excluded.startup_urls, fingerprint_json = excluded.fingerprint_json,
                camoufox_config_json = excluded.camoufox_config_json,
                storage_path = excluded.storage_path, proxy_status = excluded.proxy_status,
                updated_at = excluded.updated_at, last_run_at = excluded.last_run_at
            """, p);
    }

    public void DeleteProfile(string id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM profiles WHERE id = @id", new { id });
    }

    public int CountProfiles(string? groupId = null)
    {
        using var c = db.Open();
        return groupId is null
            ? c.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles")
            : c.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE group_id = @groupId", new { groupId });
    }

    // ---- groups ----
    public List<Group> ListGroups()
    {
        using var c = db.Open();
        return c.Query<Group>("SELECT id AS Id, name AS Name, sort_order AS SortOrder FROM groups ORDER BY sort_order, name").ToList();
    }

    public void UpsertGroup(Group g)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO groups (id, name, sort_order) VALUES (@Id, @Name, @SortOrder)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, sort_order = excluded.sort_order
            """, g);
    }

    public void DeleteGroup(string id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM groups WHERE id = @id", new { id });
    }

    // ---- tags ----
    public List<Tag> ListTags()
    {
        using var c = db.Open();
        return c.Query<Tag>("SELECT id AS Id, name AS Name, color AS Color FROM tags ORDER BY name").ToList();
    }

    public void UpsertTag(Tag t)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO tags (id, name, color) VALUES (@Id, @Name, @Color)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, color = excluded.color
            """, t);
    }

    public void DeleteTag(string id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM tags WHERE id = @id", new { id });
    }

    public Dictionary<string, List<Tag>> TagsByProfile()
    {
        using var c = db.Open();
        var rows = c.Query<(string ProfileId, string Id, string Name, string Color)>("""
            SELECT pt.profile_id, t.id, t.name, t.color FROM profile_tags pt JOIN tags t ON t.id = pt.tag_id
            """);
        return rows.GroupBy(r => r.ProfileId)
            .ToDictionary(g => g.Key, g => g.Select(r => new Tag { Id = r.Id, Name = r.Name, Color = r.Color }).ToList());
    }

    public void SetProfileTags(string profileId, IEnumerable<string> tagIds)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM profile_tags WHERE profile_id = @profileId", new { profileId }, tx);
        foreach (var tagId in tagIds.Distinct())
            c.Execute("INSERT INTO profile_tags (profile_id, tag_id) VALUES (@profileId, @tagId)", new { profileId, tagId }, tx);
        tx.Commit();
    }

    // ---- saved proxies ----
    public List<SavedProxy> ListProxies()
    {
        using var c = db.Open();
        return c.Query<SavedProxy>("""
            SELECT id AS Id, raw AS Raw, note AS Note, last_check AS LastCheck,
                   route_resolved AS RouteResolved, created_at AS CreatedAt
            FROM proxies ORDER BY created_at DESC
            """).ToList();
    }

    public void UpsertProxy(SavedProxy p)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO proxies (id, raw, note, last_check, route_resolved, created_at)
            VALUES (@Id, @Raw, @Note, @LastCheck, @RouteResolved, @CreatedAt)
            ON CONFLICT(id) DO UPDATE SET raw = excluded.raw, note = excluded.note,
                last_check = excluded.last_check, route_resolved = excluded.route_resolved
            """, p);
    }

    public void DeleteProxy(string id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM proxies WHERE id = @id", new { id });
    }
}
