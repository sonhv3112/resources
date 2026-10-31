using System.IO.Compression;
using System.Text.Json;
using GpmLite.Core.Models;

namespace GpmLite.Core.Services;

/// <summary>The profile row fields stored alongside the browser storage in a backup zip.</summary>
public sealed record BackupMetadata(
    string Name,
    string Proxy,
    string Note,
    string GroupName,
    string FingerprintJson,
    string StartupUrls);

/// <summary>
/// Backs a profile up to a .zip (its Chrome storage dir under <c>profile/</c> plus <c>metadata.json</c>),
/// and restores one as a brand-new profile (new id, new storage dir).
/// </summary>
public static class BackupService
{
    private const string MetadataEntry = "metadata.json";
    private const string ProfilePrefix = "profile/";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void Backup(ProfileService service, string profileId, string zipPath)
    {
        var p = service.Repo.GetProfile(profileId) ?? throw new KeyNotFoundException($"Không tìm thấy hồ sơ {profileId}");
        var groupName = p.GroupId is null
            ? ""
            : service.Repo.ListGroups().FirstOrDefault(g => g.Id == p.GroupId)?.Name ?? "";
        var meta = new BackupMetadata(p.Name, p.Proxy, p.Note, groupName, p.FingerprintJson, p.StartupUrls);

        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var metaEntry = zip.CreateEntry(MetadataEntry);
        using (var s = metaEntry.Open())
            JsonSerializer.Serialize(s, meta, JsonOpts);

        var dir = p.StoragePath;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            var root = Path.GetFullPath(dir);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                try { zip.CreateEntryFromFile(file, ProfilePrefix + rel); }
                catch (IOException) { /* a locked file (profile open) is skipped */ }
            }
        }
    }

    /// <summary>Recreates the profile from a backup zip with a new id and storage dir. Returns the new profile.</summary>
    public static Profile Restore(ProfileService service, string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var metaEntry = zip.GetEntry(MetadataEntry)
            ?? throw new FormatException("File sao lưu không hợp lệ: thiếu metadata.json.");

        BackupMetadata meta;
        using (var s = metaEntry.Open())
            meta = JsonSerializer.Deserialize<BackupMetadata>(s, JsonOpts)
                   ?? throw new FormatException("File sao lưu không hợp lệ: metadata.json rỗng.");

        var groupId = ResolveGroup(service, meta.GroupName);

        // Build the row directly (preserve the exact fingerprint from the backup rather than regenerating).
        var p = new Profile
        {
            Name = string.IsNullOrWhiteSpace(meta.Name) ? "Khôi phục" : meta.Name,
            GroupId = groupId,
            Proxy = meta.Proxy,
            Note = meta.Note,
            StartupUrls = meta.StartupUrls,
            FingerprintJson = string.IsNullOrWhiteSpace(meta.FingerprintJson) ? "{}" : meta.FingerprintJson,
        };
        p.StoragePath = service.Paths.ProfileDir(p.Id);
        Directory.CreateDirectory(p.StoragePath);

        var destRoot = Path.GetFullPath(p.StoragePath);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(ProfilePrefix, StringComparison.Ordinal)) continue;
            var rel = entry.FullName[ProfilePrefix.Length..];
            if (rel.Length == 0 || entry.FullName.EndsWith('/')) continue; // directory marker

            var dest = Path.GetFullPath(Path.Combine(destRoot, rel));
            // Guard against zip-slip: never write outside the new storage dir.
            if (!dest.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }

        service.Repo.UpsertProfile(p);
        return p;
    }

    private static string? ResolveGroup(ProfileService service, string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return null;
        var existing = service.Repo.ListGroups()
            .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;
        var g = new Group { Name = name };
        service.Repo.UpsertGroup(g);
        return g.Id;
    }
}
