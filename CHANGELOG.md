# Changelog

All notable changes to **RuleOpen** will be documented in this file.

## [1.0.1] - 2026-11-27

### Changed
- **Microsoft Store Release (`9NPTZJ83X1W9`)**: Published RuleOpen on the [Microsoft Store](https://apps.microsoft.com/detail/9NPTZJ83X1W9) using only `runFullTrust` (removed `unvirtualizedResources` and the separate headless `AppListEntry="none"` entry) with declarative `uap3:FileTypeAssociation` (77 common file types), `desktop10:IconHandler`, `desktop4:FileExplorerContextMenus`, and `uap5:AppExecutionAlias`.
- **Direct Windows Default Apps Deep-Link**: In Store builds, **Open Default Apps** now opens `ms-settings:defaultapps?registeredAUMID=<FamilyName>!RuleOpen.Desktop` to jump straight to RuleOpen in Windows 11 Settings.
- **Faster Store Startup**: Moved startup association/icon initialization off the UI thread to keep launch instantaneous.
- **Settings & About Polish**: Collapsed the **File Explorer context menu** expander by default, hid the classic HKCU context menu toggle in Store builds, added a Microsoft Store link to **About**, and displayed the app version dynamically from assembly metadata.
- **CI & Packaging**: Made the WiX installer and GitHub Actions release workflow resolve version numbers automatically from `Directory.Build.props` and Git release tags.

## [1.0.0] - 2026-11-23

### Added
- **Deterministic Rule Engine (`RuleOpen.Core`)**: Folder depth, exact-vs-recursive, explicit priority, and extension specificity precedence; Unicode & space support; folder-prefix collision protection; versioned atomic JSON config with corruption backup.
- **Lightweight Dispatcher (`RuleOpen.Dispatcher`)**: Zero-WinUI fast file dispatcher supporting single and multi-file activation, self-dispatch recursion prevention, editable extension & global fallbacks, recovery UI fallback, `--install-register`, and interactive ComCtl32 `TaskDialogIndirect` `--uninstall-cleanup`.
- **Dynamic Explorer Icons & Per-Extension ProgIDs (`RuleOpen.Windows` & `RuleOpen.ShellExtension`)**: Per-extension `RuleOpen.File.<ext>` ProgIDs with Windows 11 `UserChoiceLatest` detection, native C++20 `IExtractIconW` COM handler (`icon-routes.tsv`) displaying matched routed app icons per folder (default), 10-frame high-DPI (`16–256 px`) v3 extension badge icons, and fallback-app icon inheritance.
- **Windows Explorer Context Menu (`RuleOpen.ShellExtension`)**: Native C++20 `IExplorerCommand` COM handler with `IShellDispatch2::ShellExecute` unpackaged launch delegation for Sparse MSIX / Store compatibility and optional classic per-user menu fallback.
- **WinUI 3 Management App (`RuleOpen.Desktop`)**: Native Windows 11 Fluent UI with Mica backdrop, Light/Dark/System themes, Rules manager with live search, Rule Editor with inline app picker and overlap warnings, Rule Test diagnostics with drag-and-drop, customizable fallback pickers, Windows-style Settings, About, First-run onboarding, and Recovery dialog.
- **Dual Packaging & CI (`RuleOpen.Packaging`, `scripts/package-release.ps1`)**: Microsoft Store Full MSIX (`PaulBrinkmann.RuleOpen`) using `runFullTrust` with declarative `uap3:FileTypeAssociation`, `desktop10:IconHandler`, and `AppExecutionAlias`, WiX v6 per-user MSI installer with same-version upgrade support and finish launch prompt, Sparse MSIX package, portable ZIP packaging script, `PRIVACY.md`, and GitHub Actions workflow.
