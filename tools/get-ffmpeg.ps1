<#
.SYNOPSIS
  Downloads the FFmpeg 8.1 LGPL shared build (BtbN/FFmpeg-Builds) into third_party\ffmpeg.
  FFmpeg.AutoGen 8.1.0 expects exactly these major library versions (avcodec-62, avformat-62, avutil-60 ...).
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root "third_party\ffmpeg"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip"
$zip = Join-Path $env:TEMP "openreo-ffmpeg.zip"
$extract = Join-Path $env:TEMP "openreo-ffmpeg"

Write-Host "Downloading $url"
Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive $zip $extract
$inner = Get-ChildItem $extract -Directory | Select-Object -First 1
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
Move-Item $inner.FullName $target
Remove-Item $zip -Force
Write-Host "FFmpeg installed to $target"
Get-ChildItem (Join-Path $target "bin") -Filter *.dll | ForEach-Object { "  " + $_.Name }
