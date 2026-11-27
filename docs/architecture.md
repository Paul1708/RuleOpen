# RuleOpen Architecture Decision Record (ADR)

## 1. Deployment & Windows Integration Strategy

### Decision
Ship **RuleOpen** via **two complementary distribution formats** built from one codebase:

1. **Microsoft Store Full MSIX (`PaulBrinkmann.RuleOpen`, `AppxManifest.Store.xml`)**:
   - Uses only `runFullTrust` (no `unvirtualizedResources` restricted capability required), signed automatically by the Microsoft Store with trusted identity (`CN=1C346476-CE87-4371-A043-7D1DFD06F837`), registering the **Windows 11 modern context menu** out of the box with zero Developer Mode setup.
   - Declaratively registers 77 common file extensions via `<uap:Extension Category="windows.fileTypeAssociation" Executable="RuleOpen.Dispatcher.exe">` (`uap3:FileTypeAssociation` + `<desktop10:IconHandler Clsid="7B42A9F1-4D8E-4E2A-B8E1-9E44C3B10110" />`), pointing double-click activations directly to the lightweight dispatcher and reading icon routes from the package's unvirtualized `LocalState` folder (`ApplicationData.Current.LocalFolder`).
   - Deep-links directly to `ms-settings:defaultapps?registeredAUMID=<FamilyName>!RuleOpen.Desktop` and exposes `RuleOpen.Dispatcher.exe` via `<uap5:AppExecutionAlias>`.

