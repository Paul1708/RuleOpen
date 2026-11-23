using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Desktop.Services;

namespace RuleOpen.Desktop.ViewModels;

public sealed class AppPickerViewModel(
    IApplicationDiscoveryService discoveryService,
    IconCacheService iconCache) : ObservableObject
{
    private static readonly string[] GlobalFallbackSampleExtensions = [".pdf", ".txt", ".html", ".png"];
    private readonly List<ApplicationChoiceViewModel> _allChoices = [];
    private string _dialogTitle = "Choose an application";
    private string _subtitle = "Select a registered Windows application or browse for an executable.";
    private string _searchQuery = string.Empty;
    private bool _isDiscovering;
    private ApplicationTarget? _selectedTarget;
    private BitmapImage? _selectedAppIcon;

    public ObservableCollection<ApplicationChoiceViewModel> RecommendedApps { get; } = [];
    public ObservableCollection<ApplicationChoiceViewModel> OtherApps { get; } = [];

    public string DialogTitle
    {
        get => _dialogTitle;
        private set => SetProperty(ref _dialogTitle, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

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

    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set
        {
            if (SetProperty(ref _isDiscovering, value))
            {
                OnPropertyChanged(nameof(HasNoDiscoveredApps));
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
                OnPropertyChanged(nameof(SelectedAppName));
                OnPropertyChanged(nameof(SelectedAppDetail));
                OnPropertyChanged(nameof(CanConfirm));
            }
        }
    }

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

    public string SelectedAppName => _selectedTarget?.DisplayName ?? "No application selected";
    public string SelectedAppDetail => _selectedTarget is null
        ? "Pick an app below or browse for a custom .exe"
        : _selectedTarget.Kind == ApplicationTargetKind.CustomExecutable
            ? $"Custom executable • {_selectedTarget.ExecutablePath}"
            : "Windows registered handler";

    public bool HasSelectedAppIcon => _selectedAppIcon is not null;
    public bool MissingSelectedAppIcon => _selectedAppIcon is null;
    public bool CanConfirm => _selectedTarget is not null;
    public bool HasRecommendedApps => RecommendedApps.Count > 0;
    public bool HasOtherApps => OtherApps.Count > 0;
    public bool HasNoDiscoveredApps => !IsDiscovering && RecommendedApps.Count == 0 && OtherApps.Count == 0;

    public async Task InitializeForExtensionAsync(string extension, ApplicationTarget? current)
    {
        DialogTitle = $"Choose fallback app for {extension}";
        Subtitle = $"Used when no rule matches {extension} files, and for its Explorer icon when inheriting fallback icons.";
        await InitializeSelectionAsync(current, extension);
        await LoadHandlersAsync(new[] { extension });
    }

    public async Task InitializeForGlobalFallbackAsync(ApplicationTarget? current)
    {
        DialogTitle = "Choose global fallback app";
        Subtitle = "Used when no folder rule and no per-extension fallback matches.";
        await InitializeSelectionAsync(current, null);
        await LoadHandlersAsync(GlobalFallbackSampleExtensions);
    }

    public void SelectChoice(ApplicationChoiceViewModel choice)
    {
        iconCache.Seed(choice.App.Id, choice.App.IconPngBytes);
        SelectedTarget = choice.App.ToTarget();
        SelectedAppIcon = choice.Icon;
    }

    public async Task SetCustomExecutableAsync(string executablePath)
    {
        string friendly = Path.GetFileNameWithoutExtension(executablePath);
        try
        {
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(executablePath);
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
    }

    private async Task InitializeSelectionAsync(ApplicationTarget? current, string? extensionHint)
    {
        if (current is not null)
        {
            SelectedTarget = current.Clone();
            SelectedAppIcon = await iconCache.GetIconBitmapAsync(SelectedTarget, extensionHint);
        }
        else
        {
            SelectedTarget = null;
            SelectedAppIcon = null;
        }
    }

    private async Task LoadHandlersAsync(IReadOnlyList<string> extensions)
    {
        IsDiscovering = true;
        try
        {
            Dictionary<string, DiscoveredApplication> merged = new(StringComparer.OrdinalIgnoreCase);
            foreach (string ext in extensions)
            {
                IReadOnlyList<DiscoveredApplication> found = await discoveryService.GetHandlersForExtensionAsync(ext);
                foreach (DiscoveredApplication app in found)
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
            IsDiscovering = false;
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        RecommendedApps.Clear();
        OtherApps.Clear();

        string query = _searchQuery.Trim();
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
}
