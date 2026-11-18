<#
.SYNOPSIS
  Checks that every German UI text has an English translation.

  Collects all keys used via Lang.T("…") / Lang.F("…") in C#, {l:T '…'} in XAML and the German
  option labels of the camera settings, and compares them with Translations.English
  (src\OpenReo.Core\Localization\Translations.cs).

.EXAMPLE
  .\tools\check-translations.ps1            # exit code = number of missing translations
  .\tools\check-translations.ps1 -ListKeys  # print all keys (for writing translations)
#>
param([switch]$ListKeys)

$ErrorActionPreference = "Stop"
$src = Join-Path (Split-Path $PSScriptRoot -Parent) "src"
$keys = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)

function Unescape([string]$s) { $s.Replace('\"', '"').Replace('\n', "`n").Replace('\\', '\') }

foreach ($f in Get-ChildItem $src -Recurse -Include *.cs, *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Name -ne 'Translations.cs' }) {
    $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    foreach ($m in [regex]::Matches($text, 'Lang\.[TF]\(\s*"((?:[^"\\]|\\.)*)"')) { [void]$keys.Add((Unescape $m.Groups[1].Value)) }
    foreach ($m in [regex]::Matches($text, "\{l:T '([^']*)'\}")) { [void]$keys.Add([Net.WebUtility]::HtmlDecode($m.Groups[1].Value)) }
    # German labels in option dictionaries (translated when displayed)
    if ($f.Name -in 'SettingsCatalog.cs', 'ScheduleGrid.cs') {
        foreach ($m in [regex]::Matches($text, '\["[^"]+"\]\s*=\s*"((?:[^"\\]|\\.)*)"')) { [void]$keys.Add((Unescape $m.Groups[1].Value)) }
    }
}

if ($ListKeys) { $keys; return }

$tr = [IO.File]::ReadAllText((Join-Path $src "OpenReo.Core\Localization\Translations.cs"), [Text.Encoding]::UTF8)
$known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($m in [regex]::Matches($tr, '\[\s*"((?:[^"\\]|\\.)*)"\s*\]\s*=')) { [void]$known.Add((Unescape $m.Groups[1].Value)) }

# Every text with letters needs an entry (identical texts are listed explicitly).
$missing = @($keys | Where-Object { -not $known.Contains($_) -and $_ -match '[A-Za-zÄÖÜäöüß]{2}' -and $_ -cnotmatch '^[a-z_]+$' })
if ($missing.Count -eq 0) { Write-Host "OK: $($keys.Count) texts, all translated." -ForegroundColor Green }
else {
    Write-Host "$($missing.Count) text(s) without English translation:" -ForegroundColor Yellow
    $missing | ForEach-Object { "  $_" }
}
exit $missing.Count
