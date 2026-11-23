using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;
using RuleOpen.Core.Services;
using Xunit;

namespace RuleOpen.Core.Tests;

public sealed class NormalizationAndValidationTests
{
    [Fact]
    public void ExtensionNormalizer_ParsesVariousFormatsCleanly()
    {
        (IReadOnlyList<string> exts, IReadOnlyList<string> invalid) =
            ExtensionNormalizer.ParseList(".MD, *.markdown; txt  PDF .md");

        Assert.Empty(invalid);
        Assert.Equal([".md", ".markdown", ".txt", ".pdf"], exts);
    }

    [Theory]
    [InlineData("*.")]
    [InlineData("a/b")]
    [InlineData("tar.gz")]
    public void ExtensionNormalizer_RejectsInvalidTokens(string raw)
    {
        Assert.False(ExtensionNormalizer.TryNormalize(raw, out _));
    }

    [Fact]
    public void RuleValidator_RejectsSelfTargetingRules()
    {
        OpeningRule rule = new()
        {
            Name = "Loop",
            Extensions = [".pdf"],
            FolderPath = @"D:\Work",
            TargetApplication = new ApplicationTarget
            {
                Kind = ApplicationTargetKind.CustomExecutable,
                ApplicationId = @"C:\Apps\RuleOpen.Dispatcher.exe",
                DisplayName = "RuleOpen Dispatcher",
                ExecutablePath = @"C:\Apps\RuleOpen.Dispatcher.exe"
            }
        };

        ValidationResult validation = RuleValidator.Validate(rule);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("cannot target itself", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuleValidator_WarnsOnMissingFolderWithoutBlockingSave()
    {
        FakeFileSystem fs = new();
        OpeningRule rule = new()
        {
            Name = "Future Folder",
            Extensions = [".pdf"],
            FolderPath = @"D:\Future",
            TargetApplication = new ApplicationTarget
            {
                Kind = ApplicationTargetKind.RegisteredHandler,
                ApplicationId = "MSEdgePDF",
                DisplayName = "Microsoft Edge"
            }
        };

        ValidationResult validation = RuleValidator.Validate(rule, fs);
        Assert.True(validation.IsValid);
        Assert.Single(validation.Warnings);
    }
}

public sealed class ConfigurationAndDispatchTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationAndDispatchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RuleOpenCoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void JsonConfigurationStore_RecoversFromCorruptedFileWithBackup()
    {
        string path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, "{ broken json !!!");

        JsonConfigurationStore store = new(path);
        RuleOpenConfig loaded = store.Load();

        Assert.NotNull(store.LastLoadWarning);
        Assert.Empty(loaded.Rules);
        Assert.NotEmpty(Directory.GetFiles(_tempDir, "config.corrupt.*.json"));
    }

