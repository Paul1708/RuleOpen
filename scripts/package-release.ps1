param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$CertPfxPath = "",
    [string]$CertPassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($found -and (Test-Path $found)) {
            return $found
        }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "MSBuild.exe could not be located."
}

function Find-WindowsSdkBinDir {
    $kitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $kitRoot)) {
        throw "Windows 10/11 SDK bin directory not found under $kitRoot."
    }
    $dirs = Get-ChildItem $kitRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName "x64\makeappx.exe")) } |
        Sort-Object Name -Descending
    if (-not $dirs) {
        throw "Could not find makeappx.exe inside Windows Kits."
    }
    return (Join-Path $dirs[0].FullName "x64")
}

function Invoke-SignToolIfConfigured {
    param([string]$FilePath, [string]$SdkBin)
    if ([string]::IsNullOrWhiteSpace($CertPfxPath)) {
        return
    }
    $signtool = Join-Path $SdkBin "signtool.exe"
    $args = @("sign", "/fd", "SHA256", "/f", $CertPfxPath)
    if (-not [string]::IsNullOrWhiteSpace($CertPassword)) {
        $args += @("/p", $CertPassword)
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $args += @("/tr", $TimestampUrl, "/td", "SHA256")
    }
    $args += $FilePath
    & $signtool @args
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $FilePath"
    }
}

$msbuild = Find-MSBuild
$sdkBin = Find-WindowsSdkBinDir
$makeappx = Join-Path $sdkBin "makeappx.exe"
$makepri = Join-Path $sdkBin "makepri.exe"
if (-not (Test-Path $makepri)) {
    throw "makepri.exe could not be found in $sdkBin."
}

$artifactsDir = Join-Path $root "artifacts"
$stageDir = Join-Path $artifactsDir "staging"
$releaseDir = Join-Path $artifactsDir "release"
$msixStageDir = Join-Path $artifactsDir "msix-stage"
$storeStageDir = Join-Path $artifactsDir "store-stage"

Remove-Item $stageDir, $msixStageDir, $storeStageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageDir, $releaseDir, "$msixStageDir\Assets", "$storeStageDir\Assets" -Force | Out-Null

Write-Host "==> 1/6 Building native C++ Explorer context menu extension..."
$shellProj = Join-Path $root "src\RuleOpen.ShellExtension\RuleOpen.ShellExtension.vcxproj"
& $msbuild $shellProj /p:Configuration=$Configuration /p:Platform=x64 /m /nologo
if ($LASTEXITCODE -ne 0) { throw "Native C++ shell extension build failed." }

$shellDll = Join-Path $root "src\RuleOpen.ShellExtension\bin\x64\$Configuration\RuleOpen.ShellExtension.dll"
Invoke-SignToolIfConfigured -FilePath $shellDll -SdkBin $sdkBin

Write-Host "==> 2/6 Packing Sparse MSIX shell integration package..."
$stagedManifest = Join-Path $msixStageDir "AppxManifest.xml"
Copy-Item (Join-Path $root "src\RuleOpen.ShellExtension\AppxManifest.xml") $stagedManifest
[xml]$manifestXml = Get-Content -LiteralPath $stagedManifest -Raw
$manifestXml.Package.Identity.SetAttribute("Version", "$Version.0")
if (-not [string]::IsNullOrWhiteSpace($CertPfxPath)) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertPfxPath, $CertPassword)
    try {
        $manifestXml.Package.Identity.SetAttribute("Publisher", $cert.Subject)
    }
    finally {
        $cert.Dispose()
    }
}
$manifestXml.Save($stagedManifest)
Copy-Item (Join-Path $root "src\RuleOpen.ShellExtension\Assets\*.png") "$msixStageDir\Assets\"
$msixOut = Join-Path $releaseDir "RuleOpen-$Version-ShellPackage.msix"
& $makeappx pack /o /nv /d $msixStageDir /p $msixOut
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed." }
Invoke-SignToolIfConfigured -FilePath $msixOut -SdkBin $sdkBin

Write-Host "==> 3/6 Publishing self-contained RuleOpen.Desktop & RuleOpen.Dispatcher..."
$desktopProj = Join-Path $root "src\RuleOpen.Desktop\RuleOpen.Desktop.csproj"
dotnet publish $desktopProj `
    -c $Configuration `
    -r win-x64 `
    -p:Platform=x64 `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false `
    --self-contained true `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "RuleOpen.Desktop publish failed." }

