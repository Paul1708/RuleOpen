using Microsoft.Win32;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Windows.Associations;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage;

namespace RuleOpen.Windows.Shell;

/// <summary>
/// Manages both the Windows 11 modern context menu (Sparse MSIX + <c>IExplorerCommand</c>)
/// and an optional per-user classic Explorer context menu fallback.
/// </summary>
public sealed class ContextMenuRegistrationService : IContextMenuService
{
    public const string ModernPackageName = "RuleOpen.ShellExtension";
    public const string StorePackageName = "PaulBrinkmann.RuleOpen";
    private const string FileVerbKey = @"Software\Classes\*\shell\RuleOpen";
    private const string DirectoryVerbKey = @"Software\Classes\Directory\shell\RuleOpen";
    private const string DirectoryBgVerbKey = @"Software\Classes\Directory\Background\shell\RuleOpen";

    public static bool IsRunningAsStorePackage()
    {
        try
        {
            return string.Equals(Package.Current.Id.Name, StorePackageName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string? TryGetStorePackageFamilyName()
    {
        if (!IsRunningAsStorePackage())
        {
            return null;
        }

        try
        {
            return Package.Current.Id.FamilyName;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryGetStoreApplicationUserModelId()
    {
        string? familyName = TryGetStorePackageFamilyName();
        return string.IsNullOrWhiteSpace(familyName) ? null : $"{familyName}!RuleOpen.Desktop";
    }

    public static string? TryGetStoreLocalFolderPath()
    {
        if (!IsRunningAsStorePackage())
        {
            return null;
        }

        try
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            return null;
        }
    }

    public ContextMenuStatus GetStatus()
    {
        bool classicEnabled = IsClassicContextMenuInstalled();
        string? msixPath = FindSparsePackagePath();

        if (IsRunningAsStorePackage())
        {
            PackageVersion ver = Package.Current.Id.Version;
            return new ContextMenuStatus
            {
                ModernState = ModernContextMenuState.Registered,
                ModernStatusDetail = $"Active via Microsoft Store package (v{ver.Major}.{ver.Minor}.{ver.Build}). Appears directly on the Windows 11 right-click menu.",
                IsClassicEnabled = classicEnabled,
                CanRegisterModern = false,
                IsStoreManaged = true
            };
        }

        try
        {
            PackageManager pm = new();
            IReadOnlyList<Package> userPackages = pm.FindPackagesForUser(string.Empty).ToList();

            Package? storeInstalled = userPackages
                .FirstOrDefault(p => string.Equals(p.Id.Name, StorePackageName, StringComparison.OrdinalIgnoreCase));
            if (storeInstalled is not null)
            {
                PackageVersion ver = storeInstalled.Id.Version;
                return new ContextMenuStatus
                {
                    ModernState = ModernContextMenuState.Registered,
                    ModernStatusDetail = $"Active via Microsoft Store package (v{ver.Major}.{ver.Minor}.{ver.Build}). Appears directly on the Windows 11 right-click menu.",
                    IsClassicEnabled = classicEnabled,
                    CanRegisterModern = false,
                    IsStoreManaged = true
                };
            }

            Package? installed = userPackages
                .FirstOrDefault(p => string.Equals(p.Id.Name, ModernPackageName, StringComparison.OrdinalIgnoreCase));

            if (installed is not null)
            {
                return new ContextMenuStatus
                {
                    ModernState = ModernContextMenuState.Registered,
                    ModernStatusDetail = $"Registered (v{installed.Id.Version.Major}.{installed.Id.Version.Minor}.{installed.Id.Version.Build}). Appears directly on the Windows 11 right-click menu.",
                    IsClassicEnabled = classicEnabled,
                    CanRegisterModern = true
                };
            }
        }
        catch (Exception ex)
        {
            return new ContextMenuStatus
            {
                ModernState = ModernContextMenuState.RegistrationBlocked,
                ModernStatusDetail = $"Unable to query Sparse MSIX status: {ex.Message}",
                IsClassicEnabled = classicEnabled,
                CanRegisterModern = msixPath is not null
            };
        }

        if (msixPath is null)
        {
            return new ContextMenuStatus
            {
                ModernState = ModernContextMenuState.PackageNotFound,
                ModernStatusDetail = "RuleOpen.ShellPackage.msix was not found in the application directory. Build the packaging project or use the classic per-user menu below.",
                IsClassicEnabled = classicEnabled,
                CanRegisterModern = false
            };
        }

        return new ContextMenuStatus
        {
            ModernState = ModernContextMenuState.PackageAvailableNotRegistered,
            ModernStatusDetail = "Modern Windows 11 context menu package is ready to register for the current user.",
            IsClassicEnabled = classicEnabled,
            CanRegisterModern = true
        };
    }

    public async Task<(bool Success, string Message)> EnableModernContextMenuAsync(CancellationToken cancellationToken = default)
    {
        string? msixPath = FindSparsePackagePath();
        if (msixPath is null)
        {
            return (false, "RuleOpen.ShellPackage.msix could not be found in the installation folder.");
        }

        string externalLocation = Path.GetDirectoryName(PerUserFileAssociationService.ResolveExecutablePath("RuleOpen.Desktop.exe"))
            ?? AppContext.BaseDirectory;

        try
        {
            PackageManager pm = new();
            Uri msixUri = new(msixPath);
            Uri externalUri = new(externalLocation);

            // Signed Sparse MSIX registration (works on any Windows 11 PC when signed with a trusted cert).
            AddPackageOptions signedOptions = new()
            {
                ExternalLocationUri = externalUri,
                AllowUnsigned = false
            };

            DeploymentResult result = await pm.AddPackageByUriAsync(msixUri, signedOptions)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.ErrorText))
            {
                return await TryDeveloperModeFallbackOrExplainAsync(
                    pm,
                    msixPath,
                    externalUri,
                    result.ErrorText,
                    result.ExtendedErrorCode,
                    cancellationToken).ConfigureAwait(false);
            }

            return (true, "Windows 11 Explorer context menu registered.");
        }
        catch (Exception ex)
        {
            return await TryDeveloperModeFallbackOrExplainAsync(
                new PackageManager(),
                msixPath,
                new Uri(externalLocation),
                ex.Message,
                ex,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<(bool Success, string Message)> DisableModernContextMenuAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunningAsStorePackage())
        {
            return (false, "The modern context menu is built into the Microsoft Store package and cannot be unregistered separately.");
        }

        try
        {
            PackageManager pm = new();
            Package? installed = pm.FindPackagesForUser(string.Empty)
                .FirstOrDefault(p => string.Equals(p.Id.Name, ModernPackageName, StringComparison.OrdinalIgnoreCase));

            if (installed is null)
            {
                return (true, "Modern context menu package is not currently registered.");
            }

            DeploymentResult result = await pm.RemovePackageAsync(installed.Id.FullName)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.ErrorText))
            {
                return (false, result.ErrorText);
            }

            return (true, "Windows 11 Explorer context menu unregistered.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to remove Sparse MSIX package: {ex.Message}");
        }
    }

    public (bool Success, string Message) SetClassicContextMenuEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(FileVerbKey, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(DirectoryVerbKey, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(DirectoryBgVerbKey, throwOnMissingSubKey: false);
                return (true, "Classic per-user Explorer context menu entries removed.");
            }

            string desktopExe = PerUserFileAssociationService.ResolveExecutablePath("RuleOpen.Desktop.exe");
            string dispatcherExe = PerUserFileAssociationService.ResolveExecutablePath("RuleOpen.Dispatcher.exe");
            string iconPath = ResolveIconPath(desktopExe);

            // Files (*) cascading menu: Create opening rule + Open using RuleOpen
            using (RegistryKey fileRoot = Registry.CurrentUser.CreateSubKey(FileVerbKey))
            {
                fileRoot.SetValue("MUIVerb", "RuleOpen", RegistryValueKind.String);
                fileRoot.SetValue("Icon", iconPath, RegistryValueKind.String);
                fileRoot.SetValue("SubCommands", string.Empty, RegistryValueKind.String);

                using RegistryKey createSub = fileRoot.CreateSubKey(@"shell\01_CreateRule");
                createSub.SetValue("MUIVerb", "Create opening rule", RegistryValueKind.String);
                createSub.SetValue("Icon", iconPath, RegistryValueKind.String);
                using (RegistryKey cmd = createSub.CreateSubKey("command"))
                {
                    cmd.SetValue(null, $"\"{desktopExe}\" --create-rule \"%1\"", RegistryValueKind.String);
                }

                using RegistryKey openSub = fileRoot.CreateSubKey(@"shell\02_OpenWithRuleOpen");
                openSub.SetValue("MUIVerb", "Open using RuleOpen", RegistryValueKind.String);
                openSub.SetValue("Icon", iconPath, RegistryValueKind.String);
                using (RegistryKey cmd = openSub.CreateSubKey("command"))
                {
                    cmd.SetValue(null, $"\"{dispatcherExe}\" -- \"%1\"", RegistryValueKind.String);
                }
            }

            // Directory cascading menu: Create opening rule
            WriteFolderMenuKey(DirectoryVerbKey, desktopExe, iconPath, "%1");
            // Directory background cascading menu: Create opening rule
            WriteFolderMenuKey(DirectoryBgVerbKey, desktopExe, iconPath, "%V");

            return (true, "Classic Explorer context menu enabled (visible under \"Show more options\" on Windows 11).");
        }
        catch (Exception ex)
        {
            return (false, $"Could not update classic context menu entries: {ex.Message}");
        }
    }

