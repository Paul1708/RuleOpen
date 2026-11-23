using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Desktop.Services;
using RuleOpen.Windows.Shell;

namespace RuleOpen.Desktop.ViewModels;

public sealed class RecoveryViewModel(
    IConfigurationStore configurationStore,
    IApplicationDiscoveryService discoveryService,
    IApplicationLauncher launcher,
    IconCacheService iconCache) : ObservableObject
{
    private string _filePath = string.Empty;
    private string _headline = string.Empty;
    private string _explanation = string.Empty;
    private string? _ruleId;
    private string _statusMessage = string.Empty;

    public ObservableCollection<ApplicationChoiceViewModel> SuggestedApps { get; } = [];

    public string FilePath { get => _filePath; private set => SetProperty(ref _filePath, value); }
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Explanation { get => _explanation; private set => SetProperty(ref _explanation, value); }
    public string? RuleId { get => _ruleId; private set => SetProperty(ref _ruleId, value); }
    public string StatusMessage { get => _statusMessage; private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatusMessage)); } }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

    public async Task InitializeAsync(string filePath, string? reason, string? ruleId)
    {
        FilePath = filePath;
        RuleId = ruleId;

        (Headline, Explanation) = reason switch
        {
            nameof(DispatchOutcome.ApplicationMissing) => (
                "Target application could not be found",
                $"The application configured to open \"{Path.GetFileName(filePath)}\" appears to have been moved or uninstalled."),
            nameof(DispatchOutcome.RecursiveDispatchBlocked) => (
                "Launch loop prevented",
                $"RuleOpen stopped opening \"{Path.GetFileName(filePath)}\" because the selected target points back to RuleOpen."),
            nameof(DispatchOutcome.FileMissing) => (
                "File no longer exists",
                $"\"{filePath}\" could not be opened because it was moved, renamed, or deleted."),
            _ => (
                "No opening rule matched",
                $"No active RuleOpen rule or fallback is configured for \"{Path.GetFileName(filePath)}\". Choose an app to open it now or create a rule.")
        };

        string ext = Path.GetExtension(filePath);
        if (!string.IsNullOrWhiteSpace(ext))
        {
            IReadOnlyList<DiscoveredApplication> apps = await discoveryService.GetHandlersForExtensionAsync(ext);
            SuggestedApps.Clear();
            foreach (DiscoveredApplication app in apps.Take(8))
            {
                iconCache.Seed(app.Id, app.IconPngBytes);
                SuggestedApps.Add(new ApplicationChoiceViewModel
                {
                    App = app,
                    Icon = await IconCacheService.DecodePngAsync(app.IconPngBytes)
                });
            }
        }
    }

    public async Task<bool> LaunchWithAsync(ApplicationChoiceViewModel choice, bool saveRuleForFolder = false)
    {
        LaunchResult launch = await launcher.LaunchAsync(choice.App.ToTarget(), FilePath);
        if (!launch.Success)
        {
            StatusMessage = launch.ErrorMessage ?? "Could not launch the selected application.";
            return false;
        }

        if (saveRuleForFolder && File.Exists(FilePath))
        {
            string? folder = Path.GetDirectoryName(FilePath);
            string ext = Path.GetExtension(FilePath).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(ext))
            {
                RuleOpenConfig config = configurationStore.Load();
                string folderName = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder;
                config.Rules.Add(new OpeningRule
                {
                    Name = $"{folderName} {ext.TrimStart('.').ToUpperInvariant()}s",
                    Extensions = [ext],
                    FolderPath = folder,
                    IncludeSubfolders = true,
                    TargetApplication = choice.App.ToTarget()
                });
                configurationStore.Save(config);
            }
        }

        return true;
    }

    public bool ShowWindowsOpenWith(nint hwnd) => OpenWithDialogLauncher.ShowOpenWithDialog(FilePath, hwnd);
}
