using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace VamPackageUpdater.Services;

public sealed class PackageUpdater
{
    private static readonly HashSet<string> ScannableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".json", ".vap", ".vam", ".vaj", ".vab" };

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

        if (opts.PluginUpdates.Count == 0 && opts.NewLicenseLine is null)
            return UpdateResult.Fail("Nothing to do — no plugin updates and no license change.");

        var patterns = opts.PluginUpdates
            .Where(u => !string.IsNullOrWhiteSpace(u.NewVersion))
            .Select(u => new PluginPattern(
                u,
                new Regex($@"{Regex.Escape(u.PluginId)}\.(?:\d+|latest)\b", RegexOptions.IgnoreCase),
                $"{u.PluginId}.{u.NewVersion.Trim()}"))
            .ToList();

        _log($"Starting update for: {Path.GetFileName(opts.SourceVarPath)}");
        if (patterns.Count == 0)
            _log("No plugin updates queued.");
        else
        {
            _log($"Plugin updates queued: {patterns.Count}");
            foreach (var p in patterns)
                _log($"  - {p.Update.PluginId}  ->  {p.Update.NewVersion}");
        }
        _log(opts.NewLicenseLine is null
            ? "License will not be changed."
            : "License will be changed.");
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
            var perPluginCounts = new Dictionary<string, int>();
            var licenseUpdated = false;

            var metaJson = Path.Combine(tempDir, "meta.json");
            if (opts.NewLicenseLine is not null && File.Exists(metaJson))
            {
                var lines = File.ReadAllLines(metaJson).ToList();
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Contains("\"licenseType\""))
                    {
                        var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                        lines[i] = indent + opts.NewLicenseLine;
                        licenseUpdated = true;
                        _log("License updated in 'meta.json'.");
                        File.WriteAllText(metaJson, string.Join("\n", lines));
                        break;
                    }
                }
            }

            if (patterns.Count > 0)
            {
                foreach (var file in EnumerateScannableFiles(tempDir))
                {
                    ct.ThrowIfCancellationRequested();
                    var text = File.ReadAllText(file);
                    var changed = false;

                    foreach (var p in patterns)
                    {
                        var (replaced, count) = ReplaceAll(p.Regex, text, p.Replacement);
                        if (count > 0)
                        {
                            text = replaced;
                            changed = true;
                            totalReplacements += count;
                            perPluginCounts[p.Update.PluginId] =
                                perPluginCounts.GetValueOrDefault(p.Update.PluginId) + count;
                        }
                    }

                    if (changed)
                    {
                        File.WriteAllText(file, text);
                        var rel = Path.GetRelativePath(tempDir, file);
                        _log($"  - Updated: {rel}");
                    }
                }

                if (perPluginCounts.Count > 0)
                {
                    _log("");
                    _log("Per-plugin replacement counts:");
                    foreach (var kv in perPluginCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
                        _log($"  - {kv.Key}: {kv.Value}");
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

            if (opts.DryRun)
            {
                _log("");
                _log(new string('-', 50));
                _log("DRY RUN: no file was written. Would have created:");
                _log($"   {outPath}");
                return UpdateResult.Ok(null, totalReplacements, licenseUpdated);
            }

            if (File.Exists(outPath))
            {
                var choice = opts.OnCollision?.Invoke(outPath) ?? CollisionChoice.Overwrite;
                switch (choice)
                {
                    case CollisionChoice.Cancel:
                        _log("");
                        _log("Cancelled. No file was written.");
                        return UpdateResult.Ok(null, totalReplacements, licenseUpdated);
                    case CollisionChoice.NextFree:
                        outPath = FindNextFreePath(outDir, stem);
                        newFilename = Path.GetFileName(outPath);
                        _log($"Using next free filename: {newFilename}");
                        break;
                    case CollisionChoice.Overwrite:
                        _log($"Overwriting existing '{newFilename}'.");
                        File.Delete(outPath);
                        break;
                }
            }

            _log("");
            _log($"Repackaging to '{newFilename}'...");
            ZipFromDirectory(tempDir, outPath);

            _log(new string('-', 50));
            _log("Success! Package created at:");
            _log($"   {outPath}");

            return UpdateResult.Ok(outPath, totalReplacements, licenseUpdated);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    public static string FindNextFreePath(string outDir, string sourceStem)
    {
        var parts = sourceStem.Split('.');
        if (parts.Length > 1 && int.TryParse(parts[^1], out var n))
        {
            for (var candidate = n + 1; candidate < n + 10000; candidate++)
            {
                parts[^1] = candidate.ToString();
                var candidateName = string.Join('.', parts) + ".var";
                var candidatePath = Path.Combine(outDir, candidateName);
                if (!File.Exists(candidatePath)) return candidatePath;
            }
        }
        return Path.Combine(outDir, sourceStem + ".updated." + Guid.NewGuid().ToString("N")[..8] + ".var");
    }

    private static IEnumerable<string> EnumerateScannableFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (ScannableExtensions.Contains(Path.GetExtension(file)))
                yield return file;
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

    private sealed record PluginPattern(Models.PluginUpdate Update, Regex Regex, string Replacement);
}

public sealed record UpdateResult(bool Success, string? OutputPath, int Replacements, bool LicenseUpdated, string? Error)
{
    public static UpdateResult Ok(string? path, int replacements, bool licenseUpdated) =>
        new(true, path, replacements, licenseUpdated, null);

    public static UpdateResult Fail(string error) =>
        new(false, null, 0, false, error);
}
