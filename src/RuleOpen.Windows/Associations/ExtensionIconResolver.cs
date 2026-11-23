using Microsoft.Win32;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using RuleOpen.Windows.Applications;
using Windows.Win32.UI.Shell;

namespace RuleOpen.Windows.Associations;

/// <summary>
/// Resolves per-extension and per-target File Explorer icons so RuleOpen-managed file types remain
/// visually distinct across all three display modes.
/// </summary>
public static class ExtensionIconResolver
{
    public static string ResolveDefaultIcon(
        string normalizedExtension,
        ApplicationTarget? fallback,
        ExplorerIconMode mode,
        string fallbackRuleOpenIconPath)
    {
        if (!ExtensionNormalizer.TryNormalize(normalizedExtension, out string normExt))
        {
            return fallbackRuleOpenIconPath;
        }

        if (mode == ExplorerIconMode.MatchedRoutedAppIcon)
        {
            return "%1";
        }

        if (mode == ExplorerIconMode.RuleOpenExtensionBadges)
        {
            return ExtensionBadgeIconGenerator.EnsureExtensionIcon(normExt) ?? fallbackRuleOpenIconPath;
        }

        // Mode == ExplorerIconMode.InheritFallbackIcon
        string? rawLocation = ResolveRawIconLocationForTarget(normExt, fallback);
        if (!string.IsNullOrWhiteSpace(rawLocation))
        {
            return rawLocation;
        }

        return ExtensionBadgeIconGenerator.EnsureExtensionIcon(normExt) ?? fallbackRuleOpenIconPath;
    }

    public static (string IconPath, int IconIndex) ResolveShellIconForTarget(
        string normalizedExtension,
        ApplicationTarget? target,
        string fallbackBadgeIconPath,
        string? iconsOutputDir = null)
    {
        if (!ExtensionNormalizer.TryNormalize(normalizedExtension, out string normExt))
        {
            return (fallbackBadgeIconPath, 0);
        }

        string? rawLocation = ResolveRawIconLocationForTarget(normExt, target);
        if (!string.IsNullOrWhiteSpace(rawLocation))
        {
            if (rawLocation.StartsWith("@{", StringComparison.Ordinal))
            {
                string? resolved = ShellIconExtractor.TryResolveIndirectString(rawLocation);
                if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                {
                    if (resolved.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        string? wrappedIco = ExtensionBadgeIconGenerator.EnsureWrappedPngIcon(resolved, iconsOutputDir);
                        if (!string.IsNullOrWhiteSpace(wrappedIco))
                        {
                            return (wrappedIco, 0);
                        }
                    }
                    else
                    {
                        (string path, int idx) = ShellIconExtractor.ParseIconLocation(resolved);
                        string? existing = ResolveExistingIconFile(path);
                        if (!string.IsNullOrWhiteSpace(existing))
                        {
                            return (existing, idx);
                        }
                    }
                }
            }
            else
            {
                (string parsedPath, int index) = ShellIconExtractor.ParseIconLocation(rawLocation);
                string? existing = ResolveExistingIconFile(parsedPath);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    if (existing.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        string? wrappedIco = ExtensionBadgeIconGenerator.EnsureWrappedPngIcon(existing, iconsOutputDir);
                        if (!string.IsNullOrWhiteSpace(wrappedIco))
                        {
                            return (wrappedIco, 0);
                        }
                    }
                    else
                    {
                        return (existing, index);
                    }
                }
            }
        }

