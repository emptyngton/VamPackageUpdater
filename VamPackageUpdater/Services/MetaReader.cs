using System.IO;
using System.IO.Compression;
using System.Text.Json;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

public sealed class MetaReader
{
    public Task<PackageMeta> ReadAsync(string varPath, CancellationToken ct = default) =>
        Task.Run(() => Read(varPath), ct);

    public PackageMeta Read(string varPath)
    {
        var fileSize = new FileInfo(varPath).Length;

        using var archive = ZipFile.OpenRead(varPath);
        var fileCount = archive.Entries.Count(e => !e.FullName.EndsWith('/'));

        var metaEntry = archive.GetEntry("meta.json");
        if (metaEntry is null)
            return new PackageMeta { FileSize = fileSize, FileCount = fileCount };

        string json;
        try
        {
            using var stream = metaEntry.Open();
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch
        {
            return new PackageMeta { FileSize = fileSize, FileCount = fileCount };
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new PackageMeta
            {
                CreatorName = GetString(root, "creatorName"),
                PackageName = GetString(root, "packageName"),
                LicenseType = GetString(root, "licenseType"),
                Description = GetString(root, "description"),
                Credits = GetString(root, "credits"),
                ProgramVersion = GetString(root, "programVersion"),
                FileSize = fileSize,
                FileCount = fileCount
            };
        }
        catch (JsonException)
        {
            return new PackageMeta { FileSize = fileSize, FileCount = fileCount };
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
