namespace RuleOpen.Core.Models;

/// <summary>
/// Controls how File Explorer renders file icons for extensions whose Windows default is RuleOpen.
/// </summary>
public enum ExplorerIconMode
{
    /// <summary>
    /// Generate dedicated RuleOpen document icons clearly badged with each file extension.
    /// </summary>
    RuleOpenExtensionBadges = 0,

    /// <summary>
    /// Reuse the captured or system fallback application's document icon per extension.
    /// </summary>
    InheritFallbackIcon = 1,

    /// <summary>
    /// Dynamically display the icon of whichever application the matching folder rule (or fallback) routes to.
    /// </summary>
    MatchedRoutedAppIcon = 2
}

/// <summary>
/// Persisted user preferences for RuleOpen.
/// </summary>
public sealed class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public string Language { get; set; } = "en-US";

    public ConflictResolutionMode ConflictResolutionMode { get; set; } = ConflictResolutionMode.SpecificityFirst;

    public bool EnableDiagnosticLogging { get; set; } = true;

    public bool HasCompletedOnboarding { get; set; }

    public bool ClassicContextMenuEnabled { get; set; }

    public ExplorerIconMode ExplorerIconMode { get; set; } = ExplorerIconMode.MatchedRoutedAppIcon;

    /// <summary>
    /// Extensions currently registered by RuleOpen in Windows RegisteredApplications.
    /// </summary>
    public List<string> RegisteredExtensions { get; set; } = [];
}

/// <summary>
/// Root versioned configuration document persisted to disk.
/// </summary>
public sealed class RuleOpenConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<OpeningRule> Rules { get; set; } = [];

    /// <summary>
    /// Optional fallback application used when no rule matches and no per-extension fallback exists.
    /// </summary>
    public ApplicationTarget? GlobalFallback { get; set; }

    /// <summary>
    /// Captured previous Windows default handlers keyed by normalized extension (e.g., ".pdf").
    /// Prevents losing the prior default after RuleOpen becomes the active handler.
    /// </summary>
    public Dictionary<string, ApplicationTarget> ExtensionFallbacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AppSettings Settings { get; set; } = new();
}
