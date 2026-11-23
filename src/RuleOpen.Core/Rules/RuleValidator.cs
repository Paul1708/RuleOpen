using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;

namespace RuleOpen.Core.Rules;

/// <summary>
/// Validates rule definitions before saving to configuration.
/// </summary>
public static class RuleValidator
{
    public static ValidationResult Validate(OpeningRule rule, IFileSystem? fileSystem = null)
    {
        ValidationResult result = new();

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            result.Errors.Add("Enter a name for this rule.");
        }
        else if (rule.Name.Trim().Length > 120)
        {
            result.Errors.Add("Rule name must be 120 characters or fewer.");
        }

        IReadOnlyList<string> normalizedExtensions = ExtensionNormalizer.NormalizeMany(rule.Extensions);
        if (normalizedExtensions.Count == 0)
        {
            result.Errors.Add("Specify at least one valid file extension (for example, .pdf).");
        }
        else if (normalizedExtensions.Count != rule.Extensions.Count)
        {
            result.Errors.Add("One or more file extensions are invalid or duplicated.");
        }

        if (string.IsNullOrWhiteSpace(rule.FolderPath))
        {
            result.Errors.Add("Select a folder where this rule applies.");
        }
        else if (!PathNormalizer.TryNormalizeFolderPath(rule.FolderPath, out string normalizedFolder))
        {
            result.Errors.Add("Folder path must be an absolute Windows folder path without wildcards.");
        }
        else if (fileSystem is not null && !fileSystem.DirectoryExists(normalizedFolder))
        {
            result.Warnings.Add($"The folder \"{normalizedFolder}\" does not currently exist.");
        }

        if (rule.TargetApplication is null ||
            string.IsNullOrWhiteSpace(rule.TargetApplication.ApplicationId) ||
            string.IsNullOrWhiteSpace(rule.TargetApplication.DisplayName))
        {
            result.Errors.Add("Select an application to open matching files.");
        }
        else if (IsRuleOpenSelfReference(rule.TargetApplication))
        {
            result.Errors.Add("RuleOpen cannot target itself as an opening application.");
        }
        else if (rule.TargetApplication.Kind == ApplicationTargetKind.CustomExecutable)
        {
            string? exe = rule.TargetApplication.ExecutablePath ?? rule.TargetApplication.ApplicationId;
            if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathFullyQualified(exe))
            {
                result.Errors.Add("Custom executable path must be a full path.");
            }
            else if (fileSystem is not null && !fileSystem.FileExists(exe))
            {
                result.Warnings.Add($"The target executable \"{exe}\" was not found on disk.");
            }
        }

        if (rule.Priority is < 0 or > 10000)
        {
            result.Errors.Add("Priority must be between 0 and 10,000.");
        }

        return result;
    }

    private static bool IsRuleOpenSelfReference(ApplicationTarget target)
    {
        return string.Equals(target.ProgId, "RuleOpen.File", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(target.ApplicationId, "RuleOpen.File", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(target.ExecutablePath) &&
                (target.ExecutablePath.EndsWith("RuleOpen.Dispatcher.exe", StringComparison.OrdinalIgnoreCase) ||
                 target.ExecutablePath.EndsWith("RuleOpen.Desktop.exe", StringComparison.OrdinalIgnoreCase)));
    }
}
