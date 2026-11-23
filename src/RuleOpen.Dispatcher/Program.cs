using System.Diagnostics;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using RuleOpen.Core.Services;
using RuleOpen.Windows;
using RuleOpen.Windows.Applications;
using RuleOpen.Windows.Associations;
using RuleOpen.Windows.Shell;

namespace RuleOpen.Dispatcher;

internal static class Program
{
    public const string DisableRecoveryUiEnvVar = "RULEOPEN_DISPATCHER_NO_UI";

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return 0;
        }

        if (args.Any(a => string.Equals(a, "--install-register", StringComparison.OrdinalIgnoreCase)))
        {
            RunInstallRegistration();
            return 0;
        }

        if (args.Any(a => string.Equals(a, "--uninstall-cleanup", StringComparison.OrdinalIgnoreCase)))
        {
            await RunUninstallCleanupAsync(args).ConfigureAwait(false);
            return 0;
        }

        bool suppressUi = string.Equals(
            Environment.GetEnvironmentVariable(DisableRecoveryUiEnvVar),
            "1",
            StringComparison.Ordinal);

        List<string> filePaths = [];
        bool afterDoubleDash = false;
        foreach (string arg in args)
        {
            if (!afterDoubleDash && string.Equals(arg, "--", StringComparison.Ordinal))
            {
                afterDoubleDash = true;
                continue;
            }

            if (!afterDoubleDash && string.Equals(arg, "--no-ui", StringComparison.OrdinalIgnoreCase))
            {
                suppressUi = true;
                continue;
            }

            if (!afterDoubleDash && arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(arg))
            {
                filePaths.Add(arg);
            }
        }

        if (filePaths.Count == 0)
        {
            return 0;
        }

        RollingFileLogger logger = new();
        JsonConfigurationStore store = new(logger: logger);
        RuleOpenConfig config = store.Load();
        logger.IsEnabled = config.Settings.EnableDiagnosticLogging;

        RuleEngine ruleEngine = new(new WindowsFileSystem());
        ShellApplicationLauncher launcher = new();
        DispatchCoordinator coordinator = new(store, ruleEngine, launcher, logger);

        int lastExitCode = 0;

        foreach (string filePath in filePaths)
        {
            DispatchResult result = await coordinator
                .DispatchWithConfigAsync(filePath, config)
                .ConfigureAwait(false);

            int code = MapOutcomeToExitCode(result.Outcome);
            if (code != 0)
            {
                lastExitCode = code;
                if (!suppressUi)
                {
                    LaunchDesktopRecoveryUi(result);
                }
            }
        }

        return lastExitCode;
    }

    private static int MapOutcomeToExitCode(DispatchOutcome outcome) => outcome switch
    {
        DispatchOutcome.LaunchedByRule => 0,
        DispatchOutcome.LaunchedByExtensionFallback => 0,
        DispatchOutcome.LaunchedByGlobalFallback => 0,
        DispatchOutcome.NoRuleAndNoFallback => 10,
        DispatchOutcome.ApplicationMissing => 11,
        DispatchOutcome.RecursiveDispatchBlocked => 12,
        DispatchOutcome.FileMissing => 13,
        DispatchOutcome.InvalidInput => 13,
        DispatchOutcome.LaunchFailed => 14,
        _ => 1
    };

    private static void LaunchDesktopRecoveryUi(DispatchResult result)
    {
        string desktopExe = PerUserFileAssociationService.ResolveExecutablePath("RuleOpen.Desktop.exe");
        if (!File.Exists(desktopExe))
        {
            return;
        }

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = desktopExe,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--fallback-picker");
            psi.ArgumentList.Add(result.FilePath);
            psi.ArgumentList.Add("--reason");
            psi.ArgumentList.Add(result.Outcome.ToString());

            if (result.Evaluation?.WinningRule is not null)
            {
                psi.ArgumentList.Add("--rule-id");
                psi.ArgumentList.Add(result.Evaluation.WinningRule.Id);
            }

            Process.Start(psi);
        }
        catch
        {
            // Never throw an unhandled exception out of the silent dispatcher process.
        }
    }

    private static void RunInstallRegistration()
    {
        try
        {
            RollingFileLogger logger = new();
            JsonConfigurationStore store = new(logger: logger);
            RuleOpenConfig config = store.Load();

            PerUserFileAssociationService assocService = new();
            assocService.EnsureRegistered(config, registerStarterExtensionsIfEmpty: true);

            if (config.Settings.ClassicContextMenuEnabled)
            {
                try
                {
                    new ContextMenuRegistrationService().SetClassicContextMenuEnabled(true);
                }
                catch
                {
                }
            }

            store.Save(config);
        }
        catch
        {
            // Never fail MSI installation if HKCU registration encounters an environmental issue.
        }
    }

    internal static (int UiLevel, bool? ExplicitDeleteUserData, bool SuppressUi) ParseUninstallArguments(string[] args)
    {
        int uiLevel = 2;
        bool? deleteUserData = null;
        bool suppressUi = string.Equals(
            Environment.GetEnvironmentVariable(DisableRecoveryUiEnvVar),
            "1",
            StringComparison.Ordinal);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--no-ui", StringComparison.OrdinalIgnoreCase))
            {
                suppressUi = true;
            }
            else if (string.Equals(arg, "--ui-level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int parsedLevel))
                {
                    uiLevel = parsedLevel;
                }
            }
            else if (string.Equals(arg, "--delete-user-data", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string raw = args[++i].Trim();
                if (raw is "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    deleteUserData = true;
                }
                else if (raw is "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    deleteUserData = false;
                }
            }
        }

        return (uiLevel, deleteUserData, suppressUi);
    }

    internal static bool ApplyUserDataAndCacheCleanup(
        string configDir,
        bool deleteAllUserData)
    {
        if (deleteAllUserData)
        {
            TryDeleteDirectoryRecursive(configDir);
            return true;
        }

        // Keep config.json & logs, but clean install-transient caches.
        TryDeleteDirectoryRecursive(Path.Combine(configDir, "Icons"));
        TryDeleteDirectoryRecursive(Path.Combine(configDir, "ShellPackage"));
        return false;
    }

    private static async Task RunUninstallCleanupAsync(string[] args)
    {
        (int uiLevel, bool? explicitDeleteUserData, bool suppressUi) = ParseUninstallArguments(args);
        bool isInteractive = uiLevel > 2 && !suppressUi;

        string configDir = ConfigPaths.GetConfigDirectory();
        RollingFileLogger logger = new();
        JsonConfigurationStore store = new(logger: logger);
        RuleOpenConfig config = store.Load();
        PerUserFileAssociationService assocService = new();

        IReadOnlyList<FileAssociationStatus> activeDefaults = [];
        try
        {
            activeDefaults = assocService.GetActiveRuleOpenDefaultExtensions(config);
        }
        catch
        {
        }

        bool hasUserData = File.Exists(store.ConfigFilePath) || Directory.Exists(logger.LogDirectory);

        try
        {
            assocService.RemoveAllRegistrations();
        }
        catch
        {
        }

        try
        {
            ContextMenuRegistrationService contextMenu = new();
            contextMenu.SetClassicContextMenuEnabled(false);
            await contextMenu.DisableModernContextMenuAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        bool deleteUserData = explicitDeleteUserData
            ?? (isInteractive && hasUserData && UninstallDialogHelper.PromptDeleteUserData(configDir, config.Rules.Count));

        ApplyUserDataAndCacheCleanup(configDir, deleteUserData);

        if (isInteractive && activeDefaults.Count > 0)
        {
            try
            {
                if (UninstallDialogHelper.PromptRestoreDefaultApps(activeDefaults))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "ms-settings:defaultapps",
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
            }
        }
    }

    private static void TryDeleteDirectoryRecursive(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