2. **GitHub Releases Unpackaged Desktop Build (WiX v6 per-user `.msi` & portable `.zip` + Sparse MSIX `RuleOpen.ShellPackage.msix`)**:
   - Dedicated lightweight **zero-WinUI dispatcher executable** (`RuleOpen.Dispatcher.exe`) installed alongside `RuleOpen.Desktop.exe` under `%LocalAppData%\Programs\RuleOpen\`.
   - In-place same-version MSI upgrades (`AllowSameVersionUpgrades="yes"`) and interactive uninstall cleanup (`--uninstall-cleanup` via ComCtl32 `TaskDialogIndirect`) asking whether to remove `%LocalAppData%\RuleOpen\` user data and offering to open Windows Default Apps.
   - Optional Sparse MSIX (`AppxManifest.xml`, `uap10:AllowExternalContent="true"`) + optional per-user Classic menu toggle (`HKCU\Software\Classes\*\shell\RuleOpen`).

### Why Hybrid Capabilities + Store Full MSIX (Instead of Static `uap:FileTypeAssociation` Only)

1. **Dynamic Extensions Without Manifest Rebuilds**: Static MSIX `<uap:FileTypeAssociation>` entries must be frozen at compile time. Writing per-extension ProgIDs (`RuleOpen.File.<ext>`) to unvirtualized `HKCU\Software\Classes` and `HKCU\Software\RegisteredApplications` lets users register any custom extension dynamically at runtime.
2. **Fast Dispatcher Isolation**: Double-clicking files invokes `RuleOpen.Dispatcher.exe` directly via `shell\open\command` without loading WinUI 3 or showing splash screens (`~29 MB` peak RAM).
3. **Startup Path & Package Reconciliation**: `PerUserFileAssociationService.EnsureRegistered` automatically refreshes `InprocServer32`, `shell\open\command` (using `%LocalAppData%\Microsoft\WindowsApps\RuleOpen.Dispatcher.exe` inside the Store package), and stale icon caches across installs and upgrades.

## 2. Respecting Windows 11 UserChoice & Dynamic Explorer Icons

Windows protects `UserChoice` / `UserChoiceLatest` hashes via `UCPD.sys` to stop silent association hijacking. RuleOpen strictly follows Microsoft's model:
- Registers per-extension ProgIDs (`HKCU\Software\Classes\RuleOpen.File.<ext>`), `HKCU\Software\RuleOpen\Capabilities\FileAssociations`, and `OpenWithProgids`.
- Never writes or tampers with `UserChoice` or `UserChoiceLatest`.
- Inspects Windows 11 `UserChoiceLatest\ProgId` ahead of legacy `UserChoice\ProgId` and `AssocQueryString`, filtering out Windows noise (`Unknown`, `OpenWith.exe`, generic shell icons).
- Captures the prior default handler *before* switching an extension to RuleOpen, preserving it as a customizable per-extension fallback.
- Deep-links cleanly to `ms-settings:defaultapps?registeredAppUser=RuleOpen`.

### Explorer File Icon Modes (`ExplorerIconMode`)
- **`MatchedRoutedAppIcon` (Default)**: Registers `RuleOpen.ShellExtension.dll`'s native C++20 `IPersistFile` + `IExtractIconW` COM server (`CLSID_RuleOpenIconHandler = {7B42A9F1-4D8E-4E2A-B8E1-9E44C3B10110}`, `GIL_PERINSTANCE`) under each ProgID's `ShellEx\IconHandler`, reading pre-sorted rules from an atomic TSV lookup table (`%LocalAppData%\RuleOpen\icon-routes.tsv`) so Explorer shows the target app icon per folder rule (or badge/fallback outside ruled folders).
- **`RuleOpenExtensionBadges`**: Generates 10-frame high-DPI (`16–256 px` PNG-in-ICO) category-colored Fluent document + extension badge icons (`ro_v3_<ext>.ico`) via `ExtensionBadgeIconGenerator`.
- **`InheritFallbackIcon`**: Points each ProgID's `DefaultIcon` directly to the captured or customized fallback application's document icon.

## 3. Application Discovery, Launching & Loop Protection

- **Discovery**: `SHAssocEnumHandlers` + `IObjectWithAppUserModelID` + `IObjectWithProgID` + `IAssocHandler`, filtering out `RuleOpen.File*` / `RuleOpen.*.exe` and shell noise (`openwith.exe`, `rundll32.exe`).
- **Icons**: Resolves UWP/MSIX PRI strings (`@{...}`) via `SHLoadIndirectString` directly to PNGs (wrapped into `.ico` for Shell icon handlers when needed via `ExtensionBadgeIconGenerator.EnsureWrappedPngIcon`); extracts Win32 icon resources via `PrivateExtractIcons` / `GetIconInfo` / `GetDIBits`.
- **Launching**:
  - Registered handlers: `SHCreateItemFromParsingName` → `BHID_DataObject` → `IAssocHandler::CreateInvoker` / `IAssocHandler::Invoke` (on STA), falling back to `IApplicationActivationManager::ActivateForFile` for AUMIDs or validated `ProcessStartInfo.ArgumentList` for desktop paths.
  - Custom executables: validated full path + `ProcessStartInfo.ArgumentList` (no shell command string interpolation).
- **COM Surrogate Breakaway (`ExplorerCommands.cpp`)**: Context menu verbs invoked inside the packaged `dllhost.exe` COM surrogate delegate process creation to unpackaged Windows Explorer via `IShellDispatch2::ShellExecute` (`CLSID_ShellWindows` / `SWC_DESKTOP`), preventing `RuleOpen.Desktop.exe` and `RuleOpen.Dispatcher.exe` from failing with `0xc000027b` (`APPMODEL_ERROR_NO_PACKAGE`) in Sparse MSIX mode.
- **Recursion protection**:
  1. Validator & discovery exclude RuleOpen identifiers.
  2. `ShellApplicationLauncher.IsSelfTarget` rejects self-targeting before launch.
  3. Process environment chain (`RULEOPEN_DISPATCH_DEPTH` ≥ 2 or `RULEOPEN_DISPATCH_CHAIN` duplicate path) stops indirect re-entry immediately.

## 4. Solution Module Boundaries

| Project | Target | Responsibility |
| --- | --- | --- |
| `RuleOpen.Core` | `net10.0` | Pure models, `RuleEngine`, `RuleValidator`, `JsonConfigurationStore` (source-gen JSON + atomic write + mutex), `DispatchCoordinator`, `RollingFileLogger` |
| `RuleOpen.Windows` | `net10.0-windows10.0.26100.0` | `CsWin32` Shell discovery, icon extraction, v3 badge `.ico` generator, handler launching, HKCU per-extension ProgIDs & icon route sync, Store & context menu service |
| `RuleOpen.Dispatcher` | `net10.0-windows10.0.26100.0` (`WinExe`) | Zero-WinUI fast file dispatcher + `--install-register` + interactive `--uninstall-cleanup` (`TaskDialogIndirect`) |
| `RuleOpen.Desktop` | `net10.0-windows10.0.26100.0` (`WinUI 3`) | Fluent Windows 11 management UI (`Rules`, `Test`, `Settings`, `About`, dialogs) |
| `RuleOpen.ShellExtension` | Native C++20 DLL + Sparse/Store MSIX | `IExplorerCommand` + `IEnumExplorerCommand` + `IObjectWithSite`, `IExtractIconW` (`IconHandler`), `AppxManifest.xml` (Sparse) & `AppxManifest.Store.xml` (Full Store) |
| `RuleOpen.Packaging` | WiX Toolset v6 | Per-user MSI installer (`%LocalAppData%\Programs\RuleOpen\`) with in-place upgrade & interactive uninstall cleanup |
