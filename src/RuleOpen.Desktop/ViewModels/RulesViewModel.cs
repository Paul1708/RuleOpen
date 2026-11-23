using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using RuleOpen.Desktop.Services;

namespace RuleOpen.Desktop.ViewModels;

public sealed class RulesViewModel(
    IConfigurationStore configurationStore,
    IFileSystem fileSystem,
    IFileAssociationService fileAssociationService,
    IconCacheService iconCache,
    IDiagnosticLogger logger) : ObservableObject
{
    private readonly List<RuleItemViewModel> _allItems = [];
    private string _searchQuery = string.Empty;
    private string? _configWarning;
    private bool _showDefaultAppsPrompt;
    private string _defaultAppsPromptText = string.Empty;
    private bool _dismissedDefaultPrompt;

    public ObservableCollection<RuleItemViewModel> FilteredRules { get; } = [];

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public int TotalRuleCount => _allItems.Count;
    public bool HasNoRules => _allItems.Count == 0;
    public bool HasRules => _allItems.Count > 0;
    public bool HasSearchFilter => !string.IsNullOrWhiteSpace(_searchQuery);
    public bool ShowNoSearchMatches => HasRules && FilteredRules.Count == 0;
    public string FilterSummary => HasSearchFilter
        ? $"Showing {FilteredRules.Count} of {_allItems.Count} rules"
        : $"{_allItems.Count} {(_allItems.Count == 1 ? "rule" : "rules")}";

    public string? ConfigWarning
    {
        get => _configWarning;
        set
        {
            if (SetProperty(ref _configWarning, value))
            {
                OnPropertyChanged(nameof(HasConfigWarning));
            }
        }
    }

    public bool HasConfigWarning => !string.IsNullOrWhiteSpace(_configWarning);

    public bool ShowDefaultAppsPrompt
    {
        get => _showDefaultAppsPrompt;
        set => SetProperty(ref _showDefaultAppsPrompt, value);
    }

    public string DefaultAppsPromptText
    {
        get => _defaultAppsPromptText;
        private set => SetProperty(ref _defaultAppsPromptText, value);
    }

    public void Load()
    {
        RuleOpenConfig config = configurationStore.Load();
        ConfigWarning = configurationStore.LastLoadWarning;

        _allItems.Clear();
        foreach (OpeningRule rule in config.Rules
                     .OrderByDescending(r => r.Enabled)
                     .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            RuleItemViewModel item = new(rule, fileSystem, OnRuleToggled);
            _allItems.Add(item);
            _ = item.LoadIconAsync(iconCache);
        }

        UpdateDefaultAppsBanner(config);
        ApplyFilter();
    }

    public void SaveNewOrEditedRule(OpeningRule rule, bool isEdit)
    {
        RuleOpenConfig config = configurationStore.Load();
        if (isEdit)
        {
            int idx = config.Rules.FindIndex(r => string.Equals(r.Id, rule.Id, StringComparison.Ordinal));
            if (idx >= 0)
            {
                rule.ModifiedUtc = DateTimeOffset.UtcNow;
                config.Rules[idx] = rule;
            }
            else
            {
                config.Rules.Add(rule);
            }
        }
        else
        {
            config.Rules.Add(rule);
        }

        // Automatically register extensions used in rules so RuleOpen appears in Windows Open With / Default Apps.
        IReadOnlyList<string> exts = ExtensionNormalizer.NormalizeMany(rule.Extensions);
        if (exts.Count > 0)
        {
            try
            {
                fileAssociationService.RegisterExtensions(exts, config);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Associations", $"Could not auto-register rule extensions: {ex.Message}");
            }
        }

        configurationStore.Save(config);
        RefreshRoutedIconsIfActive(config);
        Load();
    }

    public void DuplicateRule(RuleItemViewModel item)
    {
        RuleOpenConfig config = configurationStore.Load();
        OpeningRule copy = item.Rule.Clone(generateNewId: true);
        copy.Name = $"{item.Rule.Name} (Copy)";
        config.Rules.Add(copy);
        configurationStore.Save(config);
        RefreshRoutedIconsIfActive(config);
        Load();
    }

    public void DeleteRule(RuleItemViewModel item)
    {
        RuleOpenConfig config = configurationStore.Load();
        config.Rules.RemoveAll(r => string.Equals(r.Id, item.Id, StringComparison.Ordinal));
        configurationStore.Save(config);
        RefreshRoutedIconsIfActive(config);
        Load();
    }

    public void DismissDefaultAppsPrompt()
    {
        _dismissedDefaultPrompt = true;
        ShowDefaultAppsPrompt = false;
    }

    public void OpenDefaultAppsSettings() => fileAssociationService.OpenWindowsDefaultAppsSettings();

    private void OnRuleToggled(RuleItemViewModel item, bool enabled)
    {
        RuleOpenConfig config = configurationStore.Load();
        OpeningRule? target = config.Rules.FirstOrDefault(r => string.Equals(r.Id, item.Id, StringComparison.Ordinal));
        if (target is null)
        {
            return;
        }

        target.Enabled = enabled;
        target.ModifiedUtc = DateTimeOffset.UtcNow;
        configurationStore.Save(config);
        RefreshRoutedIconsIfActive(config);
        UpdateDefaultAppsBanner(config);
    }

    private void RefreshRoutedIconsIfActive(RuleOpenConfig config)
    {
        if (config.Settings.ExplorerIconMode != ExplorerIconMode.MatchedRoutedAppIcon)
        {
            return;
        }

        try
        {
            List<string> tracked = config.Settings.RegisteredExtensions
                .Concat(config.Rules.SelectMany(r => r.Extensions))
                .Concat(config.ExtensionFallbacks.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tracked.Count > 0)
            {
                fileAssociationService.RefreshExtensionIcons(tracked, config);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Associations", $"Could not refresh per-folder Explorer icon routes: {ex.Message}");
        }
    }

    private void ApplyFilter()
    {
        FilteredRules.Clear();
        string query = _searchQuery.Trim();

        foreach (RuleItemViewModel item in _allItems)
        {
            if (string.IsNullOrEmpty(query) || MatchesQuery(item, query))
            {
                FilteredRules.Add(item);
            }
        }

        OnPropertyChanged(nameof(TotalRuleCount));
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasSearchFilter));
        OnPropertyChanged(nameof(ShowNoSearchMatches));
        OnPropertyChanged(nameof(FilterSummary));
    }

    private static bool MatchesQuery(RuleItemViewModel item, string query)
    {
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.FolderPath.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.ApplicationName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Extensions.Any(ext => ext.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateDefaultAppsBanner(RuleOpenConfig config)
    {
        if (_dismissedDefaultPrompt)
        {
            ShowDefaultAppsPrompt = false;
            return;
        }

        List<string> activeExtensions = config.Rules
            .Where(r => r.Enabled)
            .SelectMany(r => r.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (activeExtensions.Count == 0)
        {
            ShowDefaultAppsPrompt = false;
            return;
        }

        IReadOnlyList<FileAssociationStatus> statuses = fileAssociationService.GetStatusForExtensions(activeExtensions, config);
        List<string> nonDefault = statuses
            .Where(s => !s.IsRuleOpenDefault)
            .Select(s => s.Extension)
            .ToList();

        if (nonDefault.Count == 0)
        {
            ShowDefaultAppsPrompt = false;
            return;
        }

        string extSample = string.Join(", ", nonDefault.Take(4));
        if (nonDefault.Count > 4)
        {
            extSample += $" (+{nonDefault.Count - 4} more)";
        }

        DefaultAppsPromptText =
            $"To open {extSample} automatically when double-clicked in Explorer, set RuleOpen as your default app in Windows Settings (or right-click -> RuleOpen -> Open using RuleOpen).";
        ShowDefaultAppsPrompt = true;
    }
}
