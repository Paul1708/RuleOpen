using RuleOpen.Core.Models;

namespace RuleOpen.Desktop.Services;

public enum StartupIntent
{
    Normal = 0,
    CreateRuleFromFile = 1,
    CreateRuleFromFolder = 2,
    TestFile = 3,
    FallbackRecovery = 4
}

public sealed record AppStartupArguments
{
    public StartupIntent Intent { get; init; } = StartupIntent.Normal;
    public string? TargetPath { get; init; }
    public string? Reason { get; init; }
    public string? RuleId { get; init; }
    public string? InitialPage { get; init; }
    public ThemePreference? ThemeOverride { get; init; }

    public static AppStartupArguments Parse(string[] args)
    {
        StartupIntent intent = StartupIntent.Normal;
        string? targetPath = null;
        string? reason = null;
        string? ruleId = null;
        string? initialPage = null;
        ThemePreference? themeOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--create-rule", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                intent = StartupIntent.CreateRuleFromFile;
                targetPath = args[++i];
            }
            else if (string.Equals(arg, "--create-rule-folder", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                intent = StartupIntent.CreateRuleFromFolder;
                targetPath = args[++i];
            }
            else if (string.Equals(arg, "--test-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                intent = StartupIntent.TestFile;
                targetPath = args[++i];
            }
            else if (string.Equals(arg, "--fallback-picker", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                intent = StartupIntent.FallbackRecovery;
                targetPath = args[++i];
            }
            else if (string.Equals(arg, "--reason", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                reason = args[++i];
            }
            else if (string.Equals(arg, "--rule-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                ruleId = args[++i];
            }
            else if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                initialPage = args[++i];
            }
            else if (string.Equals(arg, "--theme", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (Enum.TryParse<ThemePreference>(args[++i], ignoreCase: true, out ThemePreference parsedTheme))
                {
                    themeOverride = parsedTheme;
                }
            }
        }

        return new AppStartupArguments
        {
            Intent = intent,
            TargetPath = targetPath,
            Reason = reason,
            RuleId = ruleId,
            InitialPage = initialPage,
            ThemeOverride = themeOverride
        };
    }
}
