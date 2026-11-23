namespace RuleOpen.Core.Models;

public enum RuleEvaluationStatus
{
    Matched = 0,
    NoMatch = 1,
    InvalidInput = 2,
    FileMissing = 3
}

/// <summary>
/// Captures details about a single rule evaluated against a target file.
/// </summary>
public sealed record RuleMatchCandidate
{
    public required OpeningRule Rule { get; init; }
    public required string NormalizedRuleFolder { get; init; }
    public int FolderDepth { get; init; }
    public bool IsExactFolderMatch { get; init; }
    public bool Matched { get; init; }
    public string? MatchedExtension { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Full deterministic evaluation trace shared by both the live dispatcher and Rule Test diagnostics.
/// </summary>
public sealed class RuleEvaluationResult
{
    public required string InputFilePath { get; init; }
    public string? NormalizedFilePath { get; init; }
    public string? NormalizedParentFolder { get; init; }
    public string? NormalizedExtension { get; init; }
    public bool FileExists { get; init; }
    public RuleEvaluationStatus Status { get; init; }
    public OpeningRule? WinningRule { get; init; }
    public IReadOnlyList<RuleMatchCandidate> MatchingCandidates { get; init; } = [];
    public IReadOnlyList<RuleMatchCandidate> NonMatchingRules { get; init; } = [];
    public required string Explanation { get; init; }
    public ConflictResolutionMode ResolutionMode { get; init; }
}

public enum RuleOverlapKind
{
    ExactScope = 0,
    ParentIncludesChild = 1,
    ChildInsideParent = 2
}

/// <summary>
/// Describes an overlap between a rule being authored and an existing active rule.
/// </summary>
public sealed record RuleOverlapInfo
{
    public required string ExistingRuleId { get; init; }
    public required string ExistingRuleName { get; init; }
    public required IReadOnlyList<string> SharedExtensions { get; init; }
    public required RuleOverlapKind Kind { get; init; }
    public required string Summary { get; init; }
}
