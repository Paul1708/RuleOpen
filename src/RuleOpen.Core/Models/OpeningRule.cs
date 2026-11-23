namespace RuleOpen.Core.Models;

/// <summary>
/// Defines a rule mapping file extensions within a folder scope to a target application.
/// </summary>
public sealed class OpeningRule
{
    /// <summary>
    /// Unique stable identifier for the rule (used as deterministic tie-breaker).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// User-friendly name (e.g., "University PDFs").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this rule participates in evaluation.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Normalized lowercase file extensions including leading dots (e.g., ".pdf").
    /// </summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>
    /// Normalized root folder path where this rule applies.
    /// </summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// When true, files inside nested subdirectories of <see cref="FolderPath"/> also match.
    /// </summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>
    /// Application launched when this rule wins evaluation.
    /// </summary>
    public ApplicationTarget TargetApplication { get; set; } = new();

    /// <summary>
    /// Explicit numeric priority (higher wins). Defaults to 100.
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Optional future-ready profile tag (reserved for upcoming profile support).
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// UTC timestamp when the rule was created.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// UTC timestamp when the rule was last modified.
    /// </summary>
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates an independent copy of this rule.
    /// </summary>
    public OpeningRule Clone(bool generateNewId = false) => new()
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Enabled = Enabled,
        Extensions = [.. Extensions],
        FolderPath = FolderPath,
        IncludeSubfolders = IncludeSubfolders,
        TargetApplication = TargetApplication.Clone(),
        Priority = Priority,
        Profile = Profile,
        CreatedUtc = generateNewId ? DateTimeOffset.UtcNow : CreatedUtc,
        ModifiedUtc = DateTimeOffset.UtcNow
    };
}
