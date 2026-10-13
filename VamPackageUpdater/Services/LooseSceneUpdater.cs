using System.IO;
using System.Text;
using VamPackageUpdater.Models;

namespace VamPackageUpdater.Services;

/// <summary>
/// Rewrites plugin references in a scene saved straight to Saves/scene (a .json, not a .var). VaM writes the exact
/// package a scene loaded when it saves, never ".latest", so loose scenes need this as much as packages do.
/// The scene is rewritten in place; the original is kept next to it as a timestamped .json.bak, which VaM's scene
/// browser ignores.
/// </summary>
public sealed class LooseSceneUpdater
{
    private readonly Action<string> _log;

    public LooseSceneUpdater(Action<string> log) => _log = log;

    public static bool IsLooseScene(string path) =>
        string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    public Task<UpdateResult> RunAsync(string scenePath, IEnumerable<PluginUpdate> updates, bool dryRun,
        CancellationToken ct = default) =>
        Task.Run(() => Run(scenePath, updates, dryRun, ct), ct);

    private UpdateResult Run(string scenePath, IEnumerable<PluginUpdate> updates, bool dryRun, CancellationToken ct)
    {
        if (!File.Exists(scenePath))
            return UpdateResult.Fail($"Scene not found: {scenePath}");

        var rewriter = new PluginReferenceRewriter(updates);
        if (rewriter.IsEmpty)
            return UpdateResult.Fail("Nothing to do: no plugin updates queued.");

        _log($"Starting update for scene: {Path.GetFileName(scenePath)}");
        _log($"Plugin updates queued: {rewriter.Updates.Count}");
        foreach (var u in rewriter.Updates)
            _log($"  - {u.PluginId}  ->  {u.NewVersion}");
        _log(new string('-', 50));

        var bytes = File.ReadAllBytes(scenePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        ct.ThrowIfCancellationRequested();

        var perPluginCounts = new Dictionary<string, int>();
        var (rewritten, count) = rewriter.Rewrite(text, perPluginCounts);

        if (count == 0)
        {
            _log("- No changes made. The scene already uses these versions.");
            return UpdateResult.Ok(null, 0, false);
        }

        _log("Per-plugin replacement counts:");
        foreach (var kv in perPluginCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
            _log($"  - {kv.Key}: {kv.Value}");

        var backupPath = FindFreeBackupPath(scenePath);
        if (dryRun)
        {
            _log("");
            _log(new string('-', 50));
            _log("DRY RUN: no file was written. Would have:");
            _log($"   - Kept the original as: {Path.GetFileName(backupPath)}");
            _log($"   - Rewritten:            {scenePath}");
            return UpdateResult.Ok(null, count, false);
        }

        // Written beside the scene first, then swapped in by File.Replace, which moves the original to the backup
        // path in the same step: a failed write leaves the scene untouched.
        var tempPath = scenePath + ".tmp";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom);
        File.WriteAllText(tempPath, rewritten, encoding);
        File.Replace(tempPath, scenePath, backupPath);

        _log("");
        _log($"Kept the original as '{Path.GetFileName(backupPath)}'.");
        _log(new string('-', 50));
        _log("Success! Scene updated:");
        _log($"   {scenePath}");
        return UpdateResult.Ok(scenePath, count, false);
    }

    private static string FindFreeBackupPath(string scenePath)
    {
        var dir = Path.GetDirectoryName(scenePath)!;
        var stem = Path.GetFileNameWithoutExtension(scenePath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(dir, $"{stem}.{timestamp}.json.bak");
        for (var counter = 2; File.Exists(candidate); counter++)
            candidate = Path.Combine(dir, $"{stem}.{timestamp}.{counter}.json.bak");
        return candidate;
    }
}
