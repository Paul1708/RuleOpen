using System.Diagnostics;
using Microsoft.Win32;
using RuleOpen.Core.Models;
using RuleOpen.Core.Services;
using RuleOpen.Windows.Applications;
using RuleOpen.Windows.Associations;
using Xunit;

namespace RuleOpen.Integration.Tests;

public sealed class WindowsShellDiscoveryIntegrationTests
{
    [Fact]
    public async Task DiscoveryService_ReturnsHandlersForTxt_WithValidPngIconAndNoSelfReference()
    {
        ShellApplicationDiscoveryService discovery = new();

        IReadOnlyList<DiscoveredApplication> apps = await discovery.GetHandlersForExtensionAsync(".txt");

        Assert.NotEmpty(apps);
        Assert.DoesNotContain(apps, a =>
            PerUserFileAssociationService.IsRuleOpenProgId(a.ProgId) ||
            (a.ExecutablePath?.Contains("RuleOpen.", StringComparison.OrdinalIgnoreCase) ?? false));

        DiscoveredApplication? withIcon = apps.FirstOrDefault(a => a.IconPngBytes is { Length: > 8 });
        Assert.NotNull(withIcon);

        // Verify valid PNG magic header: 89 50 4E 47 0D 0A 1A 0A
        byte[] png = withIcon!.IconPngBytes!;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
    }

    [Fact]
    public void FileAssociationService_RegistersAndCleansUpDisposableTestExtension()
    {
        const string disposableExt = ".ruleopen-test-ext";
        string expectedProgId = PerUserFileAssociationService.GetProgIdForExtension(disposableExt);
        bool hadRegisteredAppBefore;
        using (RegistryKey? regApps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: false))
        {
            hadRegisteredAppBefore = regApps?.GetValue("RuleOpen") is not null;
        }

        PerUserFileAssociationService service = new();
        RuleOpenConfig config = new()
        {
            Settings = new AppSettings
            {
                ExplorerIconMode = ExplorerIconMode.RuleOpenExtensionBadges
            }
        };
        string? generatedIconPath = null;

