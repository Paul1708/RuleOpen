using RuleOpen.Core.Models;

namespace RuleOpen.Core.Interfaces;

/// <summary>
/// Abstracts filesystem checks for deterministic unit testing.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
}

/// <summary>
/// Reads and writes RuleOpen configuration atomically across processes.
/// </summary>
public interface IConfigurationStore
{
    string ConfigFilePath { get; }
    string? LastLoadWarning { get; }
    RuleOpenConfig Load();
    Task<RuleOpenConfig> LoadAsync(CancellationToken cancellationToken = default);
    void Save(RuleOpenConfig config);
    Task SaveAsync(RuleOpenConfig config, CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates opening rules deterministically for a given file path.
/// </summary>
public interface IRuleEngine
{
    RuleEvaluationResult Evaluate(string filePath, RuleOpenConfig config);
    IReadOnlyList<RuleOverlapInfo> FindOverlaps(
        OpeningRule candidate,
        IEnumerable<OpeningRule> existingRules,
        ConflictResolutionMode mode = ConflictResolutionMode.SpecificityFirst);
}

/// <summary>
/// Discovers compatible Windows applications and resolves icons.
/// </summary>
public interface IApplicationDiscoveryService
{
    Task<IReadOnlyList<DiscoveredApplication>> GetHandlersForExtensionAsync(
        string extension,
        CancellationToken cancellationToken = default);

    Task<DiscoveredApplication?> ResolveTargetAsync(
        ApplicationTarget target,
        string? extensionHint = null,
        CancellationToken cancellationToken = default);

    Task<byte[]?> ExtractExecutableIconAsync(
        string executablePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Launches target applications cleanly with recursive-dispatch protection.
/// </summary>
public interface IApplicationLauncher
{
    bool IsSelfTarget(ApplicationTarget target);
    Task<LaunchResult> LaunchAsync(
        ApplicationTarget target,
        string filePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Manages per-user Windows file association capability entries and settings deep-links.
/// </summary>
public interface IFileAssociationService
{
    IReadOnlyList<string> GetCuratedExtensions();
    string GetFriendlyTypeName(string extension);
    IReadOnlyList<FileAssociationStatus> GetStatusForExtensions(
        IEnumerable<string> extensions,
        RuleOpenConfig config);
    void RegisterExtensions(IEnumerable<string> extensions, RuleOpenConfig config);
    void EnsureRegistered(RuleOpenConfig config, bool registerStarterExtensionsIfEmpty = false);
    void RefreshExtensionIcons(IEnumerable<string> extensions, RuleOpenConfig config);
    IReadOnlyList<FileAssociationStatus> GetActiveRuleOpenDefaultExtensions(RuleOpenConfig config);
    void UnregisterExtension(string extension, RuleOpenConfig config);
    ApplicationTarget? CaptureCurrentDefaultHandler(string extension);
    void OpenWindowsDefaultAppsSettings();
    void RemoveAllRegistrations();
}

/// <summary>
/// Inspects and manages Windows 11 modern and classic File Explorer context menus.
/// </summary>
public interface IContextMenuService
{
    ContextMenuStatus GetStatus();
    Task<(bool Success, string Message)> EnableModernContextMenuAsync(CancellationToken cancellationToken = default);
    Task<(bool Success, string Message)> DisableModernContextMenuAsync(CancellationToken cancellationToken = default);
    (bool Success, string Message) SetClassicContextMenuEnabled(bool enabled);
}

/// <summary>
/// Lightweight offline diagnostic logger with bounded rolling rotation.
/// </summary>
public interface IDiagnosticLogger
{
    string LogDirectory { get; }
    bool IsEnabled { get; set; }
    void LogInfo(string category, string message);
    void LogWarning(string category, string message);
    void LogError(string category, string message, Exception? exception = null);
}
