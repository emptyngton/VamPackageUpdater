using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace VamPackageUpdater.Services;

public sealed class PackageUpdater
{
    private readonly Action<string> _log;

    public PackageUpdater(Action<string> log) => _log = log;

    public async Task<UpdateResult> RunAsync(UpdaterOptions options, CancellationToken ct = default)
    {
        return await Task.Run(() => Run(options, ct), ct);
    }

    private UpdateResult Run(UpdaterOptions opts, CancellationToken ct)
    {
        if (!File.Exists(opts.SourceVarPath))
            return UpdateResult.Fail($"Source .var not found: {opts.SourceVarPath}");

        if (string.IsNullOrWhiteSpace(opts.PluginName) || string.IsNullOrWhiteSpace(opts.NewVersion))
            return UpdateResult.Fail("Plugin name and new version are required.");

        var pattern = new Regex(
            $@"{Regex.Escape(opts.PluginName)}\.(?:\d+|latest)\b",
            RegexOptions.IgnoreCase);
        var replacement = $"{opts.PluginName}.{opts.NewVersion}";

        _log($"Starting update for: {Path.GetFileName(opts.SourceVarPath)}");
        _log($"Searching for: '{opts.PluginName}.(number or latest)'");
        _log($"Replacing with: '{replacement}'");
        _log(opts.NewLicenseLine is null
            ? "License will not be changed."
            : $"License will be changed.");
        _log(new string('-', 50));

        var tempDir = Path.Combine(Path.GetTempPath(), "VamPackageUpdater_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            _log($"Unpacking '{Path.GetFileName(opts.SourceVarPath)}'...");
            try
            {
                ZipFile.ExtractToDirectory(opts.SourceVarPath, tempDir);
            }
            catch (InvalidDataException)
            {
                return UpdateResult.Fail($"'{Path.GetFileName(opts.SourceVarPath)}' is not a valid zip archive.");
            }

            ct.ThrowIfCancellationRequested();

            var totalReplacements = 0;
            var licenseUpdated = false;

            var metaJson = Path.Combine(tempDir, "meta.json");
            if (File.Exists(metaJson))
            {
                _log("  - Processing 'meta.json'...");
                var lines = File.ReadAllLines(metaJson).ToList();

                if (opts.NewLicenseLine is not null)
                {
                    for (var i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].Contains("\"licenseType\""))
                        {
                            var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                            lines[i] = indent + opts.NewLicenseLine;
                            licenseUpdated = true;
                            _log("    - License updated in 'meta.json'.");
                            break;
                        }
                    }
                }

                var content = string.Join("\n", lines);
                var (replaced, count) = ReplaceAll(pattern, content, replacement);
                if (count > 0)
                {
                    totalReplacements += count;
                    _log($"    - Found {count} plugin reference(s) in 'meta.json'.");
                }
                File.WriteAllText(metaJson, replaced);
            }

            var scenesRoot = Path.Combine(tempDir, "Saves", "scene");
            if (Directory.Exists(scenesRoot))
            {
                foreach (var file in Directory.EnumerateFiles(scenesRoot, "*.json", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var text = File.ReadAllText(file);
                    var (replaced, count) = ReplaceAll(pattern, text, replacement);
                    if (count > 0)
                    {
                        totalReplacements += count;
                        var rel = Path.GetRelativePath(tempDir, file);
                        _log($"  - Found {count} reference(s) in '{rel}'.");
                        File.WriteAllText(file, replaced);
                    }
                }
            }

            if (totalReplacements == 0 && !licenseUpdated)
            {
                _log("");
                _log("- No changes made. No new file created.");
                return UpdateResult.Ok(null, 0, false);
            }

            var sourceFile = new FileInfo(opts.SourceVarPath);
            var stem = Path.GetFileNameWithoutExtension(sourceFile.Name);
            var parts = stem.Split('.');
            string newFilename;
            if (parts.Length > 1 && int.TryParse(parts[^1], out var n))
            {
                parts[^1] = (n + 1).ToString();
                newFilename = string.Join('.', parts) + ".var";
            }
            else
            {
                newFilename = stem + ".updated.var";
                _log("");
                _log("- Note: Could not find numeric version in filename. Using fallback name.");
            }

            var outDir = Path.Combine(sourceFile.DirectoryName!, "updated_packages");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, newFilename);

            if (File.Exists(outPath))
            {
                _log($"  - Warning: '{newFilename}' already exists and will be overwritten.");
                File.Delete(outPath);
            }

            _log("");
            _log($"Repackaging to '{newFilename}'...");
            ZipFromDirectory(tempDir, outPath);

            _log(new string('-', 50));
            _log($"Success! Package created at:");
            _log($"   {outPath}");

            return UpdateResult.Ok(outPath, totalReplacements, licenseUpdated);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static (string Text, int Count) ReplaceAll(Regex pattern, string input, string replacement)
    {
        var count = 0;
        var result = pattern.Replace(input, m => { count++; return replacement; });
        return (result, count);
    }

    private static void ZipFromDirectory(string sourceDir, string destZip)
    {
        using var archive = ZipFile.Open(destZip, ZipArchiveMode.Create);
        var basePath = Path.GetFullPath(sourceDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(basePath, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
        }
    }
}

public sealed record UpdateResult(bool Success, string? OutputPath, int Replacements, bool LicenseUpdated, string? Error)
{
    public static UpdateResult Ok(string? path, int replacements, bool licenseUpdated) =>
        new(true, path, replacements, licenseUpdated, null);

    public static UpdateResult Fail(string error) =>
        new(false, null, 0, false, error);
}
