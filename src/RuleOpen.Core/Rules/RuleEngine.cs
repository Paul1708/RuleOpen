using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;

namespace RuleOpen.Core.Rules;

/// <summary>
/// Deterministic, platform-independent opening-rule evaluation engine.
/// Used identically by both the lightweight dispatcher and the WinUI rule tester.
/// </summary>
public sealed class RuleEngine(IFileSystem fileSystem) : IRuleEngine
{
    public RuleEvaluationResult Evaluate(string filePath, RuleOpenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ConflictResolutionMode mode = config.Settings.ConflictResolutionMode;

        if (!PathNormalizer.TryNormalizeFilePath(
                filePath,
                out string normalizedFile,
                out string normalizedParent,
                out string? normalizedExt))
        {
            return new RuleEvaluationResult
            {
                InputFilePath = filePath ?? string.Empty,
                Status = RuleEvaluationStatus.InvalidInput,
                ResolutionMode = mode,
                Explanation = "The provided path is not a valid absolute file path."
            };
        }

        bool fileExists = fileSystem.FileExists(normalizedFile);

        if (normalizedExt is null)
        {
            return new RuleEvaluationResult
            {
                InputFilePath = filePath,
                NormalizedFilePath = normalizedFile,
                NormalizedParentFolder = normalizedParent,
                NormalizedExtension = null,
                FileExists = fileExists,
                Status = fileExists ? RuleEvaluationStatus.NoMatch : RuleEvaluationStatus.FileMissing,
                ResolutionMode = mode,
                Explanation = fileExists
                    ? "The file has no extension, so extension-based rules do not apply."
                    : "The file could not be found on disk and has no file extension."
            };
        }

        List<RuleMatchCandidate> matching = [];
        List<RuleMatchCandidate> nonMatching = [];

        foreach (OpeningRule rule in config.Rules)
        {
            if (!PathNormalizer.TryNormalizeFolderPath(rule.FolderPath, out string normalizedRuleFolder))
            {
                nonMatching.Add(new RuleMatchCandidate
                {
                    Rule = rule,
                    NormalizedRuleFolder = rule.FolderPath,
                    FolderDepth = 0,
                    Matched = false,
                    Reason = "Rule folder path is invalid."
                });
                continue;
            }

            int depth = PathNormalizer.GetFolderDepth(normalizedRuleFolder);

            if (!rule.Enabled)
            {
                nonMatching.Add(new RuleMatchCandidate
                {
                    Rule = rule,
                    NormalizedRuleFolder = normalizedRuleFolder,
                    FolderDepth = depth,
                    Matched = false,
                    Reason = "Rule is disabled."
                });
                continue;
            }

            IReadOnlyList<string> ruleExts = ExtensionNormalizer.NormalizeMany(rule.Extensions);
            string? extMatch = ruleExts.FirstOrDefault(e => string.Equals(e, normalizedExt, StringComparison.OrdinalIgnoreCase));
            if (extMatch is null)
            {
                nonMatching.Add(new RuleMatchCandidate
                {
                    Rule = rule,
                    NormalizedRuleFolder = normalizedRuleFolder,
                    FolderDepth = depth,
                    Matched = false,
                    Reason = $"Extension {normalizedExt} is not in rule ({string.Join(", ", ruleExts)})."
                });
                continue;
            }

            if (!PathNormalizer.IsFolderMatch(normalizedRuleFolder, rule.IncludeSubfolders, normalizedParent, out bool isExact))
            {
                string scopeLabel = rule.IncludeSubfolders ? "folder or subfolders" : "exact folder only";
                nonMatching.Add(new RuleMatchCandidate
                {
                    Rule = rule,
                    NormalizedRuleFolder = normalizedRuleFolder,
                    FolderDepth = depth,
                    IsExactFolderMatch = false,
                    Matched = false,
                    Reason = $"File location is outside rule scope ({scopeLabel})."
                });
                continue;
            }

            matching.Add(new RuleMatchCandidate
            {
                Rule = rule,
                NormalizedRuleFolder = normalizedRuleFolder,
                FolderDepth = depth,
                IsExactFolderMatch = isExact,
                Matched = true,
                MatchedExtension = extMatch,
                Reason = isExact
                    ? $"Matched exact folder (depth {depth}, priority {rule.Priority})."
                    : $"Matched via subfolder scope (depth {depth}, priority {rule.Priority})."
            });
        }

        matching.Sort(new RulePrecedenceComparer(mode));

        if (!fileExists)
        {
            return new RuleEvaluationResult
            {
                InputFilePath = filePath,
                NormalizedFilePath = normalizedFile,
                NormalizedParentFolder = normalizedParent,
                NormalizedExtension = normalizedExt,
                FileExists = false,
                Status = RuleEvaluationStatus.FileMissing,
                WinningRule = matching.Count > 0 ? matching[0].Rule : null,
                MatchingCandidates = matching,
                NonMatchingRules = nonMatching,
                ResolutionMode = mode,
                Explanation = matching.Count > 0
                    ? $"The file no longer exists on disk, though it would match \"{matching[0].Rule.Name}\"."
                    : "The file could not be found on disk."
            };
        }

        if (matching.Count == 0)
        {
            return new RuleEvaluationResult
            {
                InputFilePath = filePath,
                NormalizedFilePath = normalizedFile,
                NormalizedParentFolder = normalizedParent,
                NormalizedExtension = normalizedExt,
                FileExists = true,
                Status = RuleEvaluationStatus.NoMatch,
                MatchingCandidates = matching,
                NonMatchingRules = nonMatching,
                ResolutionMode = mode,
                Explanation = $"No active rule matches {normalizedExt} in \"{normalizedParent}\"."
            };
        }

        RuleMatchCandidate winner = matching[0];
        string explanation = matching.Count == 1
            ? $"Matched rule \"{winner.Rule.Name}\" -> {winner.Rule.TargetApplication.DisplayName}."
            : $"Matched {matching.Count} rules; \"{winner.Rule.Name}\" won via {FormatWinReason(winner, matching[1], mode)}.";

        return new RuleEvaluationResult
        {
            InputFilePath = filePath,
            NormalizedFilePath = normalizedFile,
            NormalizedParentFolder = normalizedParent,
            NormalizedExtension = normalizedExt,
            FileExists = true,
            Status = RuleEvaluationStatus.Matched,
            WinningRule = winner.Rule,
            MatchingCandidates = matching,
            NonMatchingRules = nonMatching,
            ResolutionMode = mode,
            Explanation = explanation
        };
    }

