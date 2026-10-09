#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = Split-Path -Parent $PSScriptRoot
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stageRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ('PinCC-build-' + [Guid]::NewGuid().ToString('N'))))
$distRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'dist'))
$requiredTfms = @('net10.0')
$sdkInstallUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'
$desktopRuntimeUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'

function Assert-Within([string] $Path, [string] $Root) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the intended directory: $fullPath"
    }
}

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

function Remove-Candidate([string] $Path) {
    Assert-Within $Path $distRoot
    if (Test-Path -LiteralPath $Path -PathType Leaf) { Remove-Item -LiteralPath $Path -Force }
}

function Assert-DotNetToolchain {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw @"
未找到 dotnet。请安装 .NET 10 SDK（含 Windows Desktop 工作负载）：
  winget install Microsoft.DotNet.SDK.10
  或打开 $sdkInstallUrl
"@
    }

    $sdks = @( & dotnet --list-sdks 2>$null )
    if ($LASTEXITCODE -ne 0 -or $sdks.Count -eq 0) {
        throw "dotnet --list-sdks 失败。请重装 .NET 10 SDK：$sdkInstallUrl"
    }
    $hasNet10Sdk = $false
    foreach ($line in $sdks) {
        if ($line -match '^10\.') { $hasNet10Sdk = $true; break }
    }
    if (-not $hasNet10Sdk) {
        throw @"
需要 .NET 10 SDK 才能构建 PinCC（当前：$(($sdks | Select-Object -First 3) -join '; ')）。
安装：winget install Microsoft.DotNet.SDK.10
  或 $sdkInstallUrl
"@
    }

    $runtimes = @( & dotnet --list-runtimes 2>$null )
    $hasDesktop = $false
    foreach ($line in $runtimes) {
        if ($line -match '^Microsoft\.WindowsDesktop\.App 10\.') { $hasDesktop = $true; break }
    }
    if (-not $hasDesktop) {
        Write-Warning @"
未检测到 .NET 10 Desktop Runtime。构建仍可进行；框架依赖版 dist\PinCC.exe 运行时需要它：
  winget install Microsoft.DotNet.DesktopRuntime.10
  或 $desktopRuntimeUrl
自带运行时的 dist\standalone\PinCC.exe 不依赖本机 Desktop Runtime。
"@
    }

    # Confirm the project TFM is resolvable without pulling NuGet packages.
    $proj = Join-Path $workspace 'src\PinCC.csproj'
    $xml = [xml](Get-Content -LiteralPath $proj -Raw)
    $tfm = [string]$xml.Project.PropertyGroup.TargetFramework
    if ([string]::IsNullOrWhiteSpace($tfm)) { throw "无法读取 $proj 的 TargetFramework" }
    $ok = $false
    foreach ($prefix in $requiredTfms) {
        if ($tfm.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $ok = $true; break }
    }
    if (-not $ok) {
        throw "意外的 TargetFramework='$tfm'；本仓库约定 $($requiredTfms -join '/')。"
    }
    Write-Host "Toolchain OK: SDK 10.x, TFM $tfm$(if ($hasDesktop) { ', Desktop Runtime 10.x' } else { '' })"
}

try {
    Assert-DotNetToolchain
    Invoke-Dotnet -Arguments @('run', '--project', (Join-Path $workspace 'tests'), '-c', 'Release')
    Assert-Within $stageRoot $tempBase
    [IO.Directory]::CreateDirectory($stageRoot) | Out-Null
    Write-Host "Publish staging (kept on failure): $stageRoot"
    $frameworkDir = Join-Path $stageRoot 'framework-dependent'
    $standaloneDir = Join-Path $stageRoot 'standalone'
    Invoke-Dotnet -Arguments @('publish', (Join-Path $workspace 'src'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'false', '-p:PublishSingleFile=true', '-p:DebugType=embedded', '-o', $frameworkDir)
    Invoke-Dotnet -Arguments @('publish', (Join-Path $workspace 'src'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:DebugType=embedded', '-o', $standaloneDir)

    foreach ($directory in @($frameworkDir, $standaloneDir)) {
        $files = @(Get-ChildItem -LiteralPath $directory -File)
        if ($files.Count -ne 1 -or $files[0].Name -ne 'PinCC.exe') {
            throw "Single-file publish produced unexpected files: $directory"
        }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'UiSmoke.ps1') -Executable (Join-Path $directory 'PinCC.exe')
        if ($LASTEXITCODE -ne 0) { throw "Published GUI smoke failed: $directory" }
    }

    # Prepare both candidates and backups before touching either installed file.
    $generation = [Guid]::NewGuid().ToString('N')
    $items = @(
        @{ Source = Join-Path $frameworkDir 'PinCC.exe'; Destination = Join-Path $distRoot 'PinCC.exe'; Label = 'framework-dependent'; Promoted = $false },
        @{ Source = Join-Path $standaloneDir 'PinCC.exe'; Destination = Join-Path $distRoot 'standalone\PinCC.exe'; Label = 'standalone'; Promoted = $false }
    )
    try {
        foreach ($item in $items) {
            Assert-Within $item.Destination $distRoot
            [IO.Directory]::CreateDirectory((Split-Path -Parent $item.Destination)) | Out-Null
            $item.Candidate = Join-Path (Split-Path -Parent $item.Destination) ("PinCC.next-$generation.exe")
            $item.Backup = Join-Path $stageRoot ($item.Label + '-previous.exe')
            Assert-Within $item.Candidate $distRoot
            Assert-Within $item.Backup $stageRoot
            $item.HadOriginal = Test-Path -LiteralPath $item.Destination -PathType Leaf
            if ($item.HadOriginal) { [IO.File]::Copy($item.Destination, $item.Backup, $false) }
            [IO.File]::Copy($item.Source, $item.Candidate, $false)
        }
        foreach ($item in $items) {
            if ($item.HadOriginal) { [IO.File]::Replace($item.Candidate, $item.Destination, [NullString]::Value, $true) }
            else { [IO.File]::Move($item.Candidate, $item.Destination) }
            $item.Promoted = $true
        }
    }
    catch {
        $promotionError = $_
        $rollbackFailures = @()
        foreach ($item in $items) {
            if (-not $item.Promoted) { continue }
            try {
                if ($item.HadOriginal) {
                    [IO.File]::Copy($item.Backup, $item.Candidate, $true)
                    [IO.File]::Replace($item.Candidate, $item.Destination, [NullString]::Value, $true)
                }
                else { Remove-Candidate $item.Destination }
            }
            catch { $rollbackFailures += $_.Exception.Message }
        }
        if ($rollbackFailures.Count -gt 0) {
            throw "Publish promotion failed; rollback also failed: $($rollbackFailures -join ' / '). Backups: $stageRoot"
        }
        throw $promotionError
    }
    finally {
        foreach ($item in $items) {
            if ($item.ContainsKey('Candidate')) { Remove-Candidate $item.Candidate }
        }
    }

    # Both published programs passed their actual WPF lifecycle checks.
    $resolvedStage = (Resolve-Path -LiteralPath $stageRoot).Path
    Assert-Within $resolvedStage $tempBase
    if (-not $resolvedStage.Equals($stageRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup of an unexpected staging path: $resolvedStage"
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    Write-Host 'Built and verified: dist\PinCC.exe and dist\standalone\PinCC.exe'
}
catch {
    Write-Error $_
    exit 1
}
