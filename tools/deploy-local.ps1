<#
.SYNOPSIS
  Updates the locally installed OpenReo (per-user installation from the installer) with the
  current build – no need to run the installer again during development.

  1. publishes a self-contained Release build (artifacts\publish-local)
  2. closes a running OpenReo gracefully (settings are saved) and waits for it
  3. copies the files over the installation and restarts OpenReo if it was running
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

# installation folder from the uninstall registry entry (AppId of installer\OpenReo.iss)
$key = Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall" -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -like "{6A7F3C2E-5B1D-4E8A-9C3F-0D2B7E4A1F60}*" } | Select-Object -First 1
if (-not $key) { throw "OpenReo is not installed (run the installer once)." }
$install = (Get-ItemProperty $key.PSPath).InstallLocation.TrimEnd('\')
$version = ([xml](Get-Content (Join-Path $root "src\OpenReo.App\OpenReo.App.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$publish = Join-Path $root "artifacts\publish-local"
Write-Host "==> Publishing OpenReo $version" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\OpenReo.App\OpenReo.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:Version=$version -p:DebugType=none -p:SatelliteResourceLanguages=de%3Ben -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$running = @(Get-Process OpenReo -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith("$install\", [StringComparison]::OrdinalIgnoreCase) })
$restartArgs = $null
if ($running.Count -gt 0) {
    Write-Host "==> Closing running OpenReo (settings are saved)" -ForegroundColor Cyan
    foreach ($p in $running) {
        [void]$p.CloseMainWindow()
        if (-not $p.WaitForExit(20000)) { throw "OpenReo did not close – please close it manually and run again." }
    }
    $restartArgs = ""
}

Write-Host "==> Updating $install" -ForegroundColor Cyan
robocopy $publish $install /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying failed (robocopy exit $LASTEXITCODE)" }
Set-ItemProperty $key.PSPath -Name DisplayVersion -Value $version

if ($null -ne $restartArgs) {
    Start-Process (Join-Path $install "OpenReo.exe")
    Write-Host "==> OpenReo restarted" -ForegroundColor Green
}
Write-Host "==> Installed version $version updated." -ForegroundColor Green
exit 0
