using RuleOpen.Core.Interfaces;

namespace RuleOpen.Core.Services;

/// <summary>
/// Thread-safe, offline rolling file logger with strict size bounds and minimal PII exposure.
/// </summary>
public sealed class RollingFileLogger : IDiagnosticLogger
{
    private const long MaxFileBytes = 512 * 1024;
    private const int MaxArchivedFiles = 3;
    private readonly Lock _sync = new();
    private readonly string _logFilePath;

    public RollingFileLogger(string? logDirectory = null, string fileName = "ruleopen.log", bool enabled = true)
    {
        LogDirectory = string.IsNullOrWhiteSpace(logDirectory)
            ? ConfigPaths.GetDefaultLogDirectory()
            : Path.GetFullPath(logDirectory);
        _logFilePath = Path.Combine(LogDirectory, fileName);
        IsEnabled = enabled;
    }

    public string LogDirectory { get; }
    public bool IsEnabled { get; set; }

    public void LogInfo(string category, string message) => WriteEntry("INFO", category, message);

    public void LogWarning(string category, string message) => WriteEntry("WARN", category, message);

    public void LogError(string category, string message, Exception? exception = null)
    {
        string combined = exception is null
            ? message
            : $"{message} | {exception.GetType().Name}: {exception.Message}";
        WriteEntry("ERROR", category, combined);
    }

    private void WriteEntry(string level, string category, string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        string line = $"{DateTimeOffset.UtcNow:O} [{level}] [{category}] {message}{Environment.NewLine}";

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(_logFilePath, line);
            }
            catch
            {
                // Diagnostic logging must never break file dispatching or UI operations.
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logFilePath))
        {
            return;
        }

        FileInfo info = new(_logFilePath);
        if (info.Length < MaxFileBytes)
        {
            return;
        }

        for (int i = MaxArchivedFiles; i >= 1; i--)
        {
            string source = i == 1 ? _logFilePath : $"{_logFilePath}.{i - 1}";
            string dest = $"{_logFilePath}.{i}";
            if (File.Exists(dest) && i == MaxArchivedFiles)
            {
                File.Delete(dest);
            }

            if (File.Exists(source))
            {
                File.Move(source, dest, overwrite: true);
            }
        }
    }
}
