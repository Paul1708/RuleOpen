using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using RuleOpen.Core.Services;
using RuleOpen.Windows.Applications;
using RuleOpen.Windows.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace RuleOpen.Windows.Associations;

/// <summary>
/// Registers RuleOpen as an eligible per-user file handler in Windows 11 using
/// official HKCU <c>RegisteredApplications</c>, <c>Capabilities\FileAssociations</c>,
/// per-extension <c>RuleOpen.File.&lt;ext&gt;</c> ProgIDs, optional per-folder
/// <c>IExtractIconW</c> COM icon handler, and <c>OpenWithProgids</c> entries —
/// never bypassing or forging <c>UserChoice</c>.
/// </summary>
public sealed class PerUserFileAssociationService : IFileAssociationService
{
    public const string LegacyRuleOpenProgId = "RuleOpen.File";
    public const string RuleOpenProgId = LegacyRuleOpenProgId;
    public const string IconHandlerClsid = "{7B42A9F1-4D8E-4E2A-B8E1-9E44C3B10110}";

    private const string ProgIdPrefix = LegacyRuleOpenProgId + ".";
    private const string RegisteredAppName = "RuleOpen";
    private const string CapabilitiesSubKey = @"Software\RuleOpen\Capabilities";
    private const string FileAssociationsSubKey = @"Software\RuleOpen\Capabilities\FileAssociations";
    private const string RegisteredAppsSubKey = @"Software\RegisteredApplications";
    private const string IconHandlerClsidSubKey = @"Software\Classes\CLSID\" + IconHandlerClsid;

    private static readonly string[] StarterExtensions =
        [".pdf", ".md", ".markdown", ".txt", ".json", ".csv"];

