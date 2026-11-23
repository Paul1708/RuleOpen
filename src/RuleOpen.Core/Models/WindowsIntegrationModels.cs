namespace RuleOpen.Core.Models;

/// <summary>
/// Represents an installed application handler returned by OS discovery.
/// </summary>
public sealed record DiscoveredApplication
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public ApplicationTargetKind Kind { get; init; } = ApplicationTargetKind.RegisteredHandler;
    public string? ExecutablePath { get; init; }
    public string? ProgId { get; init; }
    public string? AppUserModelId { get; init; }
    public bool IsRecommended { get; init; }
    public string? IconLocation { get; init; }
    public byte[]? IconPngBytes { get; init; }

    public ApplicationTarget ToTarget() => new()
    {
        Kind = Kind,
        ApplicationId = Id,
        DisplayName = DisplayName,
        ExecutablePath = ExecutablePath,
        ProgId = ProgId,
        AppUserModelId = AppUserModelId
    };
}

/// <summary>
/// Describes Windows default-app and registration state for a single extension.
/// </summary>
public sealed record FileAssociationStatus
{
    public required string Extension { get; init; }
    public required string FriendlyTypeName { get; init; }
    public bool IsRegisteredWithWindows { get; init; }
    public bool IsRuleOpenDefault { get; init; }
    public string? CurrentDefaultDisplayName { get; init; }
    public string? CurrentDefaultProgId { get; init; }
    public string? CapturedFallbackDisplayName { get; init; }
}

public enum ModernContextMenuState
{
    Registered = 0,
    PackageAvailableNotRegistered = 1,
    PackageNotFound = 2,
    RegistrationBlocked = 3
}

/// <summary>
/// Current state of RuleOpen's File Explorer context menu integration.
/// </summary>
public sealed record ContextMenuStatus
{
    public ModernContextMenuState ModernState { get; init; }
    public required string ModernStatusDetail { get; init; }
    public bool IsClassicEnabled { get; init; }
    public bool CanRegisterModern { get; init; }
}
