# Chronos PhoenixPE Plugin Plan

## Problem

Chronos is a WinUI 3 / .NET 10 desktop app targeting WinPE (PhoenixPE). The
app depends on ~25 Windows system DLLs that are not included in a standard
WinPE image. Previously these were bundled into the Chronos portable ZIP from
the dev machine's System32. Because the dev machine runs build 26100 and the
PE is built from build 22621, the bundled DLLs call functions that do not
exist in the PE's ntdll/user32, causing delay-load failures (exit code 255).

The fix is two-part:

1. **Build script** -- stop bundling system DLLs. The portable ZIP should
   contain only the app, WinAppSDK DLLs, UCRT forwarders, and VC++ runtime.
2. **PhoenixPE plugin** -- inject the required system DLLs from the PE's
   source WIM during the build, so versions always match.

---

## Part 1: Build-Release.ps1 Changes

### What stays

| Category              | Examples                                  |
|-----------------------|-------------------------------------------|
| UCRT forwarders       | api-ms-win-crt-*.dll (from downlevel)     |
| VC++ runtime          | vcruntime140.dll, msvcp140.dll, etc.      |
| Conflicting-DLL purge | Remove dcomp, d2d1, d3d11, dwrite, dxgi, dwmapi, win32u, uxtheme, VERSION |

### What gets removed

The entire `$systemDlls` array and its copy loop. Also the `-PeDepsSource`
parameter and `$peDepsDir` resolution logic -- they are no longer needed since
system DLLs are handled by the PE plugin.

DLLs removed from bundling (moved to PE plugin):

```
kernel.appcore.dll       powrprof.dll
WinTypes.dll             shcore.dll
rometadata.dll           Microsoft.Internal.WarpPal.dll
msvcp_win.dll            coremessaging.dll
CoreMessagingDataModel2.dll  InputHost.dll
ninput.dll               windows.ui.dll
twinapi.appcore.dll      TextShaping.dll
TextInputFramework.dll   bcp47langs.dll
mscms.dll                profapi.dll
userenv.dll              propsys.dll
urlmon.dll               xmllite.dll
iertutil.dll             UIAutomationCore.dll
WindowsCodecs.dll
```

### Result

The portable ZIP shrinks significantly and contains only files that ship with
Chronos regardless of target environment.

---

## Part 2: PhoenixPE Plugin (Chronos.script)

### Location

```
Projects/PhoenixPE/Applications/Backup & Imaging/Chronos.script
```

### Script structure

| Section               | Purpose                                     |
|-----------------------|---------------------------------------------|
| [Main]                | Title, Description, Author, Level=5, etc.   |
| [Variables]           | ProgramFolder, ProgramExe, DownloadURL      |
| [Interface]           | File picker, shortcuts, buttons             |
| [Process]             | Main build logic                            |
| [ExtractProgram]      | Decompress the Chronos ZIP                  |
| [RequireSystemDlls]   | RequireFileEx bulk extraction from WIM      |
| [DownloadProgram]     | WebGet from GitHub releases (future)        |
| [SetProgramArch]      | x64/arm64 architecture selection            |
| [LaunchProgram]       | Test/preview (run in live PE)               |
| [ClearDownloadCache]  | Remove cached downloads                     |
| [SetDefaultOptions]   | Reset interface to defaults                 |
| [ShowScriptInfo]      | Help / about dialog                         |

### Interface elements

- **fb_ProgramSource** -- FileBox (type 13, mode=file) for selecting the
  Chronos portable ZIP. Filter: `*.zip`.
- **cb_DownloadLatest** -- Checkbox to download from GitHub instead of using
  the file picker. Disabled by default (future feature).
- **txt_DownloadURL** -- Text field for the GitHub releases URL. Pre-filled
  with the expected URL pattern but hidden in Advanced options.
