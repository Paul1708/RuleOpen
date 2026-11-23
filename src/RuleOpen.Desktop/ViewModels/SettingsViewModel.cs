using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using RuleOpen.Desktop.Services;
using RuleOpen.Windows.Associations;

namespace RuleOpen.Desktop.ViewModels;

public sealed class RegisteredExtensionItemViewModel
{
    public required FileAssociationStatus Status { get; init; }
    public string Extension => Status.Extension;
    public string FriendlyName => Status.FriendlyTypeName;
    public string DefaultStatusLabel => Status.IsRuleOpenDefault
        ? string.Equals(Status.CurrentDefaultProgId, PerUserFileAssociationService.LegacyRuleOpenProgId, StringComparison.OrdinalIgnoreCase)
            ? "RuleOpen is default (re-select in Default Apps for per-type icons)"
            : "RuleOpen is default"
        : !string.IsNullOrWhiteSpace(Status.CurrentDefaultDisplayName)
            ? $"Default: {Status.CurrentDefaultDisplayName}"
            : "No default set";
    public bool IsRuleOpenDefault => Status.IsRuleOpenDefault;
    public string? CapturedFallbackText => !string.IsNullOrWhiteSpace(Status.CapturedFallbackDisplayName)
        ? $"Fallback: {Status.CapturedFallbackDisplayName}"
        : null;
    public bool HasCapturedFallback => CapturedFallbackText is not null;
}

public sealed class ExtensionFallbackItemViewModel : ObservableObject
{
    private BitmapImage? _icon;

    public required string Extension { get; init; }
    public ApplicationTarget? Target { get; init; }
    public bool HasTarget => Target is not null;
    public string DisplayName => Target?.DisplayName ?? "Not set — uses global fallback";
    public string Detail => Target is null
        ? "No per-extension fallback configured"
        : Target.Kind == ApplicationTargetKind.CustomExecutable
            ? $"Custom executable • {Target.ExecutablePath}"
            : "Windows registered handler";

    public BitmapImage? Icon
    {
        get => _icon;
        private set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(MissingIcon));
            }
        }
    }

    public bool HasIcon => _icon is not null;
    public bool MissingIcon => _icon is null;

    public async Task LoadIconAsync(IconCacheService iconCache)
    {
        if (Target is null)
        {
            return;
        }

        Icon = await iconCache.GetIconBitmapAsync(Target, Extension);
    }
}

