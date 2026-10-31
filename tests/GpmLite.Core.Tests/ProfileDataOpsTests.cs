using System.IO.Compression;
using System.Text;
using GpmLite.Core.Models;
using GpmLite.Core.Services;
using GpmLite.Core.Storage;

namespace GpmLite.Core.Tests;

// Cookie import/export goes over a live CDP endpoint, so it is verified manually (needs a running browser).
// These tests cover everything that does not need a browser: spreadsheet/CSV parsing and the backup round-trip.

public sealed class ProfileImportExportTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpmlite-ie-" + Guid.NewGuid().ToString("N"));

    private string NewFile(string name) { Directory.CreateDirectory(_dir); return Path.Combine(_dir, name); }

    [Fact]
    public void Parses_csv_with_header_and_columns()
    {
        var path = NewFile("in.csv");
        File.WriteAllText(path,
            "Browser type,Profile name,Proxy type,Proxy,Group\n" +
            "Chrome,FB 01,Socks5,1.2.3.4:1080:user:pass,Ads\n" +
            "Chrome,\"Name, with comma\",HTTP,5.6.7.8:3128,\n");

        var rows = ProfileImportExport.ParseFile(path);
        Assert.Equal(2, rows.Count);
        Assert.Equal("FB 01", rows[0].Name);
        Assert.Equal("Ads", rows[0].Group);
        Assert.Equal("Socks5", rows[0].ProxyType);
        Assert.Equal("1.2.3.4:1080:user:pass", rows[0].Proxy);
        Assert.Equal("Name, with comma", rows[1].Name);
        Assert.Equal("", rows[1].Group);

        Cleanup();
    }

    [Fact]
    public void Parses_minimal_xlsx()
    {
        var path = NewFile("in.xlsx");
        WriteXlsx(path, new[]
        {
            new[] { "Browser type", "Profile name", "Proxy type", "Proxy", "Group" },
            new[] { "Chrome", "Acc A", "Socks5", "10.0.0.1:1080", "GrpA" },
            new[] { "Chrome", "Acc B", "No Proxy", "", "" },
        });

        var rows = ProfileImportExport.ParseFile(path);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Acc A", rows[0].Name);
        Assert.Equal("Socks5", rows[0].ProxyType);
        Assert.Equal("10.0.0.1:1080", rows[0].Proxy);
        Assert.Equal("GrpA", rows[0].Group);
        Assert.Equal("Acc B", rows[1].Name);

        Cleanup();
    }

    [Fact]
    public void BuildProxy_maps_types_and_schemes()
    {
        Assert.Equal("", ProfileImportExport.BuildProxy("No Proxy", ""));
        Assert.Equal("", ProfileImportExport.BuildProxy("", ""));
        Assert.Equal("socks5://1.2.3.4:1080", ProfileImportExport.BuildProxy("Socks5", "1.2.3.4:1080"));
        Assert.Equal("http://1.2.3.4:3128", ProfileImportExport.BuildProxy("HTTP", "1.2.3.4:3128"));
        Assert.Equal("socks4://1.2.3.4:1080", ProfileImportExport.BuildProxy("socks4", "1.2.3.4:1080"));
        Assert.Equal("socks5://host:1", ProfileImportExport.BuildProxy("HTTP", "socks5://host:1")); // already schemed wins
    }

    private void Cleanup()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>Writes a minimal but valid .xlsx (sharedStrings + one sheet) for parse tests.</summary>
    internal static void WriteXlsx(string path, string[][] grid)
    {
        // Build the shared-string table.
        var shared = new List<string>();
        var index = new Dictionary<string, int>();
        int Intern(string s)
        {
            if (index.TryGetValue(s, out var i)) return i;
            i = shared.Count;
            shared.Add(s);
            index[s] = i;
            return i;
        }

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var r = 0; r < grid.Length; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < grid[r].Length; c++)
            {
                var @ref = $"{ColumnName(c)}{r + 1}";
                var si = Intern(grid[r][c]);
                sheet.Append($"<c r=\"{@ref}\" t=\"s\"><v>{si}</v></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        var sst = new StringBuilder();
        sst.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sst.Append($"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{shared.Count}" uniqueCount="{shared.Count}">""");
        foreach (var s in shared)
            sst.Append($"<si><t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(s)}</t></si>");
        sst.Append("</sst>");

        const string contentTypes = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
            </Types>
            """;
        const string workbook = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
            <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>
            """;

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", contentTypes);
        Add(zip, "xl/workbook.xml", workbook);
        Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        Add(zip, "xl/sharedStrings.xml", sst.ToString());

        static void Add(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name);
            using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
            s.Write(content);
        }
    }

    private static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            index = (index - 1) / 26;
        }
        return sb.ToString();
    }
}

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _srcHome = Path.Combine(Path.GetTempPath(), "gpmlite-bk-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _dstHome = Path.Combine(Path.GetTempPath(), "gpmlite-bk-dst-" + Guid.NewGuid().ToString("N"));

    private static ProfileService NewService(string home)
    {
        var paths = new AppPaths(home);
        var repo = new Repository(new Database(paths.DatabaseFile));
        return new ProfileService(paths, repo, new AppSettings());
    }

    [Fact]
    public void Backup_then_restore_roundtrip()
    {
        var src = NewService(_srcHome);

        // Fake a profile row + its storage dir with some files (cookies db, nested state).
        var grp = new Group { Name = "Marketing" };
        src.Repo.UpsertGroup(grp);
        var p = new Profile
        {
            Name = "Shop 01",
            GroupId = grp.Id,
            Proxy = "socks5://1.2.3.4:1080",
            Note = "ghi chú",
            StartupUrls = "https://example.com",
            FingerprintJson = """{"Os":1,"Seed":123}""",
        };
        p.StoragePath = src.Paths.ProfileDir(p.Id);
        Directory.CreateDirectory(Path.Combine(p.StoragePath, "Default"));
        File.WriteAllText(Path.Combine(p.StoragePath, "Local State"), "root-file");
        File.WriteAllText(Path.Combine(p.StoragePath, "Default", "Cookies"), "cookie-bytes");
        src.Repo.UpsertProfile(p);

        var zip = Path.Combine(_srcHome, "shop01.zip");
        BackupService.Backup(src, p.Id, zip);
        Assert.True(File.Exists(zip));

        // Restore into a completely fresh home.
        var dst = NewService(_dstHome);
        var restored = BackupService.Restore(dst, zip);

        Assert.NotEqual(p.Id, restored.Id);                       // new id
        Assert.Equal("Shop 01", restored.Name);
        Assert.Equal("socks5://1.2.3.4:1080", restored.Proxy);
        Assert.Equal("ghi chú", restored.Note);
        Assert.Equal("https://example.com", restored.StartupUrls);
        Assert.Equal(p.FingerprintJson, restored.FingerprintJson); // fingerprint preserved exactly

        // Group recreated by name in the destination store.
        var dstGroup = dst.Repo.ListGroups().Single();
        Assert.Equal("Marketing", dstGroup.Name);
        Assert.Equal(dstGroup.Id, restored.GroupId);

        // Row persisted and storage files extracted.
        var loaded = dst.Repo.GetProfile(restored.Id)!;
        Assert.Equal("Shop 01", loaded.Name);
        Assert.Equal(dst.Paths.ProfileDir(restored.Id), restored.StoragePath);
        Assert.Equal("root-file", File.ReadAllText(Path.Combine(restored.StoragePath, "Local State")));
        Assert.Equal("cookie-bytes", File.ReadAllText(Path.Combine(restored.StoragePath, "Default", "Cookies")));
    }

    [Fact]
    public void Restore_rejects_zip_without_metadata()
    {
        var dst = NewService(_dstHome);
        var bad = Path.Combine(_dstHome, "bad.zip");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
            zip.CreateEntry("profile/x.txt");
        Assert.Throws<FormatException>(() => BackupService.Restore(dst, bad));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { _srcHome, _dstHome })
            try { Directory.Delete(d, true); } catch (IOException) { }
    }
}

public sealed class ProfileCloneTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gpmlite-clone-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileService _service;

    public ProfileCloneTests()
    {
        var paths = new AppPaths(_home);
        _service = new ProfileService(paths, new Repository(new Database(paths.DatabaseFile)), new AppSettings());
    }

    private Profile SeedProfile()
    {
        var p = new Profile
        {
            Name = "Shop 01",
            Proxy = "socks5://1.2.3.4:1080",
            Note = "ghi chú",
            StartupUrls = "https://example.com",
            FingerprintJson = """{"Os":0,"Seed":424242,"CanvasRate":17}""",
        };
        p.StoragePath = _service.Paths.ProfileDir(p.Id);
        Directory.CreateDirectory(Path.Combine(p.StoragePath, "Default"));
        File.WriteAllText(Path.Combine(p.StoragePath, "Local State"), "root-file");
        File.WriteAllText(Path.Combine(p.StoragePath, "Default", "Cookies"), "cookie-bytes");
        // A stale Chrome lock marker must NOT be carried into the clone.
        File.WriteAllText(Path.Combine(p.StoragePath, "SingletonCookie"), "stale");
        _service.Repo.UpsertProfile(p);
        return p;
    }

    [Fact]
    public void Clone_copies_fingerprint_verbatim_and_user_data_dir()
    {
        var src = SeedProfile();
        var copy = _service.Clone(src.Id);

        Assert.NotEqual(src.Id, copy.Id);                               // new id
        Assert.Equal(_service.Paths.ProfileDir(copy.Id), copy.StoragePath); // new storage path
        Assert.Equal("Shop 01 (copy)", copy.Name);
        Assert.Equal(src.FingerprintJson, copy.FingerprintJson);        // verbatim, not regenerated
        Assert.Equal(src.Proxy, copy.Proxy);

        // Cookies/sessions carried over.
        Assert.Equal("root-file", File.ReadAllText(Path.Combine(copy.StoragePath, "Local State")));
        Assert.Equal("cookie-bytes", File.ReadAllText(Path.Combine(copy.StoragePath, "Default", "Cookies")));
        // Singleton* lock markers are not copied.
        Assert.False(File.Exists(Path.Combine(copy.StoragePath, "SingletonCookie")));

        // Persisted as its own row.
        Assert.Equal("Shop 01 (copy)", _service.Repo.GetProfile(copy.Id)!.Name);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }
}
