param(
    [string]$Configuration = "Release",
    [switch]$UseStaged
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

$coreDll = Get-ChildItem (Join-Path $root "src\RuleOpen.Core\bin") -Recurse -Filter "RuleOpen.Core.dll" |
    Where-Object { $_.FullName -like "*\$Configuration\*" } |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName

if ($UseStaged -and (Test-Path (Join-Path $root "artifacts\staging\RuleOpen.Dispatcher.exe"))) {
    $dispatcherExe = Join-Path $root "artifacts\staging\RuleOpen.Dispatcher.exe"
    $desktopExe = Join-Path $root "artifacts\staging\RuleOpen.Desktop.exe"
}
else {
    $dispatcherExe = Get-ChildItem (Join-Path $root "src\RuleOpen.Dispatcher\bin") -Recurse -Filter "RuleOpen.Dispatcher.exe" |
        Where-Object { $_.FullName -like "*\$Configuration\*" } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName

    $desktopExe = Get-ChildItem (Join-Path $root "src\RuleOpen.Desktop\bin") -Recurse -Filter "RuleOpen.Desktop.exe" |
        Where-Object { $_.FullName -like "*\$Configuration\*" } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
}

$targetExe = Get-ChildItem (Join-Path $root "tests\RuleOpen.TestTarget\bin") -Recurse -Filter "RuleOpen.TestTarget.exe" |
    Where-Object { $_.FullName -like "*\$Configuration\*" } |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName

$benchTmp = Join-Path $env:TEMP ("RuleOpenBench_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $benchTmp -Force | Out-Null

try {
    $cfgDir = Join-Path $benchTmp "cfg"
    $sampleDir = Join-Path $benchTmp "Work"
    $captureDir = Join-Path $benchTmp "captures"
    New-Item -ItemType Directory -Path $cfgDir, $sampleDir, $captureDir -Force | Out-Null
    $samplePdf = Join-Path $sampleDir "lecture.pdf"
    "PDF" | Set-Content $samplePdf -Encoding ASCII

    $cfgJson = @{
        schemaVersion = 1
        rules = @(
            @{
                id = "bench-1"
                name = "Bench PDF"
                enabled = $true
                extensions = @(".pdf")
                folderPath = $sampleDir
                includeSubfolders = $true
                targetApplication = @{
                    kind = "CustomExecutable"
                    applicationId = $targetExe
                    displayName = "Test Target"
                    executablePath = $targetExe
                }
                priority = 100
            }
        )
        settings = @{
            hasCompletedOnboarding = $true
            enableDiagnosticLogging = $false
        }
    } | ConvertTo-Json -Depth 6
    $cfgJson | Set-Content (Join-Path $cfgDir "config.json") -Encoding UTF8

    $harnessProj = Join-Path $benchTmp "BenchHarness.csproj"
    $harnessCs = Join-Path $benchTmp "Program.cs"
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="RuleOpen.Core">
      <HintPath>$coreDll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"@ | Set-Content $harnessProj -Encoding UTF8

    @'
using System.Diagnostics;
using RuleOpen.Core.Interfaces;
using RuleOpen.Core.Models;
using RuleOpen.Core.Rules;

string dispatcherExe = args[0];
string samplePdf = args[1];
string cfgDir = args[2];
string captureDir = args[3];
string desktopExe = args[4];

var engine = new RuleEngine(new StubFs());
foreach (int ruleCount in new[] { 10, 100, 500 })
{
    var config = new RuleOpenConfig();
    for (int i = 0; i < ruleCount; i++)
    {
        config.Rules.Add(new OpeningRule
        {
            Id = $"r-{i:D4}",
            Name = $"Rule {i}",
            Extensions = i % 3 == 0 ? [".pdf"] : [".txt", ".md"],
            FolderPath = i == ruleCount - 1 ? @"D:\Work\ClientA" : $@"D:\Folder{i}\Sub",
            IncludeSubfolders = true,
            Priority = 100 + (i % 50),
            TargetApplication = new ApplicationTarget { ApplicationId = "Edge", DisplayName = "Edge" }
        });
    }

    const string testPath = @"D:\Work\ClientA\2026\report.pdf";
    for (int w = 0; w < 500; w++) _ = engine.Evaluate(testPath, config);

    const int iterations = 10000;
    var sw = Stopwatch.StartNew();
    for (int k = 0; k < iterations; k++) _ = engine.Evaluate(testPath, config);
    sw.Stop();
    double usPerEval = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;
    Console.WriteLine($"RuleEngine ({ruleCount} rules): {usPerEval:F2} us/eval");
}

List<double> dispatcherTimes = [];
List<double> dispatcherMemMb = [];
for (int run = 0; run < 6; run++)
{
    var psi = new ProcessStartInfo
    {
        FileName = dispatcherExe,
        UseShellExecute = false
    };
    psi.Environment["RULEOPEN_CONFIG_DIR"] = cfgDir;
    psi.Environment["RULEOPEN_DISPATCHER_NO_UI"] = "1";
    psi.Environment["RULEOPEN_TEST_CAPTURE_DIR"] = captureDir;
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add(samplePdf);

    var sw = Stopwatch.StartNew();
    using var proc = Process.Start(psi)!;
    long peakBytes = 0;
    while (!proc.HasExited)
    {
        try
        {
            proc.Refresh();
            if (proc.PeakWorkingSet64 > peakBytes) peakBytes = proc.PeakWorkingSet64;
        }
        catch { }
        Thread.Sleep(5);
    }
    proc.WaitForExit();
    sw.Stop();
    try
    {
        proc.Refresh();
        if (proc.PeakWorkingSet64 > peakBytes) peakBytes = proc.PeakWorkingSet64;
    }
    catch { }

    if (proc.ExitCode != 0) throw new InvalidOperationException($"Dispatcher exit {proc.ExitCode}");
    dispatcherTimes.Add(sw.Elapsed.TotalMilliseconds);
    dispatcherMemMb.Add(peakBytes / (1024.0 * 1024.0));
}

double warmMedianMs = dispatcherTimes.Skip(1).OrderBy(x => x).ElementAt(2);
double peakMemMb = dispatcherMemMb.Max();
Console.WriteLine($"Dispatcher end-to-end: cold={dispatcherTimes[0]:F1} ms, warm median={warmMedianMs:F1} ms, peak working set={peakMemMb:F1} MB");

// Measure Desktop UI startup (2 runs: cold/first + warm)
for (int run = 0; run < 2; run++)
{
    var psiUi = new ProcessStartInfo { FileName = desktopExe, UseShellExecute = false };
    psiUi.Environment["RULEOPEN_CONFIG_DIR"] = cfgDir;
    var swUi = Stopwatch.StartNew();
    using var uiProc = Process.Start(psiUi)!;
    uiProc.WaitForInputIdle(10000);
    while (uiProc.MainWindowHandle == IntPtr.Zero && swUi.ElapsedMilliseconds < 10000 && !uiProc.HasExited)
    {
        Thread.Sleep(15);
        uiProc.Refresh();
    }
    swUi.Stop();
    uiProc.Refresh();
    double uiMemMb = uiProc.WorkingSet64 / (1024.0 * 1024.0);
    Console.WriteLine($"RuleOpen.Desktop run {run + 1}: time-to-window={swUi.Elapsed.TotalMilliseconds:F1} ms, working set={uiMemMb:F1} MB");
    if (!uiProc.HasExited)
    {
        uiProc.CloseMainWindow();
        if (!uiProc.WaitForExit(1500)) uiProc.Kill();
    }
}

sealed class StubFs : IFileSystem
{
    public bool FileExists(string path) => true;
    public bool DirectoryExists(string path) => true;
}
'@ | Set-Content $harnessCs -Encoding UTF8

    dotnet run --project $harnessProj -c Release -- $dispatcherExe $samplePdf $cfgDir $captureDir $desktopExe
}
finally {
    Remove-Item $benchTmp -Recurse -Force -ErrorAction SilentlyContinue
}
