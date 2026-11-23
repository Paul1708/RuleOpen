using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Services;
using RuleOpen.Desktop.Services;

namespace RuleOpen.Desktop.ViewModels;

public sealed class RuleTestCandidateDisplay
{
    public required string RuleName { get; init; }
    public required string ScopeDisplay { get; init; }
    public required string AppName { get; init; }
    public required string StatusSummary { get; init; }
    public bool IsWinner { get; init; }
}

public sealed class RuleTestViewModel(
    IConfigurationStore configurationStore,
    IRuleEngine ruleEngine,
    IApplicationDiscoveryService discoveryService,
    IFileAssociationService fileAssociationService,
    DispatchCoordinator dispatchCoordinator,
    IconCacheService iconCache) : ObservableObject
{
    private string _selectedFilePath = string.Empty;
    private bool _hasResult;
    private string _matchingRuleName = string.Empty;
    private string _selectedAppName = string.Empty;
    private string _selectedAppKindLabel = string.Empty;
    private string _statusTitle = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isReady;
    private bool _isError;
    private bool _showDefaultHandlerCallout;
    private string _defaultHandlerNotice = string.Empty;
    private string _normalizedPathDisplay = string.Empty;
    private string _resolutionModeDisplay = string.Empty;
    private string _launchFeedback = string.Empty;
    private BitmapImage? _selectedAppIcon;
    private RuleEvaluationResult? _lastEvaluation;

    public ObservableCollection<RuleTestCandidateDisplay> CandidateRows { get; } = [];

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
            {
                OnPropertyChanged(nameof(CanEvaluate));
            }
        }
    }

    public bool CanEvaluate => !string.IsNullOrWhiteSpace(_selectedFilePath);
    public bool HasResult { get => _hasResult; private set => SetProperty(ref _hasResult, value); }
    public string MatchingRuleName { get => _matchingRuleName; private set => SetProperty(ref _matchingRuleName, value); }
    public string SelectedAppName { get => _selectedAppName; private set => SetProperty(ref _selectedAppName, value); }
    public string SelectedAppKindLabel { get => _selectedAppKindLabel; private set => SetProperty(ref _selectedAppKindLabel, value); }
    public string StatusTitle { get => _statusTitle; private set => SetProperty(ref _statusTitle, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsReady { get => _isReady; private set => SetProperty(ref _isReady, value); }
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }
    public bool ShowDefaultHandlerCallout { get => _showDefaultHandlerCallout; private set => SetProperty(ref _showDefaultHandlerCallout, value); }
    public string DefaultHandlerNotice { get => _defaultHandlerNotice; private set => SetProperty(ref _defaultHandlerNotice, value); }
    public string NormalizedPathDisplay { get => _normalizedPathDisplay; private set => SetProperty(ref _normalizedPathDisplay, value); }
    public string ResolutionModeDisplay { get => _resolutionModeDisplay; private set => SetProperty(ref _resolutionModeDisplay, value); }
    public string LaunchFeedback { get => _launchFeedback; private set { if (SetProperty(ref _launchFeedback, value)) OnPropertyChanged(nameof(HasLaunchFeedback)); } }
    public bool HasLaunchFeedback => !string.IsNullOrWhiteSpace(_launchFeedback);

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

    public async Task EvaluateAsync()
    {
        LaunchFeedback = string.Empty;
        if (string.IsNullOrWhiteSpace(_selectedFilePath))
        {
            HasResult = false;
            return;
        }

        RuleOpenConfig config = configurationStore.Load();
        RuleEvaluationResult eval = ruleEngine.Evaluate(_selectedFilePath, config);
        _lastEvaluation = eval;

        HasResult = true;
        NormalizedPathDisplay = eval.NormalizedFilePath ?? eval.InputFilePath;
        ResolutionModeDisplay = eval.ResolutionMode == ConflictResolutionMode.SpecificityFirst
            ? "Specificity first (deeper folder beats broader folder)"
            : "Priority first (explicit rule priority beats folder depth)";

        CandidateRows.Clear();
        for (int i = 0; i < eval.MatchingCandidates.Count; i++)
        {
            RuleMatchCandidate c = eval.MatchingCandidates[i];
            CandidateRows.Add(new RuleTestCandidateDisplay
            {
                RuleName = c.Rule.Name,
                ScopeDisplay = $"{c.NormalizedRuleFolder} (depth {c.FolderDepth}, priority {c.Rule.Priority})",
                AppName = c.Rule.TargetApplication.DisplayName,
                StatusSummary = i == 0 ? "WINNER: " + c.Reason : "Matched (lower precedence): " + c.Reason,
                IsWinner = i == 0
            });
        }

        foreach (RuleMatchCandidate c in eval.NonMatchingRules)
        {
            CandidateRows.Add(new RuleTestCandidateDisplay
            {
                RuleName = c.Rule.Name,
                ScopeDisplay = c.NormalizedRuleFolder,
                AppName = c.Rule.TargetApplication.DisplayName,
                StatusSummary = "Skipped: " + c.Reason,
                IsWinner = false
            });
        }

        ApplicationTarget? resolvedTarget = null;
        string targetSourceLabel = string.Empty;

        if (eval.WinningRule is not null)
        {
            MatchingRuleName = eval.WinningRule.Name;
            resolvedTarget = eval.WinningRule.TargetApplication;
            targetSourceLabel = resolvedTarget.Kind == ApplicationTargetKind.CustomExecutable
                ? "Custom executable"
                : "Windows registered handler";
        }
        else if (!string.IsNullOrEmpty(eval.NormalizedExtension) &&
                 config.ExtensionFallbacks.TryGetValue(eval.NormalizedExtension, out ApplicationTarget? extFallback))
        {
            MatchingRuleName = "No rule matched (extension fallback)";
            resolvedTarget = extFallback;
            targetSourceLabel = "Captured previous Windows default";
        }
        else if (config.GlobalFallback is not null)
        {
            MatchingRuleName = "No rule matched (global fallback)";
            resolvedTarget = config.GlobalFallback;
            targetSourceLabel = "Configured global fallback";
        }
        else
        {
            MatchingRuleName = "No matching rule";
            SelectedAppName = "None";
            SelectedAppKindLabel = string.Empty;
            SelectedAppIcon = null;
        }

        if (resolvedTarget is not null)
        {
            SelectedAppName = resolvedTarget.DisplayName;
            SelectedAppKindLabel = targetSourceLabel;
            SelectedAppIcon = await iconCache.GetIconBitmapAsync(resolvedTarget, eval.NormalizedExtension);

            DiscoveredApplication? appCheck = await discoveryService.ResolveTargetAsync(resolvedTarget, eval.NormalizedExtension);
            if (eval.Status == RuleEvaluationStatus.InvalidInput)
            {
                IsReady = false;
                IsError = true;
                StatusTitle = "Invalid file path";
                StatusMessage = eval.Explanation;
            }
            else if (eval.Status == RuleEvaluationStatus.FileMissing)
            {
                IsReady = false;
                IsError = true;
                StatusTitle = "File not found";
                StatusMessage = eval.Explanation;
            }
            else if (appCheck is null)
            {
                IsReady = false;
                IsError = true;
                StatusTitle = "Target application missing";
                StatusMessage = $"\"{resolvedTarget.DisplayName}\" could not be located on this PC. Edit the rule to choose an installed app.";
            }
            else
            {
                IsReady = true;
                IsError = false;
                StatusTitle = "Ready to open";
                StatusMessage = eval.Explanation;
            }
        }
        else
        {
            IsReady = false;
            IsError = eval.Status is RuleEvaluationStatus.InvalidInput or RuleEvaluationStatus.FileMissing;
            StatusTitle = eval.Status switch
            {
                RuleEvaluationStatus.InvalidInput => "Invalid path",
                RuleEvaluationStatus.FileMissing => "File not found",
                _ => "No rule or fallback matches"
            };
            StatusMessage = eval.Explanation;
        }

        UpdateDefaultHandlerNotice(eval, config);
    }

    public async Task TestOpenNowAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedFilePath))
        {
            return;
        }

        DispatchResult result = await dispatchCoordinator.DispatchAsync(_selectedFilePath);
        LaunchFeedback = result.Message;
    }

    public void OpenDefaultApps() => fileAssociationService.OpenWindowsDefaultAppsSettings();

    private void UpdateDefaultHandlerNotice(RuleEvaluationResult eval, RuleOpenConfig config)
    {
        if (string.IsNullOrWhiteSpace(eval.NormalizedExtension))
        {
            ShowDefaultHandlerCallout = false;
            DefaultHandlerNotice = string.Empty;
            return;
        }

        IReadOnlyList<FileAssociationStatus> statuses = fileAssociationService
            .GetStatusForExtensions([eval.NormalizedExtension], config);
        FileAssociationStatus? status = statuses.Count > 0 ? statuses[0] : null;

        if (status is null)
        {
            ShowDefaultHandlerCallout = false;
            return;
        }

        if (!status.IsRuleOpenDefault)
        {
            ShowDefaultHandlerCallout = true;
            string currentName = !string.IsNullOrWhiteSpace(status.CurrentDefaultDisplayName)
                ? status.CurrentDefaultDisplayName
                : status.CurrentDefaultProgId ?? "another app";
            DefaultHandlerNotice =
                $"Double-clicking {status.Extension} files in File Explorer currently opens {currentName}. Select RuleOpen in Windows Default Apps to dispatch {status.Extension} files automatically.";
        }
        else
        {
            ShowDefaultHandlerCallout = false;
            DefaultHandlerNotice = string.Empty;
        }
    }
}
