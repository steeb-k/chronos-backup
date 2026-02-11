# Version Update Guide

This guide explains the **single-source version process** and how to update versions consistently across the project.

## Single Source of Truth

The canonical version is stored in [version.json](version.json). Everything else is synchronized from this file.

Example:

- `version`: semantic version used for releases (e.g., `0.1.1`)
- `gitHubTag`: optional release tag (e.g., `0.1.1-rc1`)

## How to Update the Version

### Option A — Edit the file, then sync

1. Edit [version.json](version.json) and set:
   - `version`
   - `gitHubTag` (optional)
2. Run:
   ```powershell
   .\sync-version.ps1
   ```

### Option B — Set version via the script

Run:

```powershell
.\sync-version.ps1 -Version "0.2.0" -GitHubTag "0.2.0-rc1"
```

This updates [version.json](version.json) and synchronizes all other files.

## What Gets Updated

The sync script updates these locations automatically:

1. [version.json](version.json) — source of truth (if parameters provided)
2. [Version.props](Version.props) — MSBuild `AppVersion` property
3. [src/Chronos.App.csproj](src/Chronos.App.csproj) — `<Version>`, `<AssemblyVersion>`, `<FileVersion>`
4. [src/app.manifest](src/app.manifest) — `assemblyIdentity version` (converted to `x.y.z.0`)
5. [installer/Chronos-x64.iss](installer/Chronos-x64.iss) — `#define MyAppVersion`
6. [installer/Chronos-arm64.iss](installer/Chronos-arm64.iss) — `#define MyAppVersion`
7. [scripts/Build-Release.ps1](scripts/Build-Release.ps1) — default `-Version` parameter

## Version Format Notes

- **Semantic version**: `x.y.z` (used in version.json and most tools)
- **Assembly version**: `x.y.z.0` (required by Windows manifest and .NET assemblies)

The script converts `x.y.z` → `x.y.z.0` automatically for app.manifest and assembly versions.

## Building a Release

After updating the version, build the release:

```powershell
# Uses version from version.json automatically
.\scripts\Build-Release.ps1

# Or override with a specific version
.\scripts\Build-Release.ps1 -Version "0.2.0"
```

## GitHub Releases

Use `version` for the main release version. If you publish pre-releases, use `gitHubTag` (e.g., `0.2.0-rc1`).

## Troubleshooting

If you see mismatched versions, re-run:

```powershell
.\sync-version.ps1
```

This will synchronize all files to match [version.json](version.json).