    [Fact]
    public async Task JsonConfigurationStore_SupportsConcurrentReadsAndWritesAtomically()
    {
        string path = Path.Combine(_tempDir, "config.json");
        JsonConfigurationStore store = new(path);

        Task[] tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
            {
                RuleOpenConfig cfg = new()
                {
                    Rules =
                    [
                        new OpeningRule
                        {
                            Id = $"rule-{i}",
                            Name = $"Rule {i} Ünicode",
                            Extensions = [".pdf"],
                            FolderPath = @"D:\Work",
                            TargetApplication = new ApplicationTarget
                            {
                                ApplicationId = "Edge",
                                DisplayName = "Microsoft Edge"
                            }
                        }
                    ]
                };
                store.Save(cfg);
            }
            else
            {
                RuleOpenConfig cfg = store.Load();
                Assert.NotNull(cfg);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        RuleOpenConfig finalConfig = store.Load();
        Assert.Single(finalConfig.Rules);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp.*"));
    }

    [Fact]
    public void JsonConfigurationStore_RoundTripsExplorerIconModeAndDefaultsLegacyJson()
    {
        string path = Path.Combine(_tempDir, "icon-mode-config.json");
        JsonConfigurationStore store = new(path);

        RuleOpenConfig saved = new()
        {
            Settings = new AppSettings
            {
                ExplorerIconMode = ExplorerIconMode.RuleOpenExtensionBadges
            }
        };
        store.Save(saved);

        RuleOpenConfig reloaded = store.Load();
        Assert.Equal(ExplorerIconMode.RuleOpenExtensionBadges, reloaded.Settings.ExplorerIconMode);

        // Legacy config JSON without explorerIconMode defaults to MatchedRoutedAppIcon.
        string legacyPath = Path.Combine(_tempDir, "legacy-config.json");
        File.WriteAllText(legacyPath, """{"schemaVersion":1,"settings":{"theme":"Dark"}}""");
        RuleOpenConfig legacyLoaded = new JsonConfigurationStore(legacyPath).Load();
        Assert.Equal(ExplorerIconMode.MatchedRoutedAppIcon, legacyLoaded.Settings.ExplorerIconMode);
    }

    [Fact]
    public async Task DispatchCoordinator_UsesExtensionFallbackThenGlobalFallback()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\Other\readme.txt");
        fs.Files.Add(@"D:\Other\image.png");

        RuleOpenConfig config = new()
        {
            ExtensionFallbacks =
            {
                [".txt"] = new ApplicationTarget { ApplicationId = "notepad", DisplayName = "Notepad" }
            },
            GlobalFallback = new ApplicationTarget { ApplicationId = "fallback", DisplayName = "Fallback Viewer" }
        };

        string cfgPath = Path.Combine(_tempDir, "config.json");
        JsonConfigurationStore store = new(cfgPath);
        store.Save(config);

        FakeLauncher launcher = new();
        DispatchCoordinator coordinator = new(store, new RuleEngine(fs), launcher, new RollingFileLogger(_tempDir));

        DispatchResult txtResult = await coordinator.DispatchAsync(@"D:\Other\readme.txt");
        DispatchResult pngResult = await coordinator.DispatchAsync(@"D:\Other\image.png");

        Assert.Equal(DispatchOutcome.LaunchedByExtensionFallback, txtResult.Outcome);
        Assert.Equal("Notepad", txtResult.SelectedTarget?.DisplayName);

        Assert.Equal(DispatchOutcome.LaunchedByGlobalFallback, pngResult.Outcome);
        Assert.Equal("Fallback Viewer", pngResult.SelectedTarget?.DisplayName);
    }

    [Fact]
    public async Task DispatchCoordinator_ReturnsApplicationMissingWithoutSilentFallback()
    {
        FakeFileSystem fs = new();
        fs.Files.Add(@"D:\University\paper.pdf");

        RuleOpenConfig config = new()
        {
            Rules =
            [
                new OpeningRule
                {
                    Id = "r1",
                    Name = "Uni PDFs",
                    Extensions = [".pdf"],
                    FolderPath = @"D:\University",
                    TargetApplication = new ApplicationTarget
                    {
                        Kind = ApplicationTargetKind.CustomExecutable,
                        ApplicationId = @"C:\Missing\Acrobat.exe",
                        DisplayName = "Missing Acrobat",
                        ExecutablePath = @"C:\Missing\Acrobat.exe"
                    }
                }
            ],
            GlobalFallback = new ApplicationTarget { ApplicationId = "Edge", DisplayName = "Edge" }
        };

        string cfgPath = Path.Combine(_tempDir, "config.json");
        JsonConfigurationStore store = new(cfgPath);
        store.Save(config);

        FakeLauncher launcher = new() { MissingTargetId = @"C:\Missing\Acrobat.exe" };
        DispatchCoordinator coordinator = new(store, new RuleEngine(fs), launcher, new RollingFileLogger(_tempDir));

        DispatchResult result = await coordinator.DispatchAsync(@"D:\University\paper.pdf");

        Assert.Equal(DispatchOutcome.ApplicationMissing, result.Outcome);
        Assert.DoesNotContain(launcher.LaunchedTargets, t => t.ApplicationId == "Edge");
    }

    private sealed class FakeLauncher : IApplicationLauncher
    {
        public string? MissingTargetId { get; set; }
        public List<ApplicationTarget> LaunchedTargets { get; } = [];

        public bool IsSelfTarget(ApplicationTarget target) =>
            string.Equals(target.ApplicationId, "RuleOpen.File", StringComparison.OrdinalIgnoreCase);

        public Task<LaunchResult> LaunchAsync(
            ApplicationTarget target,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(target.ApplicationId, MissingTargetId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(LaunchResult.Fail(LaunchFailureReason.ApplicationNotFound, "Target app missing."));
            }

            LaunchedTargets.Add(target);
            return Task.FromResult(LaunchResult.Ok("Launched in fake launcher."));
        }
    }
}
