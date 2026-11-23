namespace RuleOpen.Core.Models;

/// <summary>
/// Identifies how a target application is referenced and launched.
/// </summary>
public enum ApplicationTargetKind
{
    /// <summary>
    /// An application registered with Windows Shell (Win32 ProgID, MSIX/Store AUMID, or shell handler).
    /// </summary>
    RegisteredHandler = 0,

    /// <summary>
    /// An explicit user-selected executable path.
    /// </summary>
    CustomExecutable = 1
}

/// <summary>
/// Controls how overlapping rules with different folder depths and priorities are ordered.
/// </summary>
public enum ConflictResolutionMode
{
    /// <summary>
    /// Deeper, more specific folder paths always win over broader parent folders before comparing priority.
    /// </summary>
    SpecificityFirst = 0,

    /// <summary>
    /// Explicit rule priority wins first; folder specificity breaks ties.
    /// </summary>
    PriorityFirst = 1
}

/// <summary>
/// Controls the management application's visual theme.
/// </summary>
public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}
