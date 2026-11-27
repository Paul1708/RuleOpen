# Changelog

All notable changes to **RuleOpen** will be documented in this file.

## [1.0.0] - 2026-11-23

### Added
- **Deterministic Rule Engine (`RuleOpen.Core`)**: Folder depth, exact-vs-recursive, explicit priority, and extension specificity precedence; Unicode & space support; folder-prefix collision protection; versioned atomic JSON config with corruption backup.
- **Lightweight Dispatcher (`RuleOpen.Dispatcher`)**: Zero-WinUI fast file dispatcher supporting single and multi-file activation, self-dispatch recursion prevention, editable extension & global fallbacks, recovery UI fallback, `--install-register`, and interactive ComCtl32 `TaskDialogIndirect` `--uninstall-cleanup`.
- **Dynamic Explorer Icons & Per-Extension ProgIDs (`RuleOpen.Windows` & `RuleOpen.ShellExtension`)**: Per-extension `RuleOpen.File.<ext>` ProgIDs with Windows 11 `UserChoiceLatest` detection, native C++20 `IExtractIconW` COM handler (`icon-routes.tsv`) displaying matched routed app icons per folder (default), 10-frame high-DPI (`16–256 px`) v3 extension badge icons, and fallback-app icon inheritance.
- **Windows Explorer Context Menu (`RuleOpen.ShellExtension`)**: Native C++20 `IExplorerCommand` COM handler with `IShellDispatch2::ShellExecute` unpackaged launch delegation for Sparse MSIX / Store compatibility and optional classic per-user menu fallback.
- **WinUI 3 Management App (`RuleOpen.Desktop`)**: Native Windows 11 Fluent UI with Mica backdrop, Light/Dark/System themes, Rules manager with live search, Rule Editor with inline app picker and overlap warnings, Rule Test diagnostics with drag-and-drop, customizable fallback pickers, Windows-style Settings, About, First-run onboarding, and Recovery dialog.
- **Dual Packaging & CI (`RuleOpen.Packaging`, `scripts/package-release.ps1`)**: Microsoft Store Full MSIX (`PaulBrinkmann.RuleOpen`) using `runFullTrust` with declarative `uap3:FileTypeAssociation`, `desktop10:IconHandler`, and `AppExecutionAlias`, WiX v6 per-user MSI installer with same-version upgrade support and finish launch prompt, Sparse MSIX package, portable ZIP packaging script, `PRIVACY.md`, and GitHub Actions workflow.