        try
        {
            service.RegisterExtensions([disposableExt], config);

            IReadOnlyList<FileAssociationStatus> statuses = service.GetStatusForExtensions([disposableExt], config);
            FileAssociationStatus status = Assert.Single(statuses);
            Assert.True(status.IsRegisteredWithWindows);

            using RegistryKey? fileAssocKey = Registry.CurrentUser.OpenSubKey(@"Software\RuleOpen\Capabilities\FileAssociations", writable: false);
            Assert.Equal(expectedProgId, fileAssocKey?.GetValue(disposableExt) as string);

            using RegistryKey? progIdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{expectedProgId}", writable: false);
            Assert.NotNull(progIdKey);

            using RegistryKey? appIconKey = progIdKey!.OpenSubKey("Application", writable: false);
            Assert.NotNull(appIconKey?.GetValue("ApplicationIcon") as string);

            using RegistryKey? defaultIconKey = progIdKey.OpenSubKey("DefaultIcon", writable: false);
            generatedIconPath = defaultIconKey?.GetValue(null) as string;
            Assert.False(string.IsNullOrWhiteSpace(generatedIconPath));
            Assert.EndsWith(".ico", generatedIconPath!, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(generatedIconPath));

            using RegistryKey? openWithKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{disposableExt}\OpenWithProgids", writable: false);
            Assert.NotNull(openWithKey);
            Assert.NotNull(openWithKey!.GetValue(expectedProgId));
        }
        finally
        {
            service.UnregisterExtension(disposableExt, config);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{disposableExt}", throwOnMissingSubKey: false);

            if (!string.IsNullOrWhiteSpace(generatedIconPath) && File.Exists(generatedIconPath))
            {
                try { File.Delete(generatedIconPath); } catch { }
            }

            if (!hadRegisteredAppBefore)
            {
                service.RemoveAllRegistrations();
            }
        }
    }

    [Fact]
    public void ExtensionBadgeIconGenerator_ProducesValidMultiFrameIco()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RuleOpenBadgeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (string ext in new[] { ".pdf", ".md", ".customext" })
            {
                string? icoPath = ExtensionBadgeIconGenerator.EnsureExtensionIcon(ext, tempDir);
                Assert.NotNull(icoPath);
                Assert.Contains("ro_v3_", Path.GetFileName(icoPath!), StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(icoPath!));

                byte[] bytes = File.ReadAllBytes(icoPath!);
                Assert.True(bytes.Length > 100);

                // ICONDIR: Reserved=0, Type=1 (ICO), Count=10 (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
                Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
                Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
                int count = BitConverter.ToUInt16(bytes, 4);
                Assert.Equal(10, count);

                int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
                byte[] pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
                for (int i = 0; i < count; i++)
                {
                    int entryOffset = 6 + (i * 16);
                    byte expectedDim = expectedSizes[i] >= 256 ? (byte)0 : (byte)expectedSizes[i];
                    Assert.Equal(expectedDim, bytes[entryOffset]);
                    Assert.Equal(expectedDim, bytes[entryOffset + 1]);
                    int imgSize = BitConverter.ToInt32(bytes, entryOffset + 8);
                    int imgOffset = BitConverter.ToInt32(bytes, entryOffset + 12);
                    Assert.True(imgSize > 24);
                    Assert.True(imgOffset + imgSize <= bytes.Length);
                    Assert.Equal(pngSig, bytes[imgOffset..(imgOffset + 8)]);
                }

                byte[]? extracted = ShellIconExtractor.TryExtractFileIconPng(icoPath!, 48);
                Assert.NotNull(extracted);
                Assert.Equal(pngSig, extracted![..8]);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ExtensionBadgeIconGenerator_EnsureWrappedPngIcon_WrapsValidPngIntoIco()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "RuleOpenWrapTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Reuse a valid PNG frame from the v3 generator as source PNG input (index 3 = 32x32).
            byte[] sampleIco = ExtensionBadgeIconGenerator.BuildMultiFrameIconBytes(".pdf");
            int pngSize = BitConverter.ToInt32(sampleIco, 6 + 16 * 3 + 8); // 32x32 frame
            int pngOffset = BitConverter.ToInt32(sampleIco, 6 + 16 * 3 + 12);
            string srcPng = Path.Combine(tempDir, "sample.png");
            File.WriteAllBytes(srcPng, sampleIco[pngOffset..(pngOffset + pngSize)]);

            string? wrappedIco = ExtensionBadgeIconGenerator.EnsureWrappedPngIcon(srcPng, tempDir);
            Assert.NotNull(wrappedIco);
            Assert.True(File.Exists(wrappedIco!));

            byte[] icoBytes = File.ReadAllBytes(wrappedIco!);
            Assert.Equal(0, BitConverter.ToUInt16(icoBytes, 0));
            Assert.Equal(1, BitConverter.ToUInt16(icoBytes, 2));
            Assert.Equal(1, BitConverter.ToUInt16(icoBytes, 4));
            Assert.Equal(32, icoBytes[6]);
            Assert.Equal(32, icoBytes[7]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void FileAssociationService_MatchedRoutedAppMode_SyncsAndCleansUpShellIconHandler()
    {
        const string disposableExt = ".ruleopen-route-test";
        string expectedProgId = PerUserFileAssociationService.GetProgIdForExtension(disposableExt);
        bool hadRegisteredAppBefore;
        bool hadClsidBefore;
        using (RegistryKey? regApps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: false))
        {
            hadRegisteredAppBefore = regApps?.GetValue("RuleOpen") is not null;
        }
        using (RegistryKey? clsidKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{PerUserFileAssociationService.IconHandlerClsid}", writable: false))
        {
            hadClsidBefore = clsidKey is not null;
        }

        string tempSandbox = Path.Combine(Path.GetTempPath(), "RuleOpenRouteCfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempSandbox);
        string? prevEnv = Environment.GetEnvironmentVariable(ConfigPaths.ConfigDirEnvVar);

        PerUserFileAssociationService service = new();
        string notepadExe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        RuleOpenConfig config = new()
        {
            Settings = new AppSettings
            {
                ExplorerIconMode = ExplorerIconMode.MatchedRoutedAppIcon
            },
            Rules =
            [
                new OpeningRule
                {
                    Id = "route-rule-1",
                    Name = "Route Test Rule",
                    Extensions = [disposableExt],
                    FolderPath = @"C:\RuleOpenTestScope\Sub",
                    IncludeSubfolders = true,
                    TargetApplication = new ApplicationTarget
                    {
                        Kind = ApplicationTargetKind.CustomExecutable,
                        ApplicationId = notepadExe,
                        DisplayName = "Notepad",
                        ExecutablePath = notepadExe
                    }
                }
            ]
        };

        try
        {
            Environment.SetEnvironmentVariable(ConfigPaths.ConfigDirEnvVar, tempSandbox);
            service.RegisterExtensions([disposableExt], config);

            using (RegistryKey? defaultIconKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{expectedProgId}\DefaultIcon", writable: false))
            {
                Assert.Equal("%1", defaultIconKey?.GetValue(null) as string);
            }

            using (RegistryKey? shellExKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{expectedProgId}\ShellEx\IconHandler", writable: false))
            {
                Assert.Equal(PerUserFileAssociationService.IconHandlerClsid, shellExKey?.GetValue(null) as string);
            }

            using (RegistryKey? inprocKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{PerUserFileAssociationService.IconHandlerClsid}\InprocServer32", writable: false))
            {
                Assert.NotNull(inprocKey);
                string? dllPath = inprocKey!.GetValue(null) as string;
                Assert.False(string.IsNullOrWhiteSpace(dllPath));
                Assert.True(File.Exists(dllPath!));
            }

            string routesPath = ConfigPaths.GetDefaultIconRoutesFilePath();
            Assert.True(File.Exists(routesPath));
            string routesContent = File.ReadAllText(routesPath);
            Assert.Contains($"R\t{disposableExt}\t", routesContent);
            Assert.Contains($"F\t{disposableExt}\t", routesContent);

            // Switching back to RuleOpenExtensionBadges removes ShellEx\IconHandler and HKCU CLSID cleanly.
            config.Settings.ExplorerIconMode = ExplorerIconMode.RuleOpenExtensionBadges;
            service.RefreshExtensionIcons([disposableExt], config);

            using (RegistryKey? shellExAfter = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{expectedProgId}\ShellEx\IconHandler", writable: false))
            {
                Assert.Null(shellExAfter);
            }

            using (RegistryKey? clsidAfter = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{PerUserFileAssociationService.IconHandlerClsid}", writable: false))
            {
                Assert.Null(clsidAfter);
            }

            Assert.False(File.Exists(routesPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigPaths.ConfigDirEnvVar, prevEnv);
            service.UnregisterExtension(disposableExt, config);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{disposableExt}", throwOnMissingSubKey: false);

            if (!hadClsidBefore)
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{PerUserFileAssociationService.IconHandlerClsid}", throwOnMissingSubKey: false);
            }

            if (!hadRegisteredAppBefore)
            {
                service.RemoveAllRegistrations();
            }

            if (Directory.Exists(tempSandbox))
            {
                try { Directory.Delete(tempSandbox, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void UninstallCleanupHelpers_ParseArgsAndRespectDeleteUserDataFlag()
    {
        (int ui1, bool? del1, _) = RuleOpen.Dispatcher.Program.ParseUninstallArguments(
            ["--uninstall-cleanup", "--ui-level", "5", "--delete-user-data", "0"]);
        Assert.Equal(5, ui1);
        Assert.False(del1);

        (int ui2, bool? del2, _) = RuleOpen.Dispatcher.Program.ParseUninstallArguments(
            ["--uninstall-cleanup", "--ui-level", "2", "--delete-user-data", "1"]);
        Assert.Equal(2, ui2);
        Assert.True(del2);

        (_, bool? delAsk, _) = RuleOpen.Dispatcher.Program.ParseUninstallArguments(
            ["--uninstall-cleanup", "--ui-level", "3", "--delete-user-data", "ask"]);
        Assert.Null(delAsk);

        string sandbox = Path.Combine(Path.GetTempPath(), "RuleOpenUninstallTest_" + Guid.NewGuid().ToString("N"));
        string iconsDir = Path.Combine(sandbox, "Icons");
        string pkgDir = Path.Combine(sandbox, "ShellPackage");
        string cfgFile = Path.Combine(sandbox, "config.json");
        Directory.CreateDirectory(iconsDir);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(cfgFile, """{"schemaVersion":1}""");
        File.WriteAllText(Path.Combine(iconsDir, "test.ico"), "ico");

        try
        {
            bool deletedAll = RuleOpen.Dispatcher.Program.ApplyUserDataAndCacheCleanup(sandbox, deleteAllUserData: false);
            Assert.False(deletedAll);
            Assert.True(File.Exists(cfgFile));
            Assert.False(Directory.Exists(iconsDir));
            Assert.False(Directory.Exists(pkgDir));

            deletedAll = RuleOpen.Dispatcher.Program.ApplyUserDataAndCacheCleanup(sandbox, deleteAllUserData: true);
            Assert.True(deletedAll);
            Assert.False(Directory.Exists(sandbox));
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }
}

public sealed class DispatcherEndToEndTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _configDir;
    private readonly string _captureDir;
    private readonly string _dispatcherExe;
    private readonly string _testTargetExe;

    public DispatcherEndToEndTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), "RuleOpenInteg_" + Guid.NewGuid().ToString("N"));
        _configDir = Path.Combine(_sandboxDir, "Config");
        _captureDir = Path.Combine(_sandboxDir, "Captured");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_captureDir);

        _dispatcherExe = FindBinary("src", "RuleOpen.Dispatcher", "RuleOpen.Dispatcher.exe");
        _testTargetExe = FindBinary("tests", "RuleOpen.TestTarget", "RuleOpen.TestTarget.exe");
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandboxDir))
        {
            try
            {
                Directory.Delete(_sandboxDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Dispatcher_LaunchesSingleUnicodeAndMultiFileRequestsCleanly()
    {
        string uniDir = Path.Combine(_sandboxDir, "Universität", "Übungen 2026");
        string projDir = Path.Combine(_sandboxDir, "Projects");
        Directory.CreateDirectory(uniDir);
        Directory.CreateDirectory(projDir);

        string pdfFile = Path.Combine(uniDir, "Vorlesung 日本語 01.pdf");
        string mdFile = Path.Combine(projDir, "readme notes.md");
        File.WriteAllText(pdfFile, "%PDF-1.4");
        File.WriteAllText(mdFile, "# Hello");

        RuleOpenConfig config = new()
        {
            Rules =
            [
                new OpeningRule
                {
                    Id = "rule-pdf",
                    Name = "Uni PDFs",
                    Extensions = [".pdf"],
                    FolderPath = uniDir,
                    IncludeSubfolders = true,
                    TargetApplication = new ApplicationTarget
                    {
                        Kind = ApplicationTargetKind.CustomExecutable,
                        ApplicationId = _testTargetExe,
                        DisplayName = "Test Target",
                        ExecutablePath = _testTargetExe
                    }
                },
                new OpeningRule
                {
                    Id = "rule-md",
                    Name = "Project Markdown",
                    Extensions = [".md"],
                    FolderPath = projDir,
                    IncludeSubfolders = true,
                    TargetApplication = new ApplicationTarget
                    {
                        Kind = ApplicationTargetKind.CustomExecutable,
                        ApplicationId = _testTargetExe,
                        DisplayName = "Test Target",
                        ExecutablePath = _testTargetExe
                    }
                }
            ]
        };

        JsonConfigurationStore store = new(Path.Combine(_configDir, "config.json"));
        store.Save(config);

        int exitCode = await RunDispatcherAsync(["--", pdfFile, mdFile]);
        Assert.Equal(0, exitCode);

        // Allow both child stub processes brief completion time.
        string[] files = [];
        for (int i = 0; i < 40; i++)
        {
            files = Directory.GetFiles(_captureDir, "*.txt");
            if (files.Length >= 2)
            {
                break;
            }
            await Task.Delay(50);
        }

        string[] lines = files.SelectMany(File.ReadAllLines).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains(pdfFile, lines);
        Assert.Contains(mdFile, lines);
    }

    [Fact]
    public async Task Dispatcher_BlocksRecursiveSelfDispatchAndReportsNonZeroExitCodes()
    {
        string workDir = Path.Combine(_sandboxDir, "Work");
        Directory.CreateDirectory(workDir);
        string sampleFile = Path.Combine(workDir, "loop.pdf");
        string unmatchedFile = Path.Combine(workDir, "unknown.xyz");
        File.WriteAllText(sampleFile, "data");
        File.WriteAllText(unmatchedFile, "data");

        RuleOpenConfig config = new()
        {
            Rules =
            [
                new OpeningRule
                {
                    Id = "self-loop",
                    Name = "Self Loop Rule",
                    Extensions = [".pdf"],
                    FolderPath = workDir,
                    TargetApplication = new ApplicationTarget
                    {
                        Kind = ApplicationTargetKind.CustomExecutable,
                        ApplicationId = _dispatcherExe,
                        DisplayName = "RuleOpen Dispatcher",
                        ExecutablePath = _dispatcherExe
                    }
                }
            ]
        };

        JsonConfigurationStore store = new(Path.Combine(_configDir, "config.json"));
        store.Save(config);

        int loopExit = await RunDispatcherAsync(["--", sampleFile]);
        Assert.Equal(12, loopExit); // DispatchOutcome.RecursiveDispatchBlocked

        int noMatchExit = await RunDispatcherAsync(["--", unmatchedFile]);
        Assert.Equal(10, noMatchExit); // DispatchOutcome.NoRuleAndNoFallback
    }

    private async Task<int> RunDispatcherAsync(IEnumerable<string> args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = _dispatcherExe,
            UseShellExecute = false
        };
        psi.Environment[ConfigPaths.ConfigDirEnvVar] = _configDir;
        psi.Environment["RULEOPEN_DISPATCHER_NO_UI"] = "1";
        psi.Environment["RULEOPEN_TEST_CAPTURE_DIR"] = _captureDir;

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dispatcher.");
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    private static string FindBinary(string rootFolder, string projectName, string exeName)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidateRoot = Path.Combine(dir.FullName, rootFolder, projectName, "bin");
            if (Directory.Exists(candidateRoot))
            {
                string? found = Directory.EnumerateFiles(candidateRoot, exeName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {exeName} under {rootFolder}/{projectName}/bin.");
    }
}