    public IReadOnlyList<RuleOverlapInfo> FindOverlaps(
        OpeningRule candidate,
        IEnumerable<OpeningRule> existingRules,
        ConflictResolutionMode mode = ConflictResolutionMode.SpecificityFirst)
    {
        if (!PathNormalizer.TryNormalizeFolderPath(candidate.FolderPath, out string candidateFolder))
        {
            return [];
        }

        IReadOnlyList<string> candidateExts = ExtensionNormalizer.NormalizeMany(candidate.Extensions);
        if (candidateExts.Count == 0)
        {
            return [];
        }

        List<RuleOverlapInfo> overlaps = [];

        foreach (OpeningRule existing in existingRules)
        {
            if (string.Equals(existing.Id, candidate.Id, StringComparison.Ordinal) || !existing.Enabled)
            {
                continue;
            }

            if (!PathNormalizer.TryNormalizeFolderPath(existing.FolderPath, out string existingFolder))
            {
                continue;
            }

            IReadOnlyList<string> existingExts = ExtensionNormalizer.NormalizeMany(existing.Extensions);
            List<string> sharedExts = candidateExts
                .Where(c => existingExts.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (sharedExts.Count == 0)
            {
                continue;
            }

            string extSummary = string.Join(", ", sharedExts);

            if (string.Equals(candidateFolder, existingFolder, StringComparison.OrdinalIgnoreCase))
            {
                string precedenceHint = candidate.Priority > existing.Priority
                    ? $"Your rule (priority {candidate.Priority}) will take priority over \"{existing.Name}\" (priority {existing.Priority})."
                    : candidate.Priority < existing.Priority
                        ? $"\"{existing.Name}\" (priority {existing.Priority}) currently takes priority unless you raise priority above {existing.Priority}."
                        : "Both rules share the same folder and priority; consider adjusting priority or extensions.";

                overlaps.Add(new RuleOverlapInfo
                {
                    ExistingRuleId = existing.Id,
                    ExistingRuleName = existing.Name,
                    SharedExtensions = sharedExts,
                    Kind = RuleOverlapKind.ExactScope,
                    Summary = $"This folder already has rule \"{existing.Name}\" for {extSummary}. {precedenceHint}"
                });
            }
            else if (candidate.IncludeSubfolders && PathNormalizer.IsAncestorOrEqual(candidateFolder, existingFolder))
            {
                string priorityNote = mode == ConflictResolutionMode.SpecificityFirst
                    ? $"\"{existing.Name}\" is more specific and will still take priority in its folder."
                    : "Precedence depends on rule priority (Priority-first mode).";

                overlaps.Add(new RuleOverlapInfo
                {
                    ExistingRuleId = existing.Id,
                    ExistingRuleName = existing.Name,
                    SharedExtensions = sharedExts,
                    Kind = RuleOverlapKind.ParentIncludesChild,
                    Summary = $"Subfolder rule \"{existing.Name}\" also handles {extSummary}. {priorityNote}"
                });
            }
            else if (existing.IncludeSubfolders && PathNormalizer.IsAncestorOrEqual(existingFolder, candidateFolder))
            {
                string priorityNote = mode == ConflictResolutionMode.SpecificityFirst
                    ? "Your rule targets a more specific folder and will take priority here."
                    : "Precedence depends on rule priority (Priority-first mode).";

                overlaps.Add(new RuleOverlapInfo
                {
                    ExistingRuleId = existing.Id,
                    ExistingRuleName = existing.Name,
                    SharedExtensions = sharedExts,
                    Kind = RuleOverlapKind.ChildInsideParent,
                    Summary = $"Parent folder rule \"{existing.Name}\" already includes {extSummary} in subfolders. {priorityNote}"
                });
            }
        }

        return overlaps;
    }

    private static string FormatWinReason(RuleMatchCandidate winner, RuleMatchCandidate runnerUp, ConflictResolutionMode mode)
    {
        if (mode == ConflictResolutionMode.SpecificityFirst)
        {
            if (winner.FolderDepth != runnerUp.FolderDepth)
            {
                return $"more specific folder depth ({winner.FolderDepth} vs {runnerUp.FolderDepth})";
            }

            if (winner.IsExactFolderMatch != runnerUp.IsExactFolderMatch)
            {
                return "exact folder match over subfolder scope";
            }

            if (winner.Rule.Priority != runnerUp.Rule.Priority)
            {
                return $"higher priority ({winner.Rule.Priority} vs {runnerUp.Rule.Priority})";
            }
        }
        else
        {
            if (winner.Rule.Priority != runnerUp.Rule.Priority)
            {
                return $"higher priority ({winner.Rule.Priority} vs {runnerUp.Rule.Priority})";
            }

            if (winner.FolderDepth != runnerUp.FolderDepth)
            {
                return $"more specific folder depth ({winner.FolderDepth} vs {runnerUp.FolderDepth})";
            }

            if (winner.IsExactFolderMatch != runnerUp.IsExactFolderMatch)
            {
                return "exact folder match over subfolder scope";
            }
        }

        if (winner.Rule.Extensions.Count != runnerUp.Rule.Extensions.Count)
        {
            return "narrower extension list";
        }

        return "deterministic rule ID tie-breaker";
    }
}
