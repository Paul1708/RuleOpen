namespace RuleOpen.Core.Models;

/// <summary>
/// Represents form/rule validation results with field-level messages.
/// </summary>
public sealed class ValidationResult
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool IsValid => Errors.Count == 0;
}

public enum LaunchFailureReason
{
    None = 0,
    ApplicationNotFound = 1,
    FileNotFound = 2,
    RecursiveDispatchBlocked = 3,
    HandlerUnavailable = 4,
    ProcessLaunchFailed = 5,
    InvalidArguments = 6
}

/// <summary>
/// Result of attempting to invoke a target application for a file.
/// </summary>
public sealed record LaunchResult
{
    public bool Success { get; init; }
    public LaunchFailureReason FailureReason { get; init; } = LaunchFailureReason.None;
    public string? ErrorMessage { get; init; }
    public string? DiagnosticDetail { get; init; }

    public static LaunchResult Ok(string? detail = null) => new()
    {
        Success = true,
        DiagnosticDetail = detail
    };

    public static LaunchResult Fail(LaunchFailureReason reason, string message, string? detail = null) => new()
    {
        Success = false,
        FailureReason = reason,
        ErrorMessage = message,
        DiagnosticDetail = detail
    };
}

public enum DispatchOutcome
{
    LaunchedByRule = 0,
    LaunchedByExtensionFallback = 1,
    LaunchedByGlobalFallback = 2,
    NoRuleAndNoFallback = 3,
    ApplicationMissing = 4,
    InvalidInput = 5,
    FileMissing = 6,
    RecursiveDispatchBlocked = 7,
    LaunchFailed = 8
}

/// <summary>
/// End-to-end outcome returned by <see cref="Services.DispatchCoordinator"/>.
/// </summary>
public sealed record DispatchResult
{
    public required string FilePath { get; init; }
    public DispatchOutcome Outcome { get; init; }
    public RuleEvaluationResult? Evaluation { get; init; }
    public ApplicationTarget? SelectedTarget { get; init; }
    public LaunchResult? LaunchResult { get; init; }
    public required string Message { get; init; }
}
