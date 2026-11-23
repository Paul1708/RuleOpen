string line = string.Join(" | ", args);

string? captureDir = Environment.GetEnvironmentVariable("RULEOPEN_TEST_CAPTURE_DIR");
if (!string.IsNullOrWhiteSpace(captureDir))
{
    Directory.CreateDirectory(captureDir);
    string path = Path.Combine(captureDir, $"{Environment.ProcessId}_{Guid.NewGuid():N}.txt");
    File.WriteAllText(path, line + Environment.NewLine);
}

string? captureFile = Environment.GetEnvironmentVariable("RULEOPEN_TEST_CAPTURE_FILE");
if (!string.IsNullOrWhiteSpace(captureFile))
{
    File.AppendAllText(captureFile, line + Environment.NewLine);
}
