namespace RuleOpen.Core.Models;

/// <summary>
/// Represents the target application used to open matched files.
/// </summary>
public sealed class ApplicationTarget
{
    /// <summary>
    /// How the application should be resolved and invoked.
    /// </summary>
    public ApplicationTargetKind Kind { get; set; } = ApplicationTargetKind.RegisteredHandler;

    /// <summary>
    /// Stable identifier (AppUserModelId, ProgId, or normalized executable path).
    /// </summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>
    /// Friendly display name shown in the UI.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional resolved path to the executable (primarily for Win32 apps and custom executables).
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Optional ProgID associated with the handler.
    /// </summary>
    public string? ProgId { get; set; }

    /// <summary>
    /// Optional AppUserModelID for packaged / Store applications.
    /// </summary>
    public string? AppUserModelId { get; set; }

    /// <summary>
    /// Creates a deep copy of this application target.
    /// </summary>
    public ApplicationTarget Clone() => new()
    {
        Kind = Kind,
        ApplicationId = ApplicationId,
        DisplayName = DisplayName,
        ExecutablePath = ExecutablePath,
        ProgId = ProgId,
        AppUserModelId = AppUserModelId
    };
}
