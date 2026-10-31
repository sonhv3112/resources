using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Services;

/// <summary>One row parsed from a GPM-format import file.
/// Column order: Browser type | Profile name | Proxy type | Proxy | Group.</summary>
public sealed record ImportRow(string BrowserType, string Name, string ProxyType, string Proxy, string Group);

/// <summary>Outcome of an import: how many profiles were created, how many rows were skipped, and why.</summary>
public sealed record ImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Reads profiles from a GPM-style spreadsheet (.xlsx) or a .csv with the same columns and creates them
/// through <see cref="ProfileService.Create"/>. .xlsx is parsed by hand (unzip + XML) so no NuGet is needed.
/// </summary>
public static class ProfileImportExport
{
    // --------- public API ---------

    public static List<ImportRow> ParseFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv" => ParseCsv(File.ReadAllText(path)),
            ".xlsx" => ParseXlsx(path),
            _ => throw new FormatException($"Định dạng không hỗ trợ: {ext}. Hãy dùng .xlsx hoặc .csv."),
        };
    }

    public static ImportResult Import(ProfileService service, string path)
    {
        var rows = ParseFile(path);
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;
        var groupCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var rowNo = 1;
        foreach (var row in rows)
        {
            rowNo++;
            // Fully blank row -> ignore silently.
            if (string.IsNullOrWhiteSpace(row.Name) && string.IsNullOrWhiteSpace(row.Proxy)
                && string.IsNullOrWhiteSpace(row.Group) && string.IsNullOrWhiteSpace(row.BrowserType))
                continue;

            if (string.IsNullOrWhiteSpace(row.Name))
            {
                errors.Add($"Dòng {rowNo}: thiếu tên hồ sơ.");
                skipped++;
                continue;
            }

            var proxy = BuildProxy(row.ProxyType, row.Proxy);
            if (proxy.Length > 0 && !ProxyConfig.TryParse(proxy, out _, out var err))
            {
                errors.Add($"Dòng {rowNo} ({row.Name}): proxy không hợp lệ - {err}");
                skipped++;
                continue;
            }

            try
            {
                var groupId = ResolveGroup(service, row.Group, groupCache);
                service.Create(row.Name.Trim(), groupId, proxy);
                imported++;
            }
            catch (Exception e)
            {
                errors.Add($"Dòng {rowNo} ({row.Name}): {e.Message}");
                skipped++;
            }
        }

        return new ImportResult(imported, skipped, errors);
    }

    /// <summary>Combines a GPM proxy-type column and a proxy value into a raw string
    /// <see cref="ProxyConfig.Parse"/> understands. Empty / "no proxy" -> "" (direct).</summary>
    public static string BuildProxy(string? type, string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) return "";
        if (value.Contains("://", StringComparison.Ordinal)) return value; // already schemed
        var t = (type ?? "").Trim().ToLowerInvariant();
        if (t is "no proxy" or "none" or "direct") return "";
        var scheme = t switch
        {
            "socks5" or "socks5h" or "socks" => "socks5",
            "socks4" or "socks4a" => "socks4",
            _ => "http",
        };
        return $"{scheme}://{value}";
    }

    private static string? ResolveGroup(ProfileService service, string? name, Dictionary<string, string?> cache)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return null;
        if (cache.TryGetValue(name, out var cached)) return cached;

        var existing = service.Repo.ListGroups()
            .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        string id;
        if (existing is not null) id = existing.Id;
        else
        {
            var g = new Group { Name = name };
            service.Repo.UpsertGroup(g);
            id = g.Id;
        }
        cache[name] = id;
        return id;
    }

    // --------- CSV ---------

    public static List<ImportRow> ParseCsv(string content)
    {
        var rows = new List<ImportRow>();
        var records = ParseCsvRecords(content);
        var first = true;
        foreach (var fields in records)
        {
            if (first)
            {
                first = false;
                if (LooksLikeHeader(fields)) continue;
            }
            rows.Add(ToRow(fields));
        }
        return rows;
    }

    /// <summary>RFC-4180-ish CSV: handles quoted fields, embedded commas, quotes ("") and newlines.</summary>
    private static List<List<string>> ParseCsvRecords(string content)
    {
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var any = false;

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; any = true; break;
                    case ',': record.Add(field.ToString()); field.Clear(); any = true; break;
                    case '\r': break;
                    case '\n':
                        record.Add(field.ToString()); field.Clear();
                        records.Add(record); record = new List<string>(); any = false;
                        break;
                    default: field.Append(ch); any = true; break;
                }
            }
        }
        if (any || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    // --------- XLSX (manual: unzip + XML) ---------

    public static List<ImportRow> ParseXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(zip);
        var sheetEntry = FindSheetEntry(zip)
            ?? throw new FormatException("Không tìm thấy worksheet trong file .xlsx.");

        var rows = new List<ImportRow>();
        using var stream = sheetEntry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = doc.Root?.Name.Namespace ?? "";

        var first = true;
        foreach (var rowEl in doc.Descendants(ns + "row"))
        {
            var cells = new List<string>();
            var expected = 0;
            foreach (var c in rowEl.Elements(ns + "c"))
            {
                var colIndex = ColumnIndex((string?)c.Attribute("r"));
                // fill gaps for sparse rows
                while (colIndex >= 0 && cells.Count < colIndex) cells.Add("");
                cells.Add(CellText(c, ns, shared));
                expected = cells.Count;
            }
            _ = expected;

            if (first)
            {
                first = false;
                if (LooksLikeHeader(cells)) continue;
            }
            rows.Add(ToRow(cells));
        }
        return rows;
    }

    private static string[] ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = doc.Root?.Name.Namespace ?? "";
        return doc.Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToArray();
    }

    private static ZipArchiveEntry? FindSheetEntry(ZipArchive zip) =>
        zip.GetEntry("xl/worksheets/sheet1.xml")
        ?? zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

    private static string CellText(XElement c, XNamespace ns, string[] shared)
    {
        var type = (string?)c.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(c.Element(ns + "is")?.Descendants(ns + "t").Select(t => t.Value) ?? []);

        var v = c.Element(ns + "v");
        if (v is null) return "";
        if (type == "s" && int.TryParse(v.Value, out var idx) && idx >= 0 && idx < shared.Length)
            return shared[idx];
        return v.Value;
    }

    /// <summary>"B7" -> 1 (zero-based column). Returns -1 when there is no cell reference.</summary>
    private static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        var col = 0;
        var seen = false;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') { col = col * 26 + (ch - 'A' + 1); seen = true; }
            else if (ch is >= 'a' and <= 'z') { col = col * 26 + (ch - 'a' + 1); seen = true; }
            else break;
        }
        return seen ? col - 1 : -1;
    }

    // --------- shared helpers ---------

    private static ImportRow ToRow(IReadOnlyList<string> f) => new(
        BrowserType: Get(f, 0),
        Name: Get(f, 1),
        ProxyType: Get(f, 2),
        Proxy: Get(f, 3),
        Group: Get(f, 4));

    private static string Get(IReadOnlyList<string> f, int i) => i < f.Count ? (f[i] ?? "").Trim() : "";

    private static bool LooksLikeHeader(IReadOnlyList<string> fields)
    {
        var joined = string.Join("|", fields).ToLowerInvariant();
        return joined.Contains("profile name") || joined.Contains("browser type")
            || joined.Contains("proxy type");
    }
}