    private static void WriteFolderMenuKey(string subKey, string desktopExe, string iconPath, string pathToken)
    {
        using RegistryKey root = Registry.CurrentUser.CreateSubKey(subKey);
        root.SetValue("MUIVerb", "RuleOpen", RegistryValueKind.String);
        root.SetValue("Icon", iconPath, RegistryValueKind.String);
        root.SetValue("SubCommands", string.Empty, RegistryValueKind.String);

        using RegistryKey createSub = root.CreateSubKey(@"shell\01_CreateRule");
        createSub.SetValue("MUIVerb", "Create opening rule", RegistryValueKind.String);
        createSub.SetValue("Icon", iconPath, RegistryValueKind.String);
        using RegistryKey cmd = createSub.CreateSubKey("command");
        cmd.SetValue(null, $"\"{desktopExe}\" --create-rule-folder \"{pathToken}\"", RegistryValueKind.String);
    }

    private static bool IsClassicContextMenuInstalled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(FileVerbKey, writable: false);
        return key is not null;
    }

    private static string? FindSparsePackagePath()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "RuleOpen.ShellPackage.msix");
        if (File.Exists(direct))
        {
            return direct;
        }

        string sibling = Path.Combine(
            Path.GetDirectoryName(PerUserFileAssociationService.ResolveExecutablePath("RuleOpen.Desktop.exe")) ?? AppContext.BaseDirectory,
            "RuleOpen.ShellPackage.msix");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string devPkg = Path.Combine(dir.FullName, "RuleOpen.ShellExtension", "bin", "x64", "Release", "RuleOpen.ShellPackage.msix");
            if (File.Exists(devPkg))
            {
                return devPkg;
            }
        }

        return null;
    }

    private static string ResolveIconPath(string desktopExe)
    {
        string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "RuleOpen.ico");
        if (File.Exists(ico))
        {
            return ico;
        }

        return $"\"{desktopExe}\",0";
    }

    private static async Task<(bool Success, string Message)> TryDeveloperModeFallbackOrExplainAsync(
        PackageManager pm,
        string msixPath,
        Uri externalUri,
        string originalError,
        Exception? originalException,
        CancellationToken cancellationToken)
    {
        if (IsUnsignedOrUntrustedError(originalError, originalException))
        {
            // When Windows Developer Mode is enabled, Windows allows registering an unpacked AppxManifest.xml
            // with ExternalLocationUri without requiring a code-signing certificate.
            try
            {
                string unpackedDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RuleOpen",
                    "ShellPackage");
                if (Directory.Exists(unpackedDir))
                {
                    Directory.Delete(unpackedDir, recursive: true);
                }
                Directory.CreateDirectory(unpackedDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(msixPath, unpackedDir, overwriteFiles: true);

                string manifestPath = Path.Combine(unpackedDir, "AppxManifest.xml");
                if (File.Exists(manifestPath))
                {
                    RegisterPackageOptions regOptions = new()
                    {
                        ExternalLocationUri = externalUri,
                        DeveloperMode = true
                    };

                    DeploymentResult devResult = await pm
                        .RegisterPackageByUriAsync(new Uri(manifestPath), regOptions)
                        .AsTask(cancellationToken)
                        .ConfigureAwait(false);

                    if (string.IsNullOrEmpty(devResult.ErrorText))
                    {
                        return (true, "Windows 11 Explorer context menu registered (Developer Mode).");
                    }
                }
            }
            catch
            {
                // Developer Mode is likely off; fall through to clear user guidance below.
            }

            return (false,
                "This build of RuleOpen.ShellPackage.msix is unsigned. Windows 11 requires either a trusted code-signing certificate or Windows Developer Mode (Settings -> System -> For developers) to register the modern Windows 11 context menu. Use the Classic per-user Explorer menu below for unsigned builds without Developer Mode.");
        }

        return (false, FormatDeploymentError(originalError, originalException));
    }

    private static bool IsUnsignedOrUntrustedError(string message, Exception? ex)
    {
        int hr = ex?.HResult ?? 0;
        return hr == unchecked((int)0x800B0100) ||
               hr == unchecked((int)0x800B0109) ||
               hr == unchecked((int)0x80073CFF) ||
               hr == unchecked((int)0x80073D2B) ||
               hr == unchecked((int)0x80073D2C) ||
               message.Contains("0x800B0100", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("0x800B0109", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("0x80073D2B", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("0x80073D2C", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("0x800B010A", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Signatur", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Zertifikat", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Unsigned", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDeploymentError(string message, Exception? ex)
    {
        if (IsUnsignedOrUntrustedError(message, ex))
        {
            return "This build of RuleOpen.ShellPackage.msix is unsigned. Windows 11 requires either a trusted code-signing certificate or Windows Developer Mode (Settings -> System -> For developers) to register the modern Windows 11 context menu. Use the Classic per-user Explorer menu below for unsigned builds without Developer Mode.";
        }

        return message;
    }
}
