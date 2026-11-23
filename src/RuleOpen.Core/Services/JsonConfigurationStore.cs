using System.Text.Json;
using System.Text.Json.Serialization;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;

namespace RuleOpen.Core.Services;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RuleOpenConfig))]
internal sealed partial class RuleOpenJsonContext : JsonSerializerContext;

/// <summary>
/// Thread-safe, cross-process atomic JSON configuration store with schema migration
/// and non-destructive corruption recovery.
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore
{
    private readonly IDiagnosticLogger? _logger;
    private readonly string _mutexName;

    public JsonConfigurationStore(string? configFilePath = null, IDiagnosticLogger? logger = null)
    {
        ConfigFilePath = string.IsNullOrWhiteSpace(configFilePath)
            ? ConfigPaths.GetDefaultConfigFilePath()
            : Path.GetFullPath(configFilePath);
        _logger = logger;
        _mutexName = ConfigPaths.GetMutexNameForPath(ConfigFilePath);
    }

    public string ConfigFilePath { get; }
    public string? LastLoadWarning { get; private set; }

    public RuleOpenConfig Load()
    {
        LastLoadWarning = null;

        if (!File.Exists(ConfigFilePath))
        {
            return new RuleOpenConfig();
        }

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using FileStream fs = new(
                    ConfigFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                if (fs.Length == 0)
                {
                    return HandleCorruptedConfig("Configuration file was empty.");
                }

                RuleOpenConfig? parsed = JsonSerializer.Deserialize(fs, RuleOpenJsonContext.Default.RuleOpenConfig);
                if (parsed is null)
                {
                    return HandleCorruptedConfig("Configuration file deserialized to null.");
                }

                return MigrateAndSanitize(parsed);
            }
            catch (JsonException ex)
            {
                return HandleCorruptedConfig($"Malformed JSON in configuration file: {ex.Message}");
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException ex)
            {
                LastLoadWarning = $"Configuration file could not be read: {ex.Message}";
                _logger?.LogError("ConfigStore", LastLoadWarning, ex);
                return new RuleOpenConfig();
            }
        }

        LastLoadWarning = "Configuration file was temporarily locked by another process.";
        _logger?.LogWarning("ConfigStore", LastLoadWarning);
        return new RuleOpenConfig();
    }

    public Task<RuleOpenConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(Load, cancellationToken);
    }

    public void Save(RuleOpenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? directory = Path.GetDirectoryName(ConfigFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RuleOpenConfig toPersist = PrepareForSave(config);
        string tempPath = Path.Combine(
            directory ?? ".",
            $".{Path.GetFileName(ConfigFilePath)}.tmp.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}");

        using Mutex mutex = new(false, _mutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("Timed out waiting for configuration write lock.");
            }

            using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, toPersist, RuleOpenJsonContext.Default.RuleOpenConfig);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(ConfigFilePath))
            {
                try
                {
                    File.Replace(tempPath, ConfigFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                    File.Move(tempPath, ConfigFilePath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, ConfigFilePath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup of temp file.
                }
            }

            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public Task SaveAsync(RuleOpenConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Save(config), cancellationToken);
    }

    private RuleOpenConfig MigrateAndSanitize(RuleOpenConfig loaded)
    {
        if (loaded.SchemaVersion > RuleOpenConfig.CurrentSchemaVersion)
        {
            LastLoadWarning = $"Configuration schema version {loaded.SchemaVersion} is newer than supported version {RuleOpenConfig.CurrentSchemaVersion}.";
            _logger?.LogWarning("ConfigStore", LastLoadWarning);
        }

        if (loaded.SchemaVersion <= 0)
        {
            loaded.SchemaVersion = RuleOpenConfig.CurrentSchemaVersion;
        }

        loaded.Rules ??= [];
        loaded.Settings ??= new AppSettings();
        loaded.Settings.RegisteredExtensions ??= [];

        Dictionary<string, ApplicationTarget> cleanFallbacks = new(StringComparer.OrdinalIgnoreCase);
        if (loaded.ExtensionFallbacks is not null)
        {
            foreach ((string key, ApplicationTarget value) in loaded.ExtensionFallbacks)
            {
                if (value is not null && ExtensionNormalizer.TryNormalize(key, out string normExt))
                {
                    cleanFallbacks[normExt] = value;
                }
            }
        }

        loaded.ExtensionFallbacks = cleanFallbacks;

        foreach (OpeningRule rule in loaded.Rules)
        {
            rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id;
            rule.Extensions = [.. ExtensionNormalizer.NormalizeMany(rule.Extensions)];
            if (PathNormalizer.TryNormalizeFolderPath(rule.FolderPath, out string normFolder))
            {
                rule.FolderPath = normFolder;
            }
            rule.TargetApplication ??= new ApplicationTarget();
        }

        loaded.Settings.RegisteredExtensions = [.. ExtensionNormalizer.NormalizeMany(loaded.Settings.RegisteredExtensions)];
        return loaded;
    }

    private static RuleOpenConfig PrepareForSave(RuleOpenConfig source)
    {
        RuleOpenConfig copy = new()
        {
            SchemaVersion = RuleOpenConfig.CurrentSchemaVersion,
            GlobalFallback = source.GlobalFallback?.Clone(),
            ExtensionFallbacks = new Dictionary<string, ApplicationTarget>(StringComparer.OrdinalIgnoreCase),
            Settings = new AppSettings
            {
                Theme = source.Settings.Theme,
                Language = string.IsNullOrWhiteSpace(source.Settings.Language) ? "en-US" : source.Settings.Language,
                ConflictResolutionMode = source.Settings.ConflictResolutionMode,
                EnableDiagnosticLogging = source.Settings.EnableDiagnosticLogging,
                HasCompletedOnboarding = source.Settings.HasCompletedOnboarding,
                ClassicContextMenuEnabled = source.Settings.ClassicContextMenuEnabled,
                ExplorerIconMode = source.Settings.ExplorerIconMode,
                RegisteredExtensions = [.. ExtensionNormalizer.NormalizeMany(source.Settings.RegisteredExtensions)]
            },
            Rules = []
        };

        foreach ((string ext, ApplicationTarget target) in source.ExtensionFallbacks)
        {
            if (ExtensionNormalizer.TryNormalize(ext, out string normExt))
            {
                copy.ExtensionFallbacks[normExt] = target.Clone();
            }
        }

        foreach (OpeningRule rule in source.Rules)
        {
            OpeningRule cloned = rule.Clone(generateNewId: false);
            cloned.Extensions = [.. ExtensionNormalizer.NormalizeMany(cloned.Extensions)];
            if (PathNormalizer.TryNormalizeFolderPath(cloned.FolderPath, out string normFolder))
            {
                cloned.FolderPath = normFolder;
            }
            copy.Rules.Add(cloned);
        }

        return copy;
    }

    private RuleOpenConfig HandleCorruptedConfig(string reason)
    {
        string backupName = $"config.corrupt.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
        string backupPath = Path.Combine(Path.GetDirectoryName(ConfigFilePath) ?? ".", backupName);

        try
        {
            if (File.Exists(ConfigFilePath))
            {
                File.Copy(ConfigFilePath, backupPath, overwrite: true);
                LastLoadWarning = $"{reason} A backup was preserved at {backupName}.";
            }
            else
            {
                LastLoadWarning = reason;
            }
        }
        catch (Exception ex)
        {
            LastLoadWarning = $"{reason} (Backup failed: {ex.Message})";
        }

        _logger?.LogWarning("ConfigStore", LastLoadWarning);
        return new RuleOpenConfig();
    }
}
