using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;

namespace RuleOpen.Core.Services;

/// <summary>
/// Orchestrates rule evaluation, fallback selection, self-dispatch protection,
/// and application invocation for a file.
/// </summary>
public sealed class DispatchCoordinator(
    IConfigurationStore configurationStore,
    IRuleEngine ruleEngine,
    IApplicationLauncher launcher,
    IDiagnosticLogger logger)
{
    public Task<DispatchResult> DispatchAsync(string filePath, CancellationToken cancellationToken = default)
    {
        RuleOpenConfig config = configurationStore.Load();
        logger.IsEnabled = config.Settings.EnableDiagnosticLogging;
        return DispatchWithConfigAsync(filePath, config, cancellationToken);
    }

    public async Task<DispatchResult> DispatchWithConfigAsync(
        string filePath,
        RuleOpenConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        RuleEvaluationResult evaluation = ruleEngine.Evaluate(filePath, config);

        if (evaluation.Status == RuleEvaluationStatus.InvalidInput)
        {
            logger.LogWarning("Dispatch", $"Rejected invalid file path input ({evaluation.Explanation}).");
            return new DispatchResult
            {
                FilePath = filePath,
                Outcome = DispatchOutcome.InvalidInput,
                Evaluation = evaluation,
                Message = evaluation.Explanation
            };
        }

        if (evaluation.Status == RuleEvaluationStatus.FileMissing)
        {
            logger.LogWarning("Dispatch", $"Target file missing for extension {evaluation.NormalizedExtension ?? "(none)"}.");
            return new DispatchResult
            {
                FilePath = evaluation.NormalizedFilePath ?? filePath,
                Outcome = DispatchOutcome.FileMissing,
                Evaluation = evaluation,
                Message = evaluation.Explanation
            };
        }

        string targetPath = evaluation.NormalizedFilePath ?? filePath;

        if (evaluation.Status == RuleEvaluationStatus.Matched && evaluation.WinningRule is not null)
        {
            ApplicationTarget ruleTarget = evaluation.WinningRule.TargetApplication;
            if (launcher.IsSelfTarget(ruleTarget))
            {
                logger.LogWarning("Dispatch", $"Blocked self-targeting rule \"{evaluation.WinningRule.Name}\".");
                return new DispatchResult
                {
                    FilePath = targetPath,
                    Outcome = DispatchOutcome.RecursiveDispatchBlocked,
                    Evaluation = evaluation,
                    SelectedTarget = ruleTarget,
                    Message = $"Rule \"{evaluation.WinningRule.Name}\" points back to RuleOpen and was blocked to prevent a launch loop."
                };
            }

            LaunchResult launchResult = await launcher.LaunchAsync(ruleTarget, targetPath, cancellationToken)
                .ConfigureAwait(false);

            if (launchResult.Success)
            {
                logger.LogInfo("Dispatch", $"Launched {evaluation.NormalizedExtension} via rule \"{evaluation.WinningRule.Name}\" -> {ruleTarget.DisplayName}.");
                return new DispatchResult
                {
                    FilePath = targetPath,
                    Outcome = DispatchOutcome.LaunchedByRule,
                    Evaluation = evaluation,
                    SelectedTarget = ruleTarget,
                    LaunchResult = launchResult,
                    Message = $"Opened with {ruleTarget.DisplayName} via rule \"{evaluation.WinningRule.Name}\"."
                };
            }

            if (launchResult.FailureReason == LaunchFailureReason.RecursiveDispatchBlocked)
            {
                logger.LogWarning("Dispatch", $"Recursive dispatch blocked for \"{evaluation.WinningRule.Name}\".");
                return new DispatchResult
                {
                    FilePath = targetPath,
                    Outcome = DispatchOutcome.RecursiveDispatchBlocked,
                    Evaluation = evaluation,
                    SelectedTarget = ruleTarget,
                    LaunchResult = launchResult,
                    Message = launchResult.ErrorMessage ?? "Recursive dispatch was prevented."
                };
            }

            logger.LogError("Dispatch", $"Rule \"{evaluation.WinningRule.Name}\" failed to launch target ({launchResult.FailureReason}): {launchResult.ErrorMessage}");
            return new DispatchResult
            {
                FilePath = targetPath,
                Outcome = launchResult.FailureReason == LaunchFailureReason.ApplicationNotFound
                    ? DispatchOutcome.ApplicationMissing
                    : DispatchOutcome.LaunchFailed,
                Evaluation = evaluation,
                SelectedTarget = ruleTarget,
                LaunchResult = launchResult,
                Message = launchResult.ErrorMessage ?? $"Could not open the file using {ruleTarget.DisplayName}."
            };
        }

        // No rule matched: consult captured per-extension fallback first, then global fallback.
        if (!string.IsNullOrEmpty(evaluation.NormalizedExtension) &&
            config.ExtensionFallbacks.TryGetValue(evaluation.NormalizedExtension, out ApplicationTarget? extFallback))
        {
            DispatchResult fallbackResult = await TryLaunchFallbackAsync(
                extFallback,
                targetPath,
                evaluation,
                DispatchOutcome.LaunchedByExtensionFallback,
                cancellationToken).ConfigureAwait(false);

            if (fallbackResult.LaunchResult?.Success == true)
            {
                return fallbackResult;
            }

            if (fallbackResult.Outcome == DispatchOutcome.RecursiveDispatchBlocked)
            {
                return fallbackResult;
            }
        }

        if (config.GlobalFallback is not null)
        {
            return await TryLaunchFallbackAsync(
                config.GlobalFallback,
                targetPath,
                evaluation,
                DispatchOutcome.LaunchedByGlobalFallback,
                cancellationToken).ConfigureAwait(false);
        }

        logger.LogInfo("Dispatch", $"No matching rule or fallback for extension {evaluation.NormalizedExtension ?? "(none)"}.");
        return new DispatchResult
        {
            FilePath = targetPath,
            Outcome = DispatchOutcome.NoRuleAndNoFallback,
            Evaluation = evaluation,
            Message = evaluation.Explanation
        };
    }

    private async Task<DispatchResult> TryLaunchFallbackAsync(
        ApplicationTarget fallbackTarget,
        string targetPath,
        RuleEvaluationResult evaluation,
        DispatchOutcome successOutcome,
        CancellationToken cancellationToken)
    {
        if (launcher.IsSelfTarget(fallbackTarget))
        {
            logger.LogWarning("Dispatch", "Blocked fallback pointing back to RuleOpen.");
            return new DispatchResult
            {
                FilePath = targetPath,
                Outcome = DispatchOutcome.RecursiveDispatchBlocked,
                Evaluation = evaluation,
                SelectedTarget = fallbackTarget,
                Message = "Fallback handler points to RuleOpen itself and was blocked."
            };
        }

        LaunchResult result = await launcher.LaunchAsync(fallbackTarget, targetPath, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            logger.LogInfo("Dispatch", $"Opened {evaluation.NormalizedExtension} via fallback {fallbackTarget.DisplayName}.");
            return new DispatchResult
            {
                FilePath = targetPath,
                Outcome = successOutcome,
                Evaluation = evaluation,
                SelectedTarget = fallbackTarget,
                LaunchResult = result,
                Message = $"No rule matched; opened with fallback {fallbackTarget.DisplayName}."
            };
        }

        logger.LogWarning("Dispatch", $"Fallback {fallbackTarget.DisplayName} failed ({result.FailureReason}): {result.ErrorMessage}");
        return new DispatchResult
        {
            FilePath = targetPath,
            Outcome = result.FailureReason == LaunchFailureReason.ApplicationNotFound
                ? DispatchOutcome.ApplicationMissing
                : DispatchOutcome.LaunchFailed,
            Evaluation = evaluation,
            SelectedTarget = fallbackTarget,
            LaunchResult = result,
            Message = result.ErrorMessage ?? $"Fallback application {fallbackTarget.DisplayName} could not be started."
        };
    }
}
