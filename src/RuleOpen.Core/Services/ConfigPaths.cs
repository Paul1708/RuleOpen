using System.Security.Cryptography;
using System.Text;

namespace RuleOpen.Core.Services;

/// <summary>
/// Resolves user-data directories for RuleOpen configuration and diagnostic logs.
/// </summary>
public static class ConfigPaths
{
    public const string ConfigDirEnvVar = "RULEOPEN_CONFIG_DIR";

    public static string GetConfigDirectory()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(ConfigDirEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Path.GetFullPath(overrideDir.Trim());
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "RuleOpen");
    }

    public static string GetDefaultConfigFilePath() => Path.Combine(GetConfigDirectory(), "config.json");

    public static string GetDefaultLogDirectory() => Path.Combine(GetConfigDirectory(), "logs");

    public static string GetDefaultIconsDirectory() => Path.Combine(GetConfigDirectory(), "Icons");

    public static string GetDefaultIconRoutesFilePath() => Path.Combine(GetConfigDirectory(), "icon-routes.tsv");

    internal static string GetMutexNameForPath(string filePath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath.ToUpperInvariant()));
        return @"Local\RuleOpen.Cfg." + Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