public sealed class SettingsViewModel(
    IConfigurationStore configurationStore,
    IFileAssociationService fileAssociationService,
    IContextMenuService contextMenuService,
    IDiagnosticLogger logger,
    ThemeService themeService,
    IconCacheService iconCache) : ObservableObject
{
    private int _selectedThemeIndex;
    private int _selectedConflictModeIndex;
    private int _selectedExplorerIconModeIndex;
    private bool _diagnosticLoggingEnabled;
    private bool _classicContextMenuEnabled;
    private bool _isBusyContextMenu;
    private string _modernContextMenuDetail = string.Empty;
    private bool _isModernContextMenuRegistered;
    private bool _canRegisterModernContextMenu;
    private string _contextMenuFeedback = string.Empty;
    private InfoBarSeverity _contextMenuFeedbackSeverity = InfoBarSeverity.Informational;
    private bool _showDeveloperSettingsButton;
    private string _newExtensionInput = string.Empty;
    private string _associationFeedback = string.Empty;
    private string _fallbackFeedback = string.Empty;
    private string _globalFallbackName = "None (ask me if no rule matches)";
    private bool _hasGlobalFallback;
    private BitmapImage? _globalFallbackIcon;
    private bool _suppressSave;

    public ObservableCollection<RegisteredExtensionItemViewModel> RegisteredExtensions { get; } = [];
    public ObservableCollection<ExtensionFallbackItemViewModel> ExtensionFallbacks { get; } = [];
    public IReadOnlyList<string> CuratedExtensions => fileAssociationService.GetCuratedExtensions();
    public bool HasExtensionFallbacks => ExtensionFallbacks.Count > 0;

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (SetProperty(ref _selectedThemeIndex, value) && !_suppressSave)
            {
                ThemePreference pref = value switch
                {
                    1 => ThemePreference.Light,
                    2 => ThemePreference.Dark,
                    _ => ThemePreference.System
                };
                themeService.ApplyTheme(pref);
                UpdateConfig(c => c.Settings.Theme = pref);
            }
        }
    }

    public int SelectedConflictModeIndex
    {
        get => _selectedConflictModeIndex;
        set
        {
            if (SetProperty(ref _selectedConflictModeIndex, value) && !_suppressSave)
            {
                ConflictResolutionMode mode = value == 1
                    ? ConflictResolutionMode.PriorityFirst
                    : ConflictResolutionMode.SpecificityFirst;
                RuleOpenConfig config = configurationStore.Load();
                config.Settings.ConflictResolutionMode = mode;
                if (config.Settings.ExplorerIconMode == ExplorerIconMode.MatchedRoutedAppIcon)
                {
                    fileAssociationService.RefreshExtensionIcons(CollectTrackedExtensions(config), config);
                }
                configurationStore.Save(config);
            }
        }
    }

    public int SelectedExplorerIconModeIndex
    {
        get => _selectedExplorerIconModeIndex;
        set
        {
            if (SetProperty(ref _selectedExplorerIconModeIndex, value) && !_suppressSave)
            {
                ExplorerIconMode mode = value switch
                {
                    1 => ExplorerIconMode.RuleOpenExtensionBadges,
                    2 => ExplorerIconMode.InheritFallbackIcon,
                    _ => ExplorerIconMode.MatchedRoutedAppIcon
                };

                RuleOpenConfig config = configurationStore.Load();
                config.Settings.ExplorerIconMode = mode;
                List<string> tracked = CollectTrackedExtensions(config);
                fileAssociationService.RefreshExtensionIcons(tracked, config);
                configurationStore.Save(config);

                AssociationFeedback = mode switch
                {
                    ExplorerIconMode.MatchedRoutedAppIcon =>
                        "Updated File Explorer icons to show each folder rule's routed application icon (or fallback outside ruled folders).",
                    ExplorerIconMode.InheritFallbackIcon =>
                        "Updated File Explorer icons to inherit each extension's fallback application icon.",
                    _ => "Updated File Explorer icons to RuleOpen extension-badged icons."
                };
            }
        }
    }

    public bool DiagnosticLoggingEnabled
    {
        get => _diagnosticLoggingEnabled;
        set
        {
            if (SetProperty(ref _diagnosticLoggingEnabled, value) && !_suppressSave)
            {
                logger.IsEnabled = value;
                UpdateConfig(c => c.Settings.EnableDiagnosticLogging = value);
            }
        }
    }

    public bool ClassicContextMenuEnabled
    {
        get => _classicContextMenuEnabled;
        set
        {
            if (SetProperty(ref _classicContextMenuEnabled, value) && !_suppressSave)
            {
                (bool ok, string msg) = contextMenuService.SetClassicContextMenuEnabled(value);
                ContextMenuFeedbackSeverity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                ShowDeveloperSettingsButton = false;
                ContextMenuFeedback = msg;
                if (ok)
                {
                    UpdateConfig(c => c.Settings.ClassicContextMenuEnabled = value);
                }
                Load();
            }
        }
    }

    public bool IsModernContextMenuRegistered
    {
        get => _isModernContextMenuRegistered;
        private set
        {
            if (SetProperty(ref _isModernContextMenuRegistered, value))
            {
                OnPropertyChanged(nameof(ModernActionLabel));
            }
        }
    }

    public bool CanRegisterModernContextMenu
    {
        get => _canRegisterModernContextMenu;
        private set
        {
            if (SetProperty(ref _canRegisterModernContextMenu, value))
            {
                OnPropertyChanged(nameof(ModernActionLabel));
            }
        }
    }

    public bool IsBusyContextMenu
    {
        get => _isBusyContextMenu;
        private set => SetProperty(ref _isBusyContextMenu, value);
    }

    public string ModernActionLabel =>
        IsModernContextMenuRegistered
            ? (CanRegisterModernContextMenu ? "Unregister" : "Active")
            : "Register";
    public string ModernContextMenuDetail { get => _modernContextMenuDetail; private set => SetProperty(ref _modernContextMenuDetail, value); }
    public string ContextMenuFeedback { get => _contextMenuFeedback; private set { if (SetProperty(ref _contextMenuFeedback, value)) OnPropertyChanged(nameof(HasContextMenuFeedback)); } }
    public bool HasContextMenuFeedback => !string.IsNullOrWhiteSpace(_contextMenuFeedback);
    public InfoBarSeverity ContextMenuFeedbackSeverity { get => _contextMenuFeedbackSeverity; private set => SetProperty(ref _contextMenuFeedbackSeverity, value); }
    public bool ShowDeveloperSettingsButton { get => _showDeveloperSettingsButton; private set => SetProperty(ref _showDeveloperSettingsButton, value); }

    public string NewExtensionInput
    {
        get => _newExtensionInput;
        set => SetProperty(ref _newExtensionInput, value);
    }

    public string AssociationFeedback
    {
        get => _associationFeedback;
        private set
        {
            if (SetProperty(ref _associationFeedback, value))
            {
                OnPropertyChanged(nameof(HasAssociationFeedback));
            }
        }
    }

    public bool HasAssociationFeedback => !string.IsNullOrWhiteSpace(_associationFeedback);

    public string FallbackFeedback
    {
        get => _fallbackFeedback;
        private set
        {
            if (SetProperty(ref _fallbackFeedback, value))
            {
                OnPropertyChanged(nameof(HasFallbackFeedback));
            }
        }
    }

    public bool HasFallbackFeedback => !string.IsNullOrWhiteSpace(_fallbackFeedback);

    public string GlobalFallbackName { get => _globalFallbackName; private set => SetProperty(ref _globalFallbackName, value); }
    public bool HasGlobalFallback { get => _hasGlobalFallback; private set => SetProperty(ref _hasGlobalFallback, value); }
    public BitmapImage? GlobalFallbackIcon { get => _globalFallbackIcon; private set { if (SetProperty(ref _globalFallbackIcon, value)) OnPropertyChanged(nameof(HasGlobalFallbackIcon)); } }
    public bool HasGlobalFallbackIcon => _globalFallbackIcon is not null;

    public void Load()
    {
        _suppressSave = true;
        try
        {
            RuleOpenConfig config = configurationStore.Load();
            SelectedThemeIndex = config.Settings.Theme switch
            {
                ThemePreference.Light => 1,
                ThemePreference.Dark => 2,
                _ => 0
            };

            SelectedConflictModeIndex = config.Settings.ConflictResolutionMode == ConflictResolutionMode.PriorityFirst ? 1 : 0;
            SelectedExplorerIconModeIndex = config.Settings.ExplorerIconMode switch
            {
                ExplorerIconMode.RuleOpenExtensionBadges => 1,
                ExplorerIconMode.InheritFallbackIcon => 2,
                _ => 0
            };
            DiagnosticLoggingEnabled = config.Settings.EnableDiagnosticLogging;

            ContextMenuStatus cmStatus = contextMenuService.GetStatus();
            IsModernContextMenuRegistered = cmStatus.ModernState == ModernContextMenuState.Registered;
            CanRegisterModernContextMenu = cmStatus.CanRegisterModern;
            ModernContextMenuDetail = cmStatus.ModernStatusDetail;
            ClassicContextMenuEnabled = cmStatus.IsClassicEnabled;

            RefreshExtensionsList(config);
            RefreshFallbackDisplay(config);
        }
        finally
        {
            _suppressSave = false;
        }
    }

    public async Task ToggleModernContextMenuAsync()
    {
        IsBusyContextMenu = true;
        try
        {
            (bool ok, string msg) = IsModernContextMenuRegistered
                ? await contextMenuService.DisableModernContextMenuAsync()
                : await contextMenuService.EnableModernContextMenuAsync();
            ContextMenuFeedbackSeverity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ShowDeveloperSettingsButton = !ok && msg.Contains("Developer Mode", StringComparison.OrdinalIgnoreCase);
            ContextMenuFeedback = msg;
            Load();
        }
        finally
        {
            IsBusyContextMenu = false;
        }
    }

    public static void OpenDeveloperSettings()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:developers",
            UseShellExecute = true
        });
    }

    public void RegisterExtensionInput()
    {
        (IReadOnlyList<string> parsed, IReadOnlyList<string> invalid) = ExtensionNormalizer.ParseList(NewExtensionInput);
        if (parsed.Count == 0 || invalid.Count > 0)
        {
            AssociationFeedback = "Enter a valid extension such as .pdf or .md.";
            return;
        }

        RuleOpenConfig config = configurationStore.Load();
        fileAssociationService.RegisterExtensions(parsed, config);
        configurationStore.Save(config);

        NewExtensionInput = string.Empty;
        AssociationFeedback = $"Registered {string.Join(", ", parsed)} with Windows. Select RuleOpen in Default Apps when ready.";
        RefreshExtensionsList(config);
        RefreshFallbackDisplay(config);
    }

    public void RegisterCommonExtensions()
    {
        RuleOpenConfig config = configurationStore.Load();
        string[] curated = [".pdf", ".md", ".markdown", ".txt", ".json", ".csv"];
        fileAssociationService.RegisterExtensions(curated, config);
        configurationStore.Save(config);
        AssociationFeedback = "Registered common document extensions (.pdf, .md, .markdown, .txt, .json, .csv).";
        RefreshExtensionsList(config);
        RefreshFallbackDisplay(config);
    }

    public void UnregisterExtension(string ext)
    {
        RuleOpenConfig config = configurationStore.Load();
        fileAssociationService.UnregisterExtension(ext, config);
        configurationStore.Save(config);
        AssociationFeedback = $"Removed {ext} from RuleOpen Windows registration.";
        RefreshExtensionsList(config);
        RefreshFallbackDisplay(config);
    }

    public void OpenDefaultAppsSettings() => fileAssociationService.OpenWindowsDefaultAppsSettings();

    public ApplicationTarget? GetGlobalFallbackTarget() => configurationStore.Load().GlobalFallback;

    public ApplicationTarget? GetExtensionFallbackTarget(string extension)
    {
        if (!ExtensionNormalizer.TryNormalize(extension, out string normExt))
        {
            return null;
        }

        RuleOpenConfig config = configurationStore.Load();
        return config.ExtensionFallbacks.TryGetValue(normExt, out ApplicationTarget? target) ? target : null;
    }

    public async Task SetGlobalFallbackTargetAsync(ApplicationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ApplicationTarget copy = target.Clone();
        RuleOpenConfig config = configurationStore.Load();
        config.GlobalFallback = copy;
        if (config.Settings.ExplorerIconMode == ExplorerIconMode.MatchedRoutedAppIcon)
        {
            fileAssociationService.RefreshExtensionIcons(CollectTrackedExtensions(config), config);
        }
        configurationStore.Save(config);

        GlobalFallbackName = copy.DisplayName;
        HasGlobalFallback = true;
        GlobalFallbackIcon = await iconCache.GetIconBitmapAsync(copy);
        FallbackFeedback = $"Global fallback set to {copy.DisplayName}.";
    }

    public void ClearGlobalFallback()
    {
        RuleOpenConfig config = configurationStore.Load();
        config.GlobalFallback = null;
        if (config.Settings.ExplorerIconMode == ExplorerIconMode.MatchedRoutedAppIcon)
        {
            fileAssociationService.RefreshExtensionIcons(CollectTrackedExtensions(config), config);
        }
        configurationStore.Save(config);
        GlobalFallbackName = "None (ask me if no rule matches)";
        HasGlobalFallback = false;
        GlobalFallbackIcon = null;
        FallbackFeedback = "Cleared global fallback application.";
    }

    public async Task SetExtensionFallbackAsync(string extension, ApplicationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ExtensionNormalizer.TryNormalize(extension, out string normExt))
        {
            return;
        }

        RuleOpenConfig config = configurationStore.Load();
        config.ExtensionFallbacks[normExt] = target.Clone();
        fileAssociationService.RefreshExtensionIcons(new[] { normExt }, config);
        configurationStore.Save(config);

        RefreshExtensionsList(config);
        RefreshFallbackDisplay(config);
        FallbackFeedback = $"Fallback for {normExt} set to {target.DisplayName}.";
        await Task.CompletedTask;
    }

    public void AutoDetectExtensionFallback(string extension)
    {
        if (!ExtensionNormalizer.TryNormalize(extension, out string normExt))
        {
            return;
        }

        RuleOpenConfig config = configurationStore.Load();
        ApplicationTarget? detected = fileAssociationService.CaptureCurrentDefaultHandler(normExt);
        if (detected is null)
        {
            FallbackFeedback =
                $"Could not auto-detect another default app for {normExt} (RuleOpen may already be the Windows default). Use 'Change...' to choose one manually.";
            return;
        }

        config.ExtensionFallbacks[normExt] = detected;
        fileAssociationService.RefreshExtensionIcons(new[] { normExt }, config);
        configurationStore.Save(config);

        RefreshExtensionsList(config);
        RefreshFallbackDisplay(config);
        FallbackFeedback = $"Detected {detected.DisplayName} from Windows for {normExt}.";
    }

    public void ClearExtensionFallback(string extension)
    {
        if (!ExtensionNormalizer.TryNormalize(extension, out string normExt))
        {
            return;
        }

        RuleOpenConfig config = configurationStore.Load();
        if (!config.ExtensionFallbacks.Remove(normExt))
        {
            return;
        }

        fileAssociationService.RefreshExtensionIcons(new[] { normExt }, config);
        configurationStore.Save(config);

        RefreshExtensionsList(config);
        RefreshFallbackDisplay(config);
        FallbackFeedback = $"Cleared per-extension fallback for {normExt}.";
    }

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(logger.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = logger.LogDirectory,
            UseShellExecute = true
        });
    }

    private static List<string> CollectTrackedExtensions(RuleOpenConfig config) =>
        config.Settings.RegisteredExtensions
            .Concat(config.Rules.SelectMany(r => r.Extensions))
            .Concat(config.ExtensionFallbacks.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void RefreshExtensionsList(RuleOpenConfig config)
    {
        RegisteredExtensions.Clear();
        List<string> tracked = config.Settings.RegisteredExtensions
            .Concat(config.Rules.SelectMany(r => r.Extensions))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tracked.Count == 0)
        {
            return;
        }

        foreach (FileAssociationStatus status in fileAssociationService.GetStatusForExtensions(tracked, config))
        {
            if (status.IsRegisteredWithWindows || status.IsRuleOpenDefault)
            {
                RegisteredExtensions.Add(new RegisteredExtensionItemViewModel { Status = status });
            }
        }
    }

    private void RefreshFallbackDisplay(RuleOpenConfig config)
    {
        ExtensionFallbacks.Clear();

        List<string> allExtensions = config.ExtensionFallbacks.Keys
            .Concat(config.Settings.RegisteredExtensions)
            .Concat(config.Rules.SelectMany(r => r.Extensions))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string ext in allExtensions)
        {
            config.ExtensionFallbacks.TryGetValue(ext, out ApplicationTarget? target);
            ExtensionFallbackItemViewModel item = new()
            {
                Extension = ext,
                Target = target
            };
            ExtensionFallbacks.Add(item);
            _ = item.LoadIconAsync(iconCache);
        }

        OnPropertyChanged(nameof(HasExtensionFallbacks));

        if (config.GlobalFallback is not null)
        {
            GlobalFallbackName = config.GlobalFallback.DisplayName;
            HasGlobalFallback = true;
            _ = LoadGlobalFallbackIconAsync(config.GlobalFallback);
        }
        else
        {
            GlobalFallbackName = "None (ask me if no rule matches)";
            HasGlobalFallback = false;
            GlobalFallbackIcon = null;
        }
    }

    private async Task LoadGlobalFallbackIconAsync(ApplicationTarget target)
    {
        GlobalFallbackIcon = await iconCache.GetIconBitmapAsync(target);
    }

    private void UpdateConfig(Action<RuleOpenConfig> mutate)
    {
        RuleOpenConfig config = configurationStore.Load();
        mutate(config);
        configurationStore.Save(config);
    }
}
