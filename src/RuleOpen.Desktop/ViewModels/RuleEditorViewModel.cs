using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using RuleOpen.Desktop.Services;

namespace RuleOpen.Desktop.ViewModels;

public sealed class ApplicationChoiceViewModel : ObservableObject
{
    public required DiscoveredApplication App { get; init; }
    public string DisplayName => App.DisplayName;
    public string Subtitle => App.IsRecommended
        ? "Recommended app"
        : !string.IsNullOrWhiteSpace(App.ExecutablePath) ? App.ExecutablePath : "Windows registered app";
    public bool IsRecommended => App.IsRecommended;
    public BitmapImage? Icon { get; set; }
    public bool HasIcon => Icon is not null;
    public bool MissingIcon => Icon is null;
}

public sealed class RuleEditorViewModel(
    IConfigurationStore configurationStore,
    IRuleEngine ruleEngine,
    IApplicationDiscoveryService discoveryService,
    IFileSystem fileSystem,
    IconCacheService iconCache) : ObservableObject
{
    private readonly List<ApplicationChoiceViewModel> _allChoices = [];
    private string _ruleId = Guid.NewGuid().ToString("N");
    private DateTimeOffset _createdUtc = DateTimeOffset.UtcNow;
    private bool _isEdit;
    private bool _nameManuallyEdited;
    private string _name = string.Empty;
    private string _extensionsText = ".pdf";
    private string _folderPath = string.Empty;
    private bool _includeSubfolders = true;
    private double _priority = 100;
    private ApplicationTarget? _selectedTarget;
    private BitmapImage? _selectedAppIcon;
    private string _appSearchQuery = string.Empty;
    private bool _isPickerExpanded;
    private bool _isDiscoveringApps;
    private string _validationError = string.Empty;
    private string _warningSummary = string.Empty;
    private string _overlapSummary = string.Empty;
    private bool _canSave;

    public ObservableCollection<ApplicationChoiceViewModel> RecommendedApps { get; } = [];
    public ObservableCollection<ApplicationChoiceViewModel> OtherApps { get; } = [];

    public string DialogTitle => _isEdit ? "Edit opening rule" : "Create opening rule";
    public string PrimaryButtonText => _isEdit ? "Save changes" : "Create rule";
    public bool IsEdit => _isEdit;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Validate();
            }
        }
    }

    public void MarkNameManuallyEdited() => _nameManuallyEdited = true;

    public string ExtensionsText
    {
        get => _extensionsText;
        set
        {
            if (SetProperty(ref _extensionsText, value))
            {
                SuggestRuleNameIfUntouched();
                Validate();
                _ = RefreshAppsAsync();
            }
        }
    }

    public string FolderPath
    {
        get => _folderPath;
        set
        {
            if (SetProperty(ref _folderPath, value))
            {
                SuggestRuleNameIfUntouched();
                Validate();
            }
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (SetProperty(ref _includeSubfolders, value))
            {
                Validate();
            }
        }
    }

    public double Priority
    {
        get => _priority;
        set
        {
            if (SetProperty(ref _priority, value))
            {
                Validate();
            }
        }
    }

    public ApplicationTarget? SelectedTarget
    {
        get => _selectedTarget;
        private set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                OnPropertyChanged(nameof(HasSelectedApp));
                OnPropertyChanged(nameof(SelectedAppName));
                OnPropertyChanged(nameof(SelectedAppDetail));
                Validate();
            }
        }
    }

    public bool HasSelectedApp => _selectedTarget is not null;
    public string SelectedAppName => _selectedTarget?.DisplayName ?? "Choose an application...";
    public string SelectedAppDetail => _selectedTarget is null
        ? "Select a Windows app or browse for an executable"
        : _selectedTarget.Kind == ApplicationTargetKind.CustomExecutable
            ? $"Custom executable • {_selectedTarget.ExecutablePath}"
            : "Windows registered handler";

    public BitmapImage? SelectedAppIcon
    {
        get => _selectedAppIcon;
        private set
        {
            if (SetProperty(ref _selectedAppIcon, value))
            {
                OnPropertyChanged(nameof(HasSelectedAppIcon));
                OnPropertyChanged(nameof(MissingSelectedAppIcon));
            }
        }
    }

    public bool HasSelectedAppIcon => _selectedAppIcon is not null;
    public bool MissingSelectedAppIcon => _selectedAppIcon is null;

    public bool IsPickerExpanded
    {
        get => _isPickerExpanded;
        set => SetProperty(ref _isPickerExpanded, value);
    }

    public string AppSearchQuery
    {
        get => _appSearchQuery;
        set
        {
            if (SetProperty(ref _appSearchQuery, value))
            {
                FilterDiscoveredApps();
            }
        }
    }

    public bool IsDiscoveringApps
    {
        get => _isDiscoveringApps;
        private set => SetProperty(ref _isDiscoveringApps, value);
    }

    public bool HasRecommendedApps => RecommendedApps.Count > 0;
    public bool HasOtherApps => OtherApps.Count > 0;
    public bool HasNoDiscoveredApps => !IsDiscoveringApps && RecommendedApps.Count == 0 && OtherApps.Count == 0;

    public string ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(_validationError);

    public string WarningSummary
    {
        get => _warningSummary;
        private set
        {
            if (SetProperty(ref _warningSummary, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(_warningSummary);

    public string OverlapSummary
    {
        get => _overlapSummary;
        private set
        {
            if (SetProperty(ref _overlapSummary, value))
            {
                OnPropertyChanged(nameof(HasOverlap));
            }
        }
    }

    public bool HasOverlap => !string.IsNullOrWhiteSpace(_overlapSummary);

    public bool CanSave
    {
        get => _canSave;
        private set => SetProperty(ref _canSave, value);
    }

    public void InitializeNew(string? prefillExtension = null, string? prefillFolder = null)
    {
        _isEdit = false;
        _ruleId = Guid.NewGuid().ToString("N");
        _createdUtc = DateTimeOffset.UtcNow;
        _nameManuallyEdited = false;

        _extensionsText = string.IsNullOrWhiteSpace(prefillExtension) ? ".pdf" : prefillExtension;
        _folderPath = prefillFolder ?? string.Empty;
        _includeSubfolders = true;
        _priority = 100;
        _selectedTarget = null;
        _selectedAppIcon = null;
        _isPickerExpanded = true;
        _name = string.Empty;

        SuggestRuleNameIfUntouched();
        Validate();
        _ = RefreshAppsAsync();
    }

    public void InitializeForFile(string filePath)
    {
        string? folder = Path.GetDirectoryName(filePath);
        string ext = Path.GetExtension(filePath);
        InitializeNew(
            string.IsNullOrWhiteSpace(ext) ? ".pdf" : ext.ToLowerInvariant(),
            folder ?? string.Empty);
    }

    public void InitializeForFolder(string folderPath)
    {
        InitializeNew(".pdf", folderPath);
    }

    public void InitializeEdit(OpeningRule existing)
    {
        _isEdit = true;
        _ruleId = existing.Id;
        _createdUtc = existing.CreatedUtc;
        _nameManuallyEdited = true;

        _name = existing.Name;
        _extensionsText = string.Join(", ", existing.Extensions);
        _folderPath = existing.FolderPath;
        _includeSubfolders = existing.IncludeSubfolders;
        _priority = existing.Priority;
        _selectedTarget = existing.TargetApplication.Clone();
        _isPickerExpanded = false;

        Validate();
        _ = LoadSelectedIconAsync();
        _ = RefreshAppsAsync();
    }

    public void AppendExtensionSuggestion(string ext)
    {
        (IReadOnlyList<string> current, _) = ExtensionNormalizer.ParseList(ExtensionsText);
        if (current.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        ExtensionsText = current.Count == 0
            ? ext
            : $"{string.Join(", ", current)}, {ext}";
    }

    public void SelectDiscoveredApp(ApplicationChoiceViewModel choice)
    {
        iconCache.Seed(choice.App.Id, choice.App.IconPngBytes);
        SelectedTarget = choice.App.ToTarget();
        SelectedAppIcon = choice.Icon;
        IsPickerExpanded = false;
    }

    public async Task SetCustomExecutableAsync(string executablePath)
    {
        string friendly = Path.GetFileNameWithoutExtension(executablePath);
        try
        {
            System.Diagnostics.FileVersionInfo fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(executablePath);
            if (!string.IsNullOrWhiteSpace(fvi.FileDescription))
            {
                friendly = fvi.FileDescription.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(fvi.ProductName))
            {
                friendly = fvi.ProductName.Trim();
            }
        }
        catch
        {
        }

        ApplicationTarget target = new()
        {
            Kind = ApplicationTargetKind.CustomExecutable,
            ApplicationId = executablePath,
            DisplayName = friendly,
            ExecutablePath = executablePath
        };

        SelectedTarget = target;
        SelectedAppIcon = await iconCache.GetIconBitmapAsync(target);
        IsPickerExpanded = false;
    }

    public OpeningRule BuildRule()
    {
        (IReadOnlyList<string> parsedExts, _) = ExtensionNormalizer.ParseList(ExtensionsText);
        string folder = PathNormalizer.TryNormalizeFolderPath(FolderPath, out string normFolder)
            ? normFolder
            : FolderPath.Trim();

        return new OpeningRule
        {
            Id = _ruleId,
            Name = Name.Trim(),
            Enabled = true,
            Extensions = [.. parsedExts],
            FolderPath = folder,
            IncludeSubfolders = IncludeSubfolders,
            TargetApplication = SelectedTarget?.Clone() ?? new ApplicationTarget(),
            Priority = Math.Clamp((int)Math.Round(Priority), 0, 10000),
            CreatedUtc = _createdUtc,
            ModifiedUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task LoadSelectedIconAsync()
    {
        IReadOnlyList<string> parsed = ExtensionNormalizer.ParseList(ExtensionsText).Extensions;
        string? extHint = parsed.Count > 0 ? parsed[0] : null;
        SelectedAppIcon = await iconCache.GetIconBitmapAsync(_selectedTarget, extHint);
    }

    private async Task RefreshAppsAsync()
    {
        (IReadOnlyList<string> exts, _) = ExtensionNormalizer.ParseList(ExtensionsText);
        if (exts.Count == 0)
        {
            _allChoices.Clear();
            FilterDiscoveredApps();
            return;
        }

        IsDiscoveringApps = true;
        OnPropertyChanged(nameof(HasNoDiscoveredApps));

        try
        {
            Dictionary<string, DiscoveredApplication> merged = new(StringComparer.OrdinalIgnoreCase);
            foreach (string ext in exts.Take(3))
            {
                IReadOnlyList<DiscoveredApplication> apps = await discoveryService.GetHandlersForExtensionAsync(ext);
                foreach (DiscoveredApplication app in apps)
                {
                    if (!merged.ContainsKey(app.Id))
                    {
                        merged[app.Id] = app;
                        iconCache.Seed(app.Id, app.IconPngBytes);
                    }
                }
            }

            _allChoices.Clear();
            foreach (DiscoveredApplication app in merged.Values
                         .OrderByDescending(a => a.IsRecommended)
                         .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                BitmapImage? icon = await IconCacheService.DecodePngAsync(app.IconPngBytes);
                _allChoices.Add(new ApplicationChoiceViewModel
                {
                    App = app,
                    Icon = icon
                });
            }
        }
        finally
        {
            IsDiscoveringApps = false;
            FilterDiscoveredApps();
        }
    }

    private void FilterDiscoveredApps()
    {
        RecommendedApps.Clear();
        OtherApps.Clear();

        string query = _appSearchQuery.Trim();
        foreach (ApplicationChoiceViewModel choice in _allChoices)
        {
            if (!string.IsNullOrEmpty(query) &&
                !choice.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            if (choice.IsRecommended)
            {
                RecommendedApps.Add(choice);
            }
            else
            {
                OtherApps.Add(choice);
            }
        }

        OnPropertyChanged(nameof(HasRecommendedApps));
        OnPropertyChanged(nameof(HasOtherApps));
        OnPropertyChanged(nameof(HasNoDiscoveredApps));
    }

    private void SuggestRuleNameIfUntouched()
    {
        if (_nameManuallyEdited || string.IsNullOrWhiteSpace(FolderPath))
        {
            return;
        }

        string trimmed = FolderPath.Trim().TrimEnd('\\', '/');
        string folderName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = trimmed.TrimEnd(':');
        }

        (IReadOnlyList<string> exts, _) = ExtensionNormalizer.ParseList(ExtensionsText);
        if (string.IsNullOrWhiteSpace(folderName) || exts.Count == 0)
        {
            return;
        }

        string extLabel = exts.Count == 1
            ? exts[0].TrimStart('.').ToUpperInvariant() + "s"
            : "Files";

        _name = $"{folderName} {extLabel}";
        OnPropertyChanged(nameof(Name));
    }

    private void Validate()
    {
        (IReadOnlyList<string> parsedExts, IReadOnlyList<string> invalidTokens) =
            ExtensionNormalizer.ParseList(ExtensionsText);

        OpeningRule candidate = new()
        {
            Id = _ruleId,
            Name = Name.Trim(),
            Enabled = true,
            Extensions = [.. parsedExts],
            FolderPath = FolderPath.Trim(),
            IncludeSubfolders = IncludeSubfolders,
            TargetApplication = SelectedTarget?.Clone() ?? new ApplicationTarget(),
            Priority = double.IsNaN(Priority) ? 100 : (int)Math.Round(Priority)
        };

        ValidationResult validation = RuleValidator.Validate(candidate, fileSystem);
        if (invalidTokens.Count > 0)
        {
            validation.Errors.Insert(0, $"Invalid file extension(s): {string.Join(", ", invalidTokens)}");
        }

        ValidationError = validation.Errors.Count > 0 ? validation.Errors[0] : string.Empty;
        WarningSummary = validation.Warnings.Count > 0 ? string.Join(" ", validation.Warnings) : string.Empty;

        if (parsedExts.Count > 0 && PathNormalizer.TryNormalizeFolderPath(FolderPath, out _))
        {
            RuleOpenConfig config = configurationStore.Load();
            IReadOnlyList<RuleOverlapInfo> overlaps = ruleEngine.FindOverlaps(
                candidate,
                config.Rules,
                config.Settings.ConflictResolutionMode);
            OverlapSummary = overlaps.Count > 0 ? overlaps[0].Summary : string.Empty;
        }
        else
        {
            OverlapSummary = string.Empty;
        }

        CanSave = validation.IsValid && invalidTokens.Count == 0;
    }
}
