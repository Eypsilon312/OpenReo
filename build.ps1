<#
.SYNOPSIS
  Builds OpenReo and creates the installer (artifacts\OpenReo-Setup-<version>.exe).

.EXAMPLE
  .\build.ps1                 # Release build + self-contained publish + installer
  .\build.ps1 -SkipInstaller  # only publish
#>
param(
    # Defaults to the <Version> in src\OpenReo.App\OpenReo.App.csproj
    [string]$Version,
    [switch]$SkipInstaller,
    # Alternative publish folder (e.g. while OpenReo is running from artifacts\publish)
    [string]$PublishDir
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "src\OpenReo.App\OpenReo.App.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$publish = if ($PublishDir) { [IO.Path]::GetFullPath($PublishDir) } else { Join-Path $artifacts "publish" }

# Never delete a folder OpenReo is currently running from (would leave a half deleted installation).
$publishPrefix = $publish.TrimEnd('\') + '\'
$running = Get-Process OpenReo -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "OpenReo is running from '$publish' (PID $($running.Id -join ', ')). Close OpenReo or use -PublishDir <other folder>."
}

# Prefer a user-local SDK (%USERPROFILE%\.dotnet) if present, otherwise the one on PATH.
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

if (-not (Test-Path (Join-Path $root "third_party\ffmpeg\bin\avcodec-62.dll"))) {
    throw "FFmpeg libraries missing. Run .\tools\get-ffmpeg.ps1 first."
}

Write-Host "==> Publishing OpenReo $Version (self-contained, win-x64)" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish (Join-Path $root "src\OpenReo.App\OpenReo.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -p:SatelliteResourceLanguages=de%3Ben -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if ($SkipInstaller) { Write-Host "Published to $publish"; return }

Write-Host "==> Restoring Inno Setup compiler (NuGet)" -ForegroundColor Cyan
& $dotnet restore (Join-Path $root "installer\InstallerTools.proj") --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Restoring Tools.InnoSetup failed" }
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$iscc = Get-ChildItem (Join-Path $nugetRoot "tools.innosetup") -Recurse -Filter ISCC.exe | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found in Tools.InnoSetup package" }

Write-Host "==> Building installer" -ForegroundColor Cyan
& $iscc.FullName "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$artifacts" /Q (Join-Path $root "installer\OpenReo.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $artifacts "OpenReo-Setup-$Version.exe"
Write-Host "==> Done: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
