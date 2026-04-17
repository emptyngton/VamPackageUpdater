# VAM Package Updater

A small Windows utility that rewrites plugin references inside Virt-a-Mate `.var` packages without opening VaM itself.

## Why

When a plugin author ships a new version, every scene `.var` that depended on the old version breaks — the references inside the scene JSON still point at `AcidBubbles.Voxta.42` while the latest is `AcidBubbles.Voxta.86`. The official workflow is: launch VaM, open each scene, update the plugin version on every atom, re-package. Heavy and slow.

This tool does the same thing in about 300ms without starting VaM:

1. Unzips the `.var`
2. Rewrites plugin version references inside `meta.json` and every scene JSON
3. Optionally rewrites the `licenseType` field
4. Re-zips to `updated_packages/<original-name>.<N+1>.var`

## Usage

1. Download the latest `VamPackageUpdater.exe` from [Releases](../../releases) (single file, no install).
2. Launch it.
3. **Browse** → pick a `.var` package.
4. Enter the plugin name (e.g. `AcidBubbles.Voxta`) and the new version number.
5. Optionally change the license.
6. **Update Package** → new file appears in an `updated_packages/` folder next to the source.

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