    public static string GetProgIdForExtension(string normalizedExtension)
    {
        string norm = ExtensionNormalizer.TryNormalize(normalizedExtension, out string clean)
            ? clean
            : normalizedExtension;

        string raw = norm.Trim().TrimStart('.');
        Span<char> buf = stackalloc char[raw.Length];
        int count = 0;
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                buf[count++] = char.ToLowerInvariant(ch);
            }
            else
            {
                buf[count++] = '_';
            }
        }

        string suffix = count > 0 ? new string(buf[..count]) : "file";
        return ProgIdPrefix + suffix;
    }

    public static bool IsRuleOpenProgId(string? progId) =>
        !string.IsNullOrWhiteSpace(progId) &&
        (string.Equals(progId, LegacyRuleOpenProgId, StringComparison.OrdinalIgnoreCase) ||
         progId.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSelfTargetProgIdOrExe(ApplicationTarget target) =>
        IsRuleOpenProgId(target.ProgId) ||
        ShellApplicationDiscoveryService.IsSelfHandler(
            target.ApplicationId,
            target.DisplayName,
            target.ProgId,
            target.AppUserModelId,
            target.ExecutablePath);

    public IReadOnlyList<string> GetCuratedExtensions() => CuratedFileTypes.DefaultSuggestedExtensions;

    public string GetFriendlyTypeName(string extension) => CuratedFileTypes.GetFriendlyName(extension);

    public IReadOnlyList<FileAssociationStatus> GetStatusForExtensions(
        IEnumerable<string> extensions,
        RuleOpenConfig config)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(config);

        HashSet<string> registeredInRegistry = ReadRegisteredExtensionsFromRegistry();
        List<FileAssociationStatus> results = [];

        foreach (string ext in ExtensionNormalizer.NormalizeMany(extensions))
        {
            (string? defaultProgId, string? defaultFriendlyName, bool isRuleOpenDefault) = QueryDefaultState(ext);
            config.ExtensionFallbacks.TryGetValue(ext, out ApplicationTarget? capturedFallback);

            results.Add(new FileAssociationStatus
            {
                Extension = ext,
                FriendlyTypeName = GetFriendlyTypeName(ext),
                IsRegisteredWithWindows = registeredInRegistry.Contains(ext),
                IsRuleOpenDefault = isRuleOpenDefault,
                CurrentDefaultProgId = defaultProgId,
                CurrentDefaultDisplayName = defaultFriendlyName,
                CapturedFallbackDisplayName = capturedFallback?.DisplayName
            });
        }

        return results;
    }

    public void RegisterExtensions(IEnumerable<string> extensions, RuleOpenConfig config)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<string> normalized = ExtensionNormalizer.NormalizeMany(extensions);
        if (normalized.Count == 0)
        {
            return;
        }

        string dispatcherExe = ResolveDispatcherExecutablePath();
        string appIconPath = ResolveIconLocation(dispatcherExe);
        string openCommand = $"\"{dispatcherExe}\" -- \"%1\"";

        using (RegistryKey capKey = Registry.CurrentUser.CreateSubKey(CapabilitiesSubKey))
        {
            capKey.SetValue("ApplicationName", RegisteredAppName, RegistryValueKind.String);
            capKey.SetValue(
                "ApplicationDescription",
                "Opens files with different apps based on folder and file type rules.",
                RegistryValueKind.String);
            capKey.SetValue("ApplicationIcon", appIconPath, RegistryValueKind.String);
        }

        using (RegistryKey regApps = Registry.CurrentUser.CreateSubKey(RegisteredAppsSubKey))
        {
            regApps.SetValue(RegisteredAppName, CapabilitiesSubKey, RegistryValueKind.String);
        }

        using RegistryKey fileAssocKey = Registry.CurrentUser.CreateSubKey(FileAssociationsSubKey);
        bool keepLegacyProgId = false;

        foreach (string ext in normalized)
        {
            if (!config.ExtensionFallbacks.ContainsKey(ext))
            {
                ApplicationTarget? previousDefault = CaptureCurrentDefaultHandler(ext);
                if (previousDefault is not null)
                {
                    config.ExtensionFallbacks[ext] = previousDefault;
                }
            }

            config.ExtensionFallbacks.TryGetValue(ext, out ApplicationTarget? fallback);
            string progId = GetProgIdForExtension(ext);
            string defaultIcon = ExtensionIconResolver.ResolveDefaultIcon(
                ext,
                fallback,
                config.Settings.ExplorerIconMode,
                appIconPath);
            string friendlyType = $"{GetFriendlyTypeName(ext)} (RuleOpen)";

            WriteExtensionProgId(progId, friendlyType, appIconPath, defaultIcon, openCommand);

            fileAssocKey.SetValue(ext, progId, RegistryValueKind.String);

            using (RegistryKey openWithKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
            {
                openWithKey.SetValue(progId, string.Empty, RegistryValueKind.String);

                (string? activeProgId, _, _) = QueryDefaultState(ext);
                if (string.Equals(activeProgId, LegacyRuleOpenProgId, StringComparison.OrdinalIgnoreCase))
                {
                    keepLegacyProgId = true;
                }
                else
                {
                    openWithKey.DeleteValue(LegacyRuleOpenProgId, throwOnMissingValue: false);
                }
            }

            if (!config.Settings.RegisteredExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                config.Settings.RegisteredExtensions.Add(ext);
            }
        }

        IEnumerable<string> allTracked = ReadRegisteredExtensionsFromRegistry()
            .Concat(config.Settings.RegisteredExtensions)
            .Concat(config.Rules.SelectMany(r => r.Extensions))
            .Concat(normalized);

        if (!keepLegacyProgId)
        {
            foreach (string ext in ExtensionNormalizer.NormalizeMany(allTracked))
            {
                (string? activeProgId, _, _) = QueryDefaultState(ext);
                if (string.Equals(activeProgId, LegacyRuleOpenProgId, StringComparison.OrdinalIgnoreCase))
                {
                    keepLegacyProgId = true;
                    break;
                }
            }
        }

        if (keepLegacyProgId)
        {
            WriteExtensionProgId(
                LegacyRuleOpenProgId,
                "RuleOpen Managed File",
                appIconPath,
                appIconPath,
                openCommand);
        }
        else
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{LegacyRuleOpenProgId}", throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }

        SyncIconHandlerAndRoutes(config, allTracked, appIconPath);
        NotifyShellAssociationsChanged();
    }

    public void EnsureRegistered(RuleOpenConfig config, bool registerStarterExtensionsIfEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> extensions = config.Settings.RegisteredExtensions
            .Concat(config.Rules.SelectMany(r => r.Extensions))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extensions.Count == 0 && registerStarterExtensionsIfEmpty)
        {
            extensions = [.. StarterExtensions];
        }

        if (extensions.Count == 0)
        {
            return;
        }

        bool needsRegistration = false;
        bool needsIconRefresh = false;

        try
        {
            using RegistryKey? regApps = Registry.CurrentUser.OpenSubKey(RegisteredAppsSubKey, writable: false);
            if (!string.Equals(regApps?.GetValue(RegisteredAppName) as string, CapabilitiesSubKey, StringComparison.OrdinalIgnoreCase))
            {
                needsRegistration = true;
            }
        }
        catch
        {
            needsRegistration = true;
        }

        if (!needsRegistration)
        {
            using RegistryKey? assocKey = Registry.CurrentUser.OpenSubKey(FileAssociationsSubKey, writable: false);
            if (assocKey is null)
            {
                needsRegistration = true;
            }
            else
            {
                string expectedOpenCommand = $"\"{ResolveDispatcherExecutablePath()}\" -- \"%1\"";
                string expectedShellDll = ResolveExecutablePath("RuleOpen.ShellExtension.dll");

                bool iconRoutesNeedsRefresh = false;
                if (config.Settings.ExplorerIconMode == ExplorerIconMode.MatchedRoutedAppIcon)
                {
                    string routesPath = ConfigPaths.GetDefaultIconRoutesFilePath();
                    if (!File.Exists(routesPath))
                    {
                        iconRoutesNeedsRefresh = true;
                    }
                    else
                    {
                        try
                        {
                            string routesText = File.ReadAllText(routesPath);
                            if (routesText.Contains("ro_v2_", StringComparison.OrdinalIgnoreCase))
                            {
                                iconRoutesNeedsRefresh = true;
                            }
                        }
                        catch
                        {
                            iconRoutesNeedsRefresh = true;
                        }
                    }

                    if (!iconRoutesNeedsRefresh)
                    {
                        try
                        {
                            using RegistryKey? inprocKey = Registry.CurrentUser.OpenSubKey($@"{IconHandlerClsidSubKey}\InprocServer32", writable: false);
                            string? currentDll = inprocKey?.GetValue(null) as string;
                            if (!string.Equals(currentDll, expectedShellDll, StringComparison.OrdinalIgnoreCase))
                            {
                                iconRoutesNeedsRefresh = true;
                            }
                        }
                        catch
                        {
                            iconRoutesNeedsRefresh = true;
                        }
                    }
                }

                foreach (string rawExt in extensions)
                {
                    if (!ExtensionNormalizer.TryNormalize(rawExt, out string ext))
                    {
                        continue;
                    }

                    string expectedProgId = GetProgIdForExtension(ext);
                    string? actualProgId = assocKey.GetValue(ext) as string;
                    if (!string.Equals(actualProgId, expectedProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        needsRegistration = true;
                        break;
                    }

                    using RegistryKey? progIdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{expectedProgId}", writable: false);
                    if (progIdKey is null)
                    {
                        needsRegistration = true;
                        break;
                    }

                    using RegistryKey? openCmdKey = progIdKey.OpenSubKey(@"shell\open\command", writable: false);
                    string? actualOpenCommand = openCmdKey?.GetValue(null) as string;
                    if (!string.Equals(actualOpenCommand, expectedOpenCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        needsRegistration = true;
                        break;
                    }

                    using RegistryKey? iconKey = progIdKey.OpenSubKey("DefaultIcon", writable: false);
                    string? currentDefaultIcon = iconKey?.GetValue(null) as string;
                    using RegistryKey? shellExKey = progIdKey.OpenSubKey(@"ShellEx\IconHandler", writable: false);
                    bool hasIconHandler = string.Equals(shellExKey?.GetValue(null) as string, IconHandlerClsid, StringComparison.OrdinalIgnoreCase);

                    if (config.Settings.ExplorerIconMode == ExplorerIconMode.MatchedRoutedAppIcon)
                    {
                        if (!string.Equals(currentDefaultIcon, "%1", StringComparison.Ordinal) ||
                            !hasIconHandler ||
                            iconRoutesNeedsRefresh)
                        {
                            needsIconRefresh = true;
                        }
                    }
                    else
                    {
                        if (hasIconHandler || string.Equals(currentDefaultIcon, "%1", StringComparison.Ordinal))
                        {
                            needsIconRefresh = true;
                        }
                        else if (config.Settings.ExplorerIconMode == ExplorerIconMode.RuleOpenExtensionBadges &&
                                 (string.IsNullOrWhiteSpace(currentDefaultIcon) ||
                                  !currentDefaultIcon.Contains("ro_v3_", StringComparison.OrdinalIgnoreCase) ||
                                  !File.Exists(currentDefaultIcon)))
                        {
                            needsIconRefresh = true;
                        }
                    }
                }
            }
        }

        if (needsRegistration)
        {
            RegisterExtensions(extensions, config);
        }
        else if (needsIconRefresh)
        {
            RefreshExtensionIcons(extensions, config);
        }
    }

    public void RefreshExtensionIcons(IEnumerable<string> extensions, RuleOpenConfig config)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<string> normalized = ExtensionNormalizer.NormalizeMany(extensions);
        if (normalized.Count == 0)
        {
            return;
        }

        string dispatcherExe = ResolveDispatcherExecutablePath();
        string appIconPath = ResolveIconLocation(dispatcherExe);
        bool updatedAny = false;

        foreach (string ext in normalized)
        {
            string progId = GetProgIdForExtension(ext);
            using RegistryKey? progIdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}", writable: true);
            if (progIdKey is null)
            {
                continue;
            }

            config.ExtensionFallbacks.TryGetValue(ext, out ApplicationTarget? fallback);
            string defaultIcon = ExtensionIconResolver.ResolveDefaultIcon(
                ext,
                fallback,
                config.Settings.ExplorerIconMode,
                appIconPath);

            using RegistryKey iconKey = progIdKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue(null, defaultIcon, RegistryValueKind.String);
            updatedAny = true;
        }

        IEnumerable<string> allTracked = ReadRegisteredExtensionsFromRegistry()
            .Concat(config.Settings.RegisteredExtensions)
            .Concat(config.Rules.SelectMany(r => r.Extensions))
            .Concat(normalized);

        if (SyncIconHandlerAndRoutes(config, allTracked, appIconPath) || updatedAny)
        {
            NotifyShellAssociationsChanged();
        }
    }

    public IReadOnlyList<FileAssociationStatus> GetActiveRuleOpenDefaultExtensions(RuleOpenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        IEnumerable<string> all = ReadRegisteredExtensionsFromRegistry()
            .Concat(config.Settings.RegisteredExtensions)
            .Concat(config.Rules.SelectMany(r => r.Extensions))
            .Concat(CuratedFileTypes.DefaultSuggestedExtensions);

        return GetStatusForExtensions(all, config)
            .Where(s => s.IsRuleOpenDefault)
            .ToList();
    }

    public void UnregisterExtension(string extension, RuleOpenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!ExtensionNormalizer.TryNormalize(extension, out string normExt))
        {
            return;
        }

        string progId = GetProgIdForExtension(normExt);

        using (RegistryKey? fileAssocKey = Registry.CurrentUser.OpenSubKey(FileAssociationsSubKey, writable: true))
        {
            fileAssocKey?.DeleteValue(normExt, throwOnMissingValue: false);
        }

        using (RegistryKey? openWithKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{normExt}\OpenWithProgids", writable: true))
        {
            openWithKey?.DeleteValue(progId, throwOnMissingValue: false);
            openWithKey?.DeleteValue(LegacyRuleOpenProgId, throwOnMissingValue: false);
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
        }
        catch
        {
        }

        config.Settings.RegisteredExtensions.RemoveAll(e => string.Equals(e, normExt, StringComparison.OrdinalIgnoreCase));

        if (config.Settings.ExplorerIconMode == ExplorerIconMode.MatchedRoutedAppIcon)
        {
            string dispatcherExe = ResolveDispatcherExecutablePath();
            string appIconPath = ResolveIconLocation(dispatcherExe);
            SyncIconHandlerAndRoutes(config, ReadRegisteredExtensionsFromRegistry().Concat(config.Settings.RegisteredExtensions), appIconPath);
        }

        NotifyShellAssociationsChanged();
    }

    public ApplicationTarget? CaptureCurrentDefaultHandler(string extension)
    {
        if (!ExtensionNormalizer.TryNormalize(extension, out string normExt))
        {
            return null;
        }

        (string? progId, string? friendlyName, bool isRuleOpenDefault) = QueryDefaultState(normExt);
        if (isRuleOpenDefault ||
            string.Equals(progId, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string assocTarget = !string.IsNullOrWhiteSpace(progId) ? progId : normExt;
        string? aumid = QueryAssocString(assocTarget, ASSOCSTR.ASSOCSTR_APPID)
                        ?? QueryAssocString(normExt, ASSOCSTR.ASSOCSTR_APPID);
        string? exe = QueryAssocString(assocTarget, ASSOCSTR.ASSOCSTR_EXECUTABLE)
                      ?? QueryAssocString(normExt, ASSOCSTR.ASSOCSTR_EXECUTABLE);
        string? resolvedFriendly = friendlyName
                                   ?? QueryAssocString(assocTarget, ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME)
                                   ?? QueryAssocString(normExt, ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME);

        if (string.IsNullOrWhiteSpace(progId) &&
            string.IsNullOrWhiteSpace(aumid) &&
            string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        if (ShellApplicationDiscoveryService.IsSelfHandler(null, resolvedFriendly, progId, aumid, exe) ||
            ShellApplicationDiscoveryService.IsSystemNoise(null, resolvedFriendly, exe))
        {
            return null;
        }

        string display = !string.IsNullOrWhiteSpace(resolvedFriendly)
            ? resolvedFriendly
            : !string.IsNullOrWhiteSpace(exe) ? Path.GetFileNameWithoutExtension(exe) : (progId ?? "Previous Default");

        string stableId = !string.IsNullOrWhiteSpace(aumid) ? $"aumid:{aumid}" :
            !string.IsNullOrWhiteSpace(progId) ? $"progid:{progId}" :
            $"exe:{exe}";

        return new ApplicationTarget
        {
            Kind = ApplicationTargetKind.RegisteredHandler,
            ApplicationId = stableId,
            DisplayName = display,
            ExecutablePath = exe,
            ProgId = progId,
            AppUserModelId = aumid
        };
    }

    public void OpenWindowsDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(RegisteredAppName)}",
                UseShellExecute = true
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true
            });
        }
    }

    public void RemoveAllRegistrations()
    {
        HashSet<string> extensionsToClean = ReadRegisteredExtensionsFromRegistry();
        foreach (string curated in CuratedFileTypes.DefaultSuggestedExtensions)
        {
            extensionsToClean.Add(curated);
        }

        List<string> progIdsToDelete = [LegacyRuleOpenProgId];
        try
        {
            using RegistryKey? classesKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: false);
            if (classesKey is not null)
            {
                foreach (string subKeyName in classesKey.GetSubKeyNames())
                {
                    if (IsRuleOpenProgId(subKeyName))
                    {
                        if (!progIdsToDelete.Contains(subKeyName, StringComparer.OrdinalIgnoreCase))
                        {
                            progIdsToDelete.Add(subKeyName);
                        }

                        if (subKeyName.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            string extSuffix = "." + subKeyName[ProgIdPrefix.Length..];
                            if (ExtensionNormalizer.TryNormalize(extSuffix, out string normExt))
                            {
                                extensionsToClean.Add(normExt);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        foreach (string ext in extensionsToClean)
        {
            string perExtProgId = GetProgIdForExtension(ext);
            try
            {
                using RegistryKey? openWithKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                if (openWithKey is not null)
                {
                    openWithKey.DeleteValue(LegacyRuleOpenProgId, throwOnMissingValue: false);
                    openWithKey.DeleteValue(perExtProgId, throwOnMissingValue: false);
                    foreach (string pid in progIdsToDelete)
                    {
                        openWithKey.DeleteValue(pid, throwOnMissingValue: false);
                    }
                }
            }
            catch
            {
            }
        }

        foreach (string pid in progIdsToDelete)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{pid}", throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(IconHandlerClsidSubKey, throwOnMissingSubKey: false);
        }
        catch
        {
        }

        try
        {
            string routesPath = ConfigPaths.GetDefaultIconRoutesFilePath();
            if (File.Exists(routesPath))
            {
                File.Delete(routesPath);
            }
        }
        catch
        {
        }

        try
        {
            using RegistryKey? regApps = Registry.CurrentUser.OpenSubKey(RegisteredAppsSubKey, writable: true);
            regApps?.DeleteValue(RegisteredAppName, throwOnMissingValue: false);
        }
        catch
        {
        }

        // Only delete Capabilities — never touch HKCU\Software\RuleOpen\Installer while MSI uninstall runs.
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(CapabilitiesSubKey, throwOnMissingSubKey: false);
        }
        catch
        {
        }

        NotifyShellAssociationsChanged();
    }

    internal static string ResolveDispatcherExecutablePath()
    {
        if (ContextMenuRegistrationService.IsRunningAsStorePackage())
        {
            string aliasPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "RuleOpen.Dispatcher.exe");
            if (File.Exists(aliasPath))
            {
                return aliasPath;
            }
        }

        return ResolveExecutablePath("RuleOpen.Dispatcher.exe");
    }

    public static string ResolveExecutablePath(string exeName)
    {
        string direct = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(direct))
        {
            return direct;
        }

        // During local developer/test runs from bin\..., check sibling and src\ project output directories.
        string projectName = Path.GetFileNameWithoutExtension(exeName);
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string?[] relRoots = [null, "src"];

        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (string? relRoot in relRoots)
            {
                string projBase = relRoot is null
                    ? Path.Combine(dir.FullName, projectName, "bin")
                    : Path.Combine(dir.FullName, relRoot, projectName, "bin");

                foreach (string candidate in new[]
                {
                    Path.Combine(projBase, "x64", "Release", "net10.0-windows10.0.26100.0", exeName),
                    Path.Combine(projBase, "Release", "net10.0-windows10.0.26100.0", exeName),
                    Path.Combine(projBase, "Debug", "net10.0-windows10.0.26100.0", exeName),
                    Path.Combine(projBase, "x64", "Release", exeName),
                    Path.Combine(projBase, "x64", "Debug", exeName)
                })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return direct;
    }

    private static bool SyncIconHandlerAndRoutes(
        RuleOpenConfig config,
        IEnumerable<string> trackedExtensions,
        string appIconPath)
    {
        IReadOnlyList<string> normalized = ExtensionNormalizer.NormalizeMany(trackedExtensions);

        if (config.Settings.ExplorerIconMode == ExplorerIconMode.MatchedRoutedAppIcon)
        {
            string shellDll = ResolveExecutablePath("RuleOpen.ShellExtension.dll");
            if (!File.Exists(shellDll))
            {
                return false;
            }

            using (RegistryKey inproc = Registry.CurrentUser.CreateSubKey($@"{IconHandlerClsidSubKey}\InprocServer32"))
            {
                inproc.SetValue(null, shellDll, RegistryValueKind.String);
                inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
            }

            WriteIconRoutesTable(config, normalized, appIconPath);

            foreach (string ext in normalized)
            {
                string progId = GetProgIdForExtension(ext);
                using RegistryKey? progIdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}", writable: true);
                if (progIdKey is null)
                {
                    continue;
                }

                using (RegistryKey iconKey = progIdKey.CreateSubKey("DefaultIcon"))
                {
                    iconKey.SetValue(null, "%1", RegistryValueKind.String);
                }

                using RegistryKey shellEx = progIdKey.CreateSubKey(@"ShellEx\IconHandler");
                shellEx.SetValue(null, IconHandlerClsid, RegistryValueKind.String);
            }

            return true;
        }

        bool changed = false;

        try
        {
            using RegistryKey? clsidKey = Registry.CurrentUser.OpenSubKey(IconHandlerClsidSubKey, writable: false);
            if (clsidKey is not null)
            {
                Registry.CurrentUser.DeleteSubKeyTree(IconHandlerClsidSubKey, throwOnMissingSubKey: false);
                changed = true;
            }
        }
        catch
        {
        }

        try
        {
            string routesPath = ConfigPaths.GetDefaultIconRoutesFilePath();
            if (File.Exists(routesPath))
            {
                File.Delete(routesPath);
                changed = true;
            }
        }
        catch
        {
        }

        foreach (string ext in normalized)
        {
            string progId = GetProgIdForExtension(ext);
            using RegistryKey? progIdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}", writable: true);
            if (progIdKey is null)
            {
                continue;
            }

            try
            {
                using RegistryKey? existingHandler = progIdKey.OpenSubKey(@"ShellEx\IconHandler", writable: false);
                if (existingHandler is not null)
                {
                    progIdKey.DeleteSubKeyTree("ShellEx", throwOnMissingSubKey: false);
                    changed = true;
                }
            }
            catch
            {
            }
        }

        return changed;
    }

    private static void WriteIconRoutesTable(
        RuleOpenConfig config,
        IReadOnlyList<string> trackedExtensions,
        string appIconPath)
    {
        List<RuleMatchCandidate> orderedRules = [];
        foreach (OpeningRule rule in config.Rules)
        {
            if (!rule.Enabled || !PathNormalizer.TryNormalizeFolderPath(rule.FolderPath, out string normFolder))
            {
                continue;
            }

            orderedRules.Add(new RuleMatchCandidate
            {
                Rule = rule,
                NormalizedRuleFolder = normFolder,
                FolderDepth = PathNormalizer.GetFolderDepth(normFolder),
                IsExactFolderMatch = !rule.IncludeSubfolders,
                Matched = true,
                Reason = "Icon route ordering"
            });
        }

        orderedRules.Sort(new RulePrecedenceComparer(config.Settings.ConflictResolutionMode));

        StringBuilder sb = new();
        HashSet<string> allExtensions = new(trackedExtensions, StringComparer.OrdinalIgnoreCase);

        foreach (RuleMatchCandidate candidate in orderedRules)
        {
            string folderWithSlash = candidate.NormalizedRuleFolder.EndsWith('\\')
                ? candidate.NormalizedRuleFolder
                : candidate.NormalizedRuleFolder + "\\";
            string subFlag = candidate.Rule.IncludeSubfolders ? "1" : "0";

            foreach (string ext in ExtensionNormalizer.NormalizeMany(candidate.Rule.Extensions))
            {
                allExtensions.Add(ext);
                string fallbackBadge = ExtensionBadgeIconGenerator.EnsureExtensionIcon(ext) ?? appIconPath;
                (string iconPath, int iconIndex) = ExtensionIconResolver.ResolveShellIconForTarget(
                    ext,
                    candidate.Rule.TargetApplication,
                    fallbackBadge);
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"R\t{ext}\t{folderWithSlash}\t{subFlag}\t{SanitizeTsvField(iconPath)}\t{iconIndex}");
            }
        }

        foreach (string ext in config.ExtensionFallbacks.Keys)
        {
            if (ExtensionNormalizer.TryNormalize(ext, out string normExt))
            {
                allExtensions.Add(normExt);
            }
        }

        foreach (string ext in allExtensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            ApplicationTarget? fallback = config.ExtensionFallbacks.TryGetValue(ext, out ApplicationTarget? extFb)
                ? extFb
                : config.GlobalFallback;
            string fallbackBadge = ExtensionBadgeIconGenerator.EnsureExtensionIcon(ext) ?? appIconPath;
            (string iconPath, int iconIndex) = ExtensionIconResolver.ResolveShellIconForTarget(ext, fallback, fallbackBadge);
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"F\t{ext}\t{SanitizeTsvField(iconPath)}\t{iconIndex}");
        }

        string routesPath = ConfigPaths.GetDefaultIconRoutesFilePath();
        string dir = Path.GetDirectoryName(routesPath) ?? ".";
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $".icon-routes.tmp.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}");
        try
        {
            File.WriteAllText(tempPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, routesPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static string SanitizeTsvField(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static void WriteExtensionProgId(
        string progId,
        string friendlyTypeName,
        string appIconPath,
        string defaultIconPath,
        string openCommand)
    {
        using RegistryKey progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
        progIdKey.SetValue(null, friendlyTypeName, RegistryValueKind.String);
        progIdKey.SetValue("FriendlyTypeName", friendlyTypeName, RegistryValueKind.String);
        progIdKey.SetValue("AppUserModelID", "RuleOpen.Desktop", RegistryValueKind.String);

        using (RegistryKey appKey = progIdKey.CreateSubKey("Application"))
        {
            appKey.SetValue("ApplicationName", RegisteredAppName, RegistryValueKind.String);
            appKey.SetValue("ApplicationIcon", appIconPath, RegistryValueKind.String);
        }

        using (RegistryKey iconKey = progIdKey.CreateSubKey("DefaultIcon"))
        {
            iconKey.SetValue(null, defaultIconPath, RegistryValueKind.String);
        }

        using (RegistryKey cmdKey = progIdKey.CreateSubKey(@"shell\open\command"))
        {
            cmdKey.SetValue(null, openCommand, RegistryValueKind.String);
        }
    }

    private static string ResolveIconLocation(string dispatcherExe)
    {
        string icoInAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "RuleOpen.ico");
        if (File.Exists(icoInAssets))
        {
            return icoInAssets;
        }

        string dirAssets = Path.Combine(Path.GetDirectoryName(dispatcherExe) ?? AppContext.BaseDirectory, "Assets", "RuleOpen.ico");
        if (File.Exists(dirAssets))
        {
            return dirAssets;
        }

        string icoNextToExe = Path.Combine(Path.GetDirectoryName(dispatcherExe) ?? AppContext.BaseDirectory, "RuleOpen.ico");
        if (File.Exists(icoNextToExe))
        {
            return icoNextToExe;
        }

        return $"\"{dispatcherExe}\",0";
    }

    private static HashSet<string> ReadRegisteredExtensionsFromRegistry()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(FileAssociationsSubKey, writable: false);
        if (key is null)
        {
            return set;
        }

        foreach (string valueName in key.GetValueNames())
        {
            if (ExtensionNormalizer.TryNormalize(valueName, out string norm))
            {
                set.Add(norm);
            }
        }

        return set;
    }

    internal static (string? ProgId, string? FriendlyName, bool IsRuleOpenDefault) QueryDefaultState(string normalizedExtension)
    {
        string? userChoiceLatestProgId = null;
        string? userChoiceProgId = null;
        try
        {
            using RegistryKey? ucLatestKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{normalizedExtension}\UserChoiceLatest\ProgId",
                writable: false);
            userChoiceLatestProgId = ucLatestKey?.GetValue("ProgId") as string;

            using RegistryKey? ucKey = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{normalizedExtension}\UserChoice",
                writable: false);
            userChoiceProgId = ucKey?.GetValue("ProgId") as string;
        }
        catch
        {
        }

        string? assocProgId = QueryAssocString(normalizedExtension, ASSOCSTR.ASSOCSTR_PROGID);
        if (string.Equals(assocProgId, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            assocProgId = null;
        }

        string? effectiveProgId = !string.IsNullOrWhiteSpace(userChoiceLatestProgId) ? userChoiceLatestProgId :
            !string.IsNullOrWhiteSpace(assocProgId) ? assocProgId :
            userChoiceProgId;

        if (string.Equals(effectiveProgId, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            effectiveProgId = null;
        }

        string? friendlyName = !string.IsNullOrWhiteSpace(effectiveProgId) && !IsRuleOpenProgId(effectiveProgId)
            ? QueryAssocString(effectiveProgId, ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME)
              ?? QueryAssocString(normalizedExtension, ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME)
            : string.IsNullOrWhiteSpace(effectiveProgId)
                ? null
                : QueryAssocString(normalizedExtension, ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME);

        string? exeCheck = !string.IsNullOrWhiteSpace(effectiveProgId)
            ? QueryAssocString(effectiveProgId, ASSOCSTR.ASSOCSTR_EXECUTABLE)
            : null;
        if (ShellApplicationDiscoveryService.IsSystemNoise(null, friendlyName, exeCheck))
        {
            friendlyName = null;
            effectiveProgId = null;
        }

        string? command = !string.IsNullOrWhiteSpace(effectiveProgId)
            ? QueryAssocString(effectiveProgId, ASSOCSTR.ASSOCSTR_COMMAND)
              ?? QueryAssocString(normalizedExtension, ASSOCSTR.ASSOCSTR_COMMAND)
            : QueryAssocString(normalizedExtension, ASSOCSTR.ASSOCSTR_COMMAND);

        bool isRuleOpen =
            IsRuleOpenProgId(effectiveProgId) ||
            (!string.IsNullOrWhiteSpace(command) &&
             command.Contains("RuleOpen.Dispatcher.exe", StringComparison.OrdinalIgnoreCase));

        return (effectiveProgId, friendlyName, isRuleOpen);
    }

    internal static string? QueryAssocString(string extensionOrProgId, ASSOCSTR str)
    {
        uint len = 0;
        HRESULT hr = PInvoke.AssocQueryString(
            ASSOCF.ASSOCF_NOTRUNCATE | ASSOCF.ASSOCF_NOFIXUPS,
            str,
            extensionOrProgId,
            null!,
            Span<char>.Empty,
            ref len);

        if (len == 0 || (hr.Failed && hr.Value != 1 && hr.Value != unchecked((int)0x8007007A)))
        {
            return null;
        }

        char[] buffer = new char[len + 1];
        hr = PInvoke.AssocQueryString(
            ASSOCF.ASSOCF_NOTRUNCATE | ASSOCF.ASSOCF_NOFIXUPS,
            str,
            extensionOrProgId,
            null!,
            buffer,
            ref len);

        if (hr.Failed || len == 0)
        {
            return null;
        }

        int actualLen = Array.IndexOf(buffer, '\0');
        string raw = actualLen >= 0 ? new string(buffer, 0, actualLen) : new string(buffer, 0, (int)len);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static unsafe void NotifyShellAssociationsChanged()
    {
        PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
    }
}
