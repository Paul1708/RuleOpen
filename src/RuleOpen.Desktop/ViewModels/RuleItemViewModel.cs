using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Desktop.Services;

namespace RuleOpen.Desktop.ViewModels;

public sealed class RuleItemViewModel : ObservableObject
{
    private readonly Action<RuleItemViewModel, bool> _onToggleChanged;
    private bool _suppressToggleCallback;
    private BitmapImage? _appIcon;
    private bool _isEnabled;

    public RuleItemViewModel(
        OpeningRule rule,
        IFileSystem fileSystem,
        Action<RuleItemViewModel, bool> onToggleChanged)
    {
        Rule = rule;
        _onToggleChanged = onToggleChanged;
        _suppressToggleCallback = true;
        _isEnabled = rule.Enabled;
        _suppressToggleCallback = false;

        FolderExists = string.IsNullOrWhiteSpace(rule.FolderPath) || fileSystem.DirectoryExists(rule.FolderPath);
        TargetMissing = rule.TargetApplication.Kind == ApplicationTargetKind.CustomExecutable &&
                        !string.IsNullOrWhiteSpace(rule.TargetApplication.ExecutablePath) &&
                        !fileSystem.FileExists(rule.TargetApplication.ExecutablePath);
    }

    public OpeningRule Rule { get; }
    public string Id => Rule.Id;
    public string Name => Rule.Name;
    public IReadOnlyList<string> Extensions => Rule.Extensions;
    public string ExtensionsDisplay => string.Join(", ", Rule.Extensions);
    public string FolderPath => Rule.FolderPath;
    public string SubfolderLabel => Rule.IncludeSubfolders ? "•  Includes subfolders" : "•  This folder only";
    public string ScopeSummary => Rule.IncludeSubfolders
        ? $"{Rule.FolderPath}  •  Includes subfolders"
        : $"{Rule.FolderPath}  •  This folder only";
    public string ApplicationName => Rule.TargetApplication.DisplayName;
    public bool HasCustomPriority => Rule.Priority != 100;
    public string PriorityBadge => $"Priority {Rule.Priority}";
    public bool FolderExists { get; }
    public bool TargetMissing { get; }
    public bool HasWarning => !FolderExists || TargetMissing;
    public string WarningText => TargetMissing
        ? "Target app not found"
        : !FolderExists ? "Folder missing" : string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                Rule.Enabled = value;
                if (!_suppressToggleCallback)
                {
                    _onToggleChanged(this, value);
                }
            }
        }
    }

    public BitmapImage? AppIcon
    {
        get => _appIcon;
        private set
        {
            if (SetProperty(ref _appIcon, value))
            {
                OnPropertyChanged(nameof(HasAppIcon));
                OnPropertyChanged(nameof(MissingAppIcon));
            }
        }
    }

    public bool HasAppIcon => _appIcon is not null;
    public bool MissingAppIcon => _appIcon is null;

    public async Task LoadIconAsync(IconCacheService iconCache)
    {
        string? extHint = Rule.Extensions.FirstOrDefault();
        BitmapImage? bmp = await iconCache.GetIconBitmapAsync(Rule.TargetApplication, extHint);
        AppIcon = bmp;
    }
}