        return (fallbackBadgeIconPath, 0);
    }

    internal static string? ResolveRawIconLocationForTarget(string normalizedExtension, ApplicationTarget? target)
    {
        if (target is not null && !PerUserFileAssociationService.IsSelfTargetProgIdOrExe(target))
        {
            // 1. Check target.ProgId DefaultIcon directly in HKCR.
            string? fromTargetProgId = TryReadProgIdDefaultIcon(target.ProgId);
            if (IsValidIconLocation(fromTargetProgId))
            {
                return fromTargetProgId!;
            }

            // 2. Ask Windows Shell handlers matching this target (handles MSIX/Win32 ProgId mismatches).
            (string? liveProgId, string? liveIconLocation, int liveIconIndex) =
                StaThreadRunner.Run(() => ShellApplicationDiscoveryService.TryFindHandlerIconInfo(normalizedExtension, target));

            string? fromLiveProgId = TryReadProgIdDefaultIcon(liveProgId);
            if (IsValidIconLocation(fromLiveProgId))
            {
                return fromLiveProgId!;
            }

            if (!string.IsNullOrWhiteSpace(liveIconLocation))
            {
                string formatted = FormatIconLocation(liveIconLocation, liveIconIndex);
                if (IsValidIconLocation(formatted))
                {
                    return formatted;
                }
            }

            // 3. Fall back to the target's executable icon if available.
            if (!string.IsNullOrWhiteSpace(target.ExecutablePath) &&
                File.Exists(target.ExecutablePath) &&
                !IsRuleOpenIconPath(target.ExecutablePath))
            {
                return $"\"{target.ExecutablePath}\",0";
            }
        }

        // 4. Check current non-RuleOpen system default ProgID/assoc icon.
        string? fromSystemDefault = TryReadNonRuleOpenSystemDefaultIcon(normalizedExtension);
        if (IsValidIconLocation(fromSystemDefault))
        {
            return fromSystemDefault!;
        }

        return null;
    }

    internal static bool IsValidIconLocation(string? rawLocation)
    {
        if (string.IsNullOrWhiteSpace(rawLocation))
        {
            return false;
        }

        string trimmed = rawLocation.Trim();
        if (string.Equals(trimmed, "%1", StringComparison.Ordinal) ||
            string.Equals(trimmed, "\"%1\"", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.StartsWith("@{", StringComparison.Ordinal))
        {
            string? resolved = ShellIconExtractor.TryResolveIndirectString(trimmed);
            return !string.IsNullOrWhiteSpace(resolved) &&
                   File.Exists(resolved) &&
                   !IsRuleOpenIconPath(resolved);
        }

        (string parsedPath, _) = ShellIconExtractor.ParseIconLocation(trimmed);
        if (string.IsNullOrWhiteSpace(parsedPath) ||
            IsRuleOpenIconPath(parsedPath) ||
            IsGenericUnknownShellIcon(trimmed))
        {
            return false;
        }

        return ResolveExistingIconFile(parsedPath) is not null;
    }

    private static string? ResolveExistingIconFile(string parsedPath)
    {
        if (string.IsNullOrWhiteSpace(parsedPath))
        {
            return null;
        }

        if (File.Exists(parsedPath))
        {
            return parsedPath;
        }

        if (!Path.IsPathRooted(parsedPath))
        {
            string sysCandidate = Path.Combine(Environment.SystemDirectory, parsedPath);
            if (File.Exists(sysCandidate))
            {
                return sysCandidate;
            }
        }

        return null;
    }

    private static string? TryReadProgIdDefaultIcon(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId) ||
            string.Equals(progId, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            PerUserFileAssociationService.IsRuleOpenProgId(progId))
        {
            return null;
        }

        try
        {
            using RegistryKey? iconKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\DefaultIcon", writable: false);
            return iconKey?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadNonRuleOpenSystemDefaultIcon(string normalizedExtension)
    {
        try
        {
            (_, _, bool isRuleOpenDefault) = PerUserFileAssociationService.QueryDefaultState(normalizedExtension);
            if (!isRuleOpenDefault)
            {
                string? assocIcon = PerUserFileAssociationService.QueryAssocString(normalizedExtension, ASSOCSTR.ASSOCSTR_DEFAULTICON);
                if (!string.IsNullOrWhiteSpace(assocIcon) && !IsGenericUnknownShellIcon(assocIcon))
                {
                    return assocIcon;
                }
            }

            using RegistryKey? extKey = Registry.ClassesRoot.OpenSubKey(normalizedExtension, writable: false);
            string? defaultProgId = extKey?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(defaultProgId) && !PerUserFileAssociationService.IsRuleOpenProgId(defaultProgId))
            {
                string? progIcon = TryReadProgIdDefaultIcon(defaultProgId);
                if (!string.IsNullOrWhiteSpace(progIcon) && !IsGenericUnknownShellIcon(progIcon))
                {
                    return progIcon;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsGenericUnknownShellIcon(string iconLocation)
    {
        (string parsedPath, int index) = ShellIconExtractor.ParseIconLocation(iconLocation);
        string fileName = Path.GetFileName(parsedPath);
        return (string.Equals(fileName, "shell32.dll", StringComparison.OrdinalIgnoreCase) && index == 0) ||
               (string.Equals(fileName, "imageres.dll", StringComparison.OrdinalIgnoreCase) && index is 0 or -2);
    }

    private static string FormatIconLocation(string location, int index)
    {
        if (location.StartsWith("@{", StringComparison.Ordinal) ||
            location.Contains(',', StringComparison.Ordinal))
        {
            return location;
        }

        return index != 0 ? $"\"{location}\",{index}" : location;
    }

    private static bool IsRuleOpenIconPath(string path) =>
        path.Contains("RuleOpen.Desktop.exe", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("RuleOpen.Dispatcher.exe", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("RuleOpen.ico", StringComparison.OrdinalIgnoreCase);
}