- **cb_DesktopShc / cb_StartMenuShc / cb_PinToTaskbar** -- Shortcut toggles.
- Standard buttons: Launch, Download, Purge cache, Set defaults, Advanced,
  Script info.

### Build logic ([Process])

```
1. Validate that a source ZIP is specified via the file picker
   OR cb_DownloadLatest is checked.
2. If cb_DownloadLatest, run [DownloadProgram] to fetch ZIP.
   Otherwise, use the file picker path directly.
3. Run [ExtractProgram] -- Decompress ZIP to %TargetPrograms%\Chronos.
4. Run [RequireSystemDlls] -- Bulk-extract 25 system DLLs from Install.wim
   using RequireFileEx,AppendList + RequireFileEx,ExtractList.
5. Create shortcuts (desktop / start menu / taskbar / start menu pin).
```

### System DLLs ([RequireSystemDlls])

All extracted with NOMUI flag (no MUI resources needed for binary DLLs):

```
RequireFileEx,AppendList,\Windows\System32\kernel.appcore.dll
RequireFileEx,AppendList,\Windows\System32\powrprof.dll
RequireFileEx,AppendList,\Windows\System32\WinTypes.dll
RequireFileEx,AppendList,\Windows\System32\shcore.dll
RequireFileEx,AppendList,\Windows\System32\rometadata.dll
RequireFileEx,AppendList,\Windows\System32\Microsoft.Internal.WarpPal.dll
RequireFileEx,AppendList,\Windows\System32\msvcp_win.dll
RequireFileEx,AppendList,\Windows\System32\coremessaging.dll
RequireFileEx,AppendList,\Windows\System32\CoreMessagingDataModel2.dll
RequireFileEx,AppendList,\Windows\System32\InputHost.dll
RequireFileEx,AppendList,\Windows\System32\ninput.dll
RequireFileEx,AppendList,\Windows\System32\windows.ui.dll
RequireFileEx,AppendList,\Windows\System32\twinapi.appcore.dll
RequireFileEx,AppendList,\Windows\System32\TextShaping.dll
RequireFileEx,AppendList,\Windows\System32\TextInputFramework.dll
RequireFileEx,AppendList,\Windows\System32\bcp47langs.dll
RequireFileEx,AppendList,\Windows\System32\mscms.dll
RequireFileEx,AppendList,\Windows\System32\profapi.dll
RequireFileEx,AppendList,\Windows\System32\userenv.dll
RequireFileEx,AppendList,\Windows\System32\propsys.dll
RequireFileEx,AppendList,\Windows\System32\urlmon.dll
RequireFileEx,AppendList,\Windows\System32\xmllite.dll
RequireFileEx,AppendList,\Windows\System32\iertutil.dll
RequireFileEx,AppendList,\Windows\System32\UIAutomationCore.dll
RequireFileEx,AppendList,\Windows\System32\WindowsCodecs.dll
RequireFileEx,ExtractList
```

These are extracted from the same Install.wim that PhoenixPE uses, so
versions always match the PE kernel and user32.

### Future enhancements

- **GitHub download**: Point `%DownloadURL%` at the latest GitHub release
  asset URL. Enable `cb_DownloadLatest` once releases are published.
- **Version detection**: Parse the ZIP filename to show the installed version.
- **ARM64 support**: Add architecture radio buttons when ARM64 PE support is
  needed.

---

## Execution Order

1. Update `Build-Release.ps1` (strip system DLLs from bundling).
2. Create `Chronos.script` in the PhoenixPE Applications folder.
3. Build Chronos with the updated script to produce a clean ZIP.
4. Open PEBakery, enable the Chronos plugin, point it at the ZIP.
5. Build PE image and test.

---

## Files Modified / Created

| File | Action |
|------|--------|
| scripts/Build-Release.ps1 | Modified -- remove system DLL bundling |
| Projects/PhoenixPE/Applications/Backup & Imaging/Chronos.script | Created |
| pe-plugin-plan.md | This file |