$dispatcherProj = Join-Path $root "src\RuleOpen.Dispatcher\RuleOpen.Dispatcher.csproj"
dotnet publish $dispatcherProj `
    -c $Configuration `
    -r win-x64 `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    --self-contained true `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "RuleOpen.Dispatcher publish failed." }

Copy-Item $shellDll (Join-Path $stageDir "RuleOpen.ShellExtension.dll") -Force
Copy-Item $msixOut (Join-Path $stageDir "RuleOpen.ShellPackage.msix") -Force

Invoke-SignToolIfConfigured -FilePath (Join-Path $stageDir "RuleOpen.Desktop.exe") -SdkBin $sdkBin
Invoke-SignToolIfConfigured -FilePath (Join-Path $stageDir "RuleOpen.Dispatcher.exe") -SdkBin $sdkBin

Write-Host "==> 4/6 Creating portable ZIP archive..."
$zipOut = Join-Path $releaseDir "RuleOpen-$Version-win-x64-portable.zip"
if (Test-Path $zipOut) { Remove-Item $zipOut -Force }
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipOut -CompressionLevel Optimal

Write-Host "==> 5/6 Generating ICE-compliant WiX file manifest & building per-user MSI..."
$wixDir = Join-Path $root "src\RuleOpen.Packaging"
$generatedWxs = Join-Path $wixDir "StagedFiles.g.wxs"

function Write-StagedWxs {
    param([string]$StageRoot, [string]$OutPath)

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')

    $script:dirIndex = 0
    $script:fileIndex = 0
    $script:compIds = [System.Collections.Generic.List[string]]::new()

    function New-DeterministicGuid {
        param([string]$Seed)
        $md5 = [System.Security.Cryptography.MD5]::Create()
        try {
            $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Seed))
            return ([guid]::new($bytes)).ToString("D").ToUpperInvariant()
        }
        finally {
            $md5.Dispose()
        }
    }

    function Emit-DirContents {
        param([string]$DirPath, [string]$DirId, [string]$RelDir, [string]$Indent, [bool]$IsRoot)

        if (-not $IsRoot) {
            $script:dirIndex++
            $rmCompId = "DirCleanup_$($script:dirIndex)"
            $rmGuid = New-DeterministicGuid "DIR:$RelDir"
            $script:compIds.Add($rmCompId)
            [void]$sb.AppendLine("$Indent<Component Id=""$rmCompId"" Guid=""$rmGuid"">")
            [void]$sb.AppendLine("$Indent  <RemoveFolder Id=""Rm_$DirId"" On=""uninstall"" />")
            [void]$sb.AppendLine("$Indent  <RegistryValue Root=""HKCU"" Key=""Software\RuleOpen\Installer\Dirs"" Name=""$DirId"" Type=""integer"" Value=""1"" KeyPath=""yes"" />")
            [void]$sb.AppendLine("$Indent</Component>")
        }

        foreach ($file in (Get-ChildItem -LiteralPath $DirPath -File | Sort-Object Name)) {
            $script:fileIndex++
            $fId = "AppFile_$($script:fileIndex)"
            $cId = "AppComp_$($script:fileIndex)"
            $relFile = if ([string]::IsNullOrEmpty($RelDir)) { $file.Name } else { "$RelDir\$($file.Name)" }
            $fGuid = New-DeterministicGuid "FILE:$relFile"
            $script:compIds.Add($cId)
            $escapedSrc = [System.Security.SecurityElement]::Escape($file.FullName)
            [void]$sb.AppendLine("$Indent<Component Id=""$cId"" Guid=""$fGuid"">")
            [void]$sb.AppendLine("$Indent  <File Id=""$fId"" Source=""$escapedSrc"" KeyPath=""no"" />")
            [void]$sb.AppendLine("$Indent  <RegistryValue Root=""HKCU"" Key=""Software\RuleOpen\Installer\Files"" Name=""$fId"" Type=""integer"" Value=""1"" KeyPath=""yes"" />")
            [void]$sb.AppendLine("$Indent</Component>")
        }

        foreach ($sub in (Get-ChildItem -LiteralPath $DirPath -Directory | Sort-Object Name)) {
            $script:dirIndex++
            $subId = "SubDir_$($script:dirIndex)"
            $subRel = if ([string]::IsNullOrEmpty($RelDir)) { $sub.Name } else { "$RelDir\$($sub.Name)" }
            $escapedName = [System.Security.SecurityElement]::Escape($sub.Name)
            [void]$sb.AppendLine("$Indent<Directory Id=""$subId"" Name=""$escapedName"">")
            Emit-DirContents -DirPath $sub.FullName -DirId $subId -RelDir $subRel -Indent ("$Indent  ") -IsRoot $false
            [void]$sb.AppendLine("$Indent</Directory>")
        }
    }

    Emit-DirContents -DirPath $StageRoot -DirId "INSTALLFOLDER" -RelDir "" -Indent "      " -IsRoot $true

    [void]$sb.AppendLine('    </DirectoryRef>')
    [void]$sb.AppendLine('  </Fragment>')
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <ComponentGroup Id="StagedApplicationFiles">')
    foreach ($cid in $script:compIds) {
        [void]$sb.AppendLine("      <ComponentRef Id=""$cid"" />")
    }
    [void]$sb.AppendLine('    </ComponentGroup>')
    [void]$sb.AppendLine('  </Fragment>')
    [void]$sb.AppendLine('</Wix>')

    [System.IO.File]::WriteAllText($OutPath, $sb.ToString(), [System.Text.Encoding]::UTF8)
}

