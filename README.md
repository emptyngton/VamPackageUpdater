# VAM Package Updater

A small Windows utility that rewrites plugin references, embeds Voxta resources, and downloads missing Hub dependencies for Virt-a-Mate `.var` packages without opening VaM itself.

## Why

When a plugin author ships a new version, every scene `.var` that depended on the old version breaks — the references inside the scene JSON still point at `AcidBubbles.Voxta.42` while the latest is `AcidBubbles.Voxta.86`. The official workflow is: launch VaM, open each scene, update the plugin version on every atom, re-package. Heavy and slow.

This tool does the same thing in about 300ms without starting VaM:

1. Unzips the `.var`
2. Rewrites plugin version references inside `meta.json` and every scene JSON
3. Optionally rewrites the `licenseType` field
4. Optionally embeds Voxta resource PNGs (characters / scenarios / memory books / packages) that the scene references but aren't bundled
5. Re-zips to `updated_packages/<original-name>.<N+1>.var`

## Usage

1. Download the latest `VamPackageUpdater.exe` from [Releases](../../releases) (single file, no install).
2. Launch it.
3. **Browse** → pick a `.var` package.
4. Use the **Plugin references** tab to queue any plugin version updates.
5. Use the **Voxta resources** tab to attach PNGs for any scene-referenced Voxta characters / scenarios / memory books / packages that aren't already bundled.
6. Use the **Download deps** tab to pull any missing Hub dependencies directly from hub.virtamate.com (see below).
7. Optionally change the license.
8. **Update Package** → new file appears in an `updated_packages/` folder next to the source.

## Embedding Voxta resources

When a scene in the `.var` has the Voxta VaM plugin and references a character (or scenario / memory book / package) by UUID, the resource needs to ship alongside it so the scene works on a clean install. On scan the tool shows every referenced Voxta resource and whether a matching PNG is already bundled:

- **Bundled** — green — a matching PNG is already inside the `.var` at `Saves/PluginData/Voxta/{Kind}s/{uuid}.{kind}.{ext}`. Nothing to do.
- **Missing** — red — scene references the resource but no PNG is bundled. Click the **…** button on the row and pick the PNG you exported from Voxta Studio. The tool validates PNG magic bytes and stages the attachment.
- **Attached** — blue — you've staged a PNG that will be written into the `.var` on the next **Update Package**.
- **Orphan** — grey — a bundled PNG that no scene references. Informational only; nothing is removed.

The attached PNG is embedded at the exact path the Voxta VaM plugin expects — `Saves/PluginData/Voxta/{Kind}s/{uuid}.{kind}.png` — regardless of the filename you picked. Works with the plugin's folder-scan fallback so versioned filenames like `...character.1.0.0.png` also resolve correctly if you prefer to keep the original name around for your own library.

## Downloading Hub dependencies

The **Download deps** tab lists every package referenced by the scene's `meta.json` dependency tree (transitive — nested deps included). Each row shows its status:

- **Installed** — green — a matching `.var` is already in your AddonPackages folder.
- **Missing** — red — nothing in AddonPackages matches. Queued for download.
- **Downloaded** — green — just fetched in this run.
- **Not on Hub** — amber — Hub returned no download URL; the package is paid-only, removed, or was never uploaded to Hub. Grab it manually from the creator's Patreon.
- **Error** — red — network / server error (see the log for details).

The **Download all missing** button queries the Hub's public find-packages API (the same endpoint VaM itself uses), then streams each `.var` directly into your AddonPackages folder. `.latest` refs are resolved server-side by Hub to a concrete version. Downloads run serially to be polite to the Hub.

By using this feature you agree to the Virt-a-Mate Hub terms that normally appear in-app as a consent banner — we pass `vamhubconsent=yes` on every download so the Hub knows you've accepted them. If you don't agree to the Hub's terms, don't use this feature.

## Building from source

Requires .NET 10 SDK.

```powershell
dotnet build
dotnet run --project VamPackageUpdater
```

To build a single-file self-contained `.exe`:

```powershell
.\publish.ps1
```

Output lands in `publish/VamPackageUpdater.exe`.

## Credits

Original Python script by an unknown VaM author — this is a C# / WPF rewrite with improvements (async, licence dropdown, forward-slash zip entries, overwrite protection).

## License

Apache 2.0 — see [LICENSE](LICENSE).