Write-StagedWxs -StageRoot $stageDir -OutPath $generatedWxs
$wixProj = Join-Path $wixDir "RuleOpen.Packaging.wixproj"
try {
    dotnet build $wixProj -c $Configuration -p:Platform=x64 -p:StageDir=$stageDir -p:ProductVersion=$Version.0
    if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed." }
}
finally {
    Remove-Item $generatedWxs -Force -ErrorAction SilentlyContinue
}

$builtMsi = Get-ChildItem (Join-Path $root "src\RuleOpen.Packaging\bin") -Recurse -Filter "*.msi" |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $builtMsi) { throw "Built MSI artifact not found." }

$msiOut = Join-Path $releaseDir "RuleOpen-$Version-win-x64.msi"
Copy-Item $builtMsi.FullName $msiOut -Force
Invoke-SignToolIfConfigured -FilePath $msiOut -SdkBin $sdkBin

Write-Host "==> 6/6 Packing Microsoft Store Full MSIX package..."
Copy-Item "$stageDir\*" $storeStageDir -Recurse -Force
Remove-Item (Join-Path $storeStageDir "RuleOpen.ShellPackage.msix") -Force -ErrorAction SilentlyContinue

$storeManifest = Join-Path $storeStageDir "AppxManifest.xml"
Copy-Item (Join-Path $root "src\RuleOpen.ShellExtension\AppxManifest.Store.xml") $storeManifest -Force
[xml]$storeXml = Get-Content -LiteralPath $storeManifest -Raw
$storeXml.Package.Identity.SetAttribute("Version", "$Version.0")
if (-not [string]::IsNullOrWhiteSpace($CertPfxPath)) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertPfxPath, $CertPassword)
    try {
        $storeXml.Package.Identity.SetAttribute("Publisher", $cert.Subject)
    }
    finally {
        $cert.Dispose()
    }
}
$storeXml.Save($storeManifest)
Copy-Item (Join-Path $root "src\RuleOpen.ShellExtension\Assets\*.png") "$storeStageDir\Assets\" -Force

$desktopDir = Join-Path $root "src\RuleOpen.Desktop"
$priConfig = Get-ChildItem (Join-Path $desktopDir "obj") -Recurse -Filter "priconfig.xml" |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $priConfig) {
    throw "Could not locate priconfig.xml under $desktopDir\obj."
}

Push-Location $desktopDir
try {
    & $makepri new /pr $desktopDir /cf $priConfig.FullName /in "PaulBrinkmann.RuleOpen" /of (Join-Path $storeStageDir "resources.pri") /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makepri failed for Store package." }
}
finally {
    Pop-Location
}

$storeMsixOut = Join-Path $releaseDir "RuleOpen-$Version-win-x64-Store.msix"
& $makeappx pack /o /d $storeStageDir /p $storeMsixOut
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed for Store package." }
Invoke-SignToolIfConfigured -FilePath $storeMsixOut -SdkBin $sdkBin

Remove-Item $stageDir, $msixStageDir, $storeStageDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Release artifacts ready in ${releaseDir}:"
Get-ChildItem $releaseDir | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    Write-Host ("  {0,-40} {1,10:N0} bytes  SHA256: {2}" -f $_.Name, $_.Length, $hash)
}
