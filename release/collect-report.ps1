param([string]$GameRoot, [switch]$NoOpen)

# 무엇을 넣고 빼는지의 기준은 CONTRIBUTING.md "진단 번들". 넣는 파일을 늘리면 그 표도 같이 고친다.

$ErrorActionPreference = 'Stop'
$pluginDir = $PSScriptRoot
if (-not $GameRoot) { $GameRoot = [IO.Path]::GetFullPath((Join-Path $pluginDir '..\..\..')) }
$bepinex = Join-Path $GameRoot 'BepInEx'
if (-not (Test-Path -LiteralPath (Join-Path $bepinex 'plugins'))) {
    throw "BepInEx 폴더를 찾지 못했습니다: $bepinex`n이 파일은 BepInEx\plugins\AstralPartyBattleLog 안에서 실행해야 합니다."
}

$IgnoreCase = [Text.RegularExpressions.RegexOptions]::IgnoreCase
$userProfile = [Environment]::GetFolderPath('UserProfile')
$profilePatterns = @()
if ($userProfile) {
    foreach ($p in @($userProfile, $userProfile.Replace('\', '/'))) { $profilePatterns += [regex]::Escape($p) }
}

# 화면 프로브 기록과 hex 덤프는 개발용이라 경로 가림만으로 식별 정보를 다 걸러낼 수 없다.
$dropPatterns = @('\[probe\]', '\[candidates\]', ':Astral Party Battle Log\] dump ')

# 서버가 보내는 playerId는 영구 계정 uid다.
$idPatterns = @(
    @{ Pattern = '(?<=\b(?:pid|killer)=)-?\d+'; Replace = '#' },
    @{ Pattern = '(?<=unhandled marker from )-?\d+'; Replace = '#' },
    @{ Pattern = ' = -?\d+ \((Monsters|Players) 목록\)'; Replace = ' = # ($1 목록)' }
)

$stats = [ordered]@{}

function Read-Shared([string]$path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Protect-Text([string]$name, [string]$text, [int]$tailLines = 0, [bool]$isLog = $true) {
    $lines = $text -split "`r?`n"
    if ($tailLines -gt 0 -and $lines.Count -gt $tailLines) { $lines = $lines[($lines.Count - $tailLines)..($lines.Count - 1)] }
    $dropped = 0
    $masked = 0
    $out = New-Object Text.StringBuilder
    foreach ($line in $lines) {
        $drop = $false
        if ($isLog) { foreach ($p in $dropPatterns) { if ($line -match $p) { $drop = $true; break } } }
        if ($drop) { $dropped++; continue }
        $before = $line
        foreach ($p in $profilePatterns) { $line = [regex]::Replace($line, $p, '%USERPROFILE%', $IgnoreCase) }
        $line = [regex]::Replace($line, '([a-z]:[\\/]+Users[\\/]+)[^\\/\r\n"'']+', '$1<user>', $IgnoreCase)
        foreach ($p in $idPatterns) { $line = [regex]::Replace($line, $p.Pattern, $p.Replace) }
        if ($line -ne $before) { $masked++ }
        [void]$out.AppendLine($line)
    }
    $stats[$name] = "줄 $($lines.Count), 뺀 줄 $dropped, 가린 줄 $masked"
    return $out.ToString()
}

function Get-ChangedSettings([string]$cfg) {
    $changed = @()
    $section = ''
    $default = $null
    foreach ($line in ($cfg -split "`r?`n")) {
        if ($line -match '^\[(.+)\]$') { $section = $Matches[1]; $default = $null; continue }
        if ($line -match '^# Default value:\s?(.*)$') { $default = $Matches[1]; continue }
        if ($line -match '^([^#=][^=]*?)\s*=\s?(.*)$') {
            if ($null -ne $default -and $Matches[2] -ne $default) { $changed += "$section.$($Matches[1]) = $($Matches[2]) (기본값 $default)" }
            $default = $null
        }
    }
    return $changed
}

$entries = [ordered]@{ 'report.txt' = '' }
$missing = @()

$logs = @(Get-ChildItem -LiteralPath $bepinex -Filter 'LogOutput*.log' -File -ErrorAction SilentlyContinue)
if ($logs.Count -eq 0) { $missing += 'BepInEx/LogOutput.log' }
foreach ($log in $logs) { $entries[$log.Name] = Protect-Text $log.Name (Read-Shared $log.FullName) }

$cfgPath = Join-Path $bepinex 'config\astralparty.battlelog.cfg'
$changed = @()
if (Test-Path -LiteralPath $cfgPath) {
    $cfg = Read-Shared $cfgPath
    $changed = Get-ChangedSettings $cfg
    $entries['astralparty.battlelog.cfg'] = Protect-Text 'astralparty.battlelog.cfg' $cfg 0 $false
} else { $missing += 'BepInEx/config/astralparty.battlelog.cfg' }

# 파일 로그는 판이 바뀔 때 비워지므로 보통 마지막 한 판이다. 그래도 길면 뒷부분만 넣는다.
$battleLog = Join-Path $pluginDir 'battle-log.txt'
if (Test-Path -LiteralPath $battleLog) {
    $entries['battle-log.txt'] = Protect-Text 'battle-log.txt' (Read-Shared $battleLog) 5000
} else { $missing += 'battle-log.txt' }

$dll = Join-Path $pluginDir 'AstralPartyBattleLog.dll'
$version = if (Test-Path -LiteralPath $dll) { (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion } else { '(DLL 없음)' }
$bepinexVersion = '(알 수 없음)'
if ($logs.Count -gt 0 -and $entries[$logs[0].Name] -match 'BepInEx (\d[^\s\-]*(?:-[^\s]+)?)') { $bepinexVersion = $Matches[1] }
$namesPath = Join-Path $pluginDir 'names.tsv'
$names = if (Test-Path -LiteralPath $namesPath) { "$(@(Get-Content -LiteralPath $namesPath -Encoding UTF8).Count)줄" } else { '없음' }
$running = [bool](Get-Process -Name 'AstralParty_INT' -ErrorAction SilentlyContinue)

$report = New-Object Text.StringBuilder
[void]$report.AppendLine('Astral Party Battle Log 진단 번들')
[void]$report.AppendLine("생성: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
[void]$report.AppendLine("플러그인: $version")
[void]$report.AppendLine("BepInEx: $bepinexVersion")
[void]$report.AppendLine("게임 실행 중: $(if ($running) { '예' } else { '아니오' })")
[void]$report.AppendLine("names.tsv: $names")
[void]$report.AppendLine()
[void]$report.AppendLine('기본값에서 바꾼 설정:')
if ($changed.Count -eq 0) { [void]$report.AppendLine('  (없음)') } else { foreach ($c in $changed) { [void]$report.AppendLine("  $c") } }
[void]$report.AppendLine()
[void]$report.AppendLine('넣은 파일:')
foreach ($k in $stats.Keys) { [void]$report.AppendLine("  $k — $($stats[$k])") }
foreach ($m in $missing) { [void]$report.AppendLine("  $m — 찾지 못함") }
[void]$report.AppendLine()
[void]$report.AppendLine('넣지 않은 파일:')
[void]$report.AppendLine('  roster-cache.tsv — 계정 uid 해시를 되돌릴 수 있음')
[void]$report.AppendLine('  replay/ — 게임 진행 시각 기록')
[void]$report.AppendLine('  names.tsv 본문 — 게임 텍스트')
[void]$report.AppendLine('  Unity Player.log — 게임 자체 로그라 어떤 정보가 있는지 보장할 수 없음')
[void]$report.AppendLine()
[void]$report.AppendLine('가린 것: 사용자 폴더 경로, 계정 uid(pid·killer 값, 참가자 줄의 id). 뺀 것: [probe]·[candidates] 줄, hex 덤프 줄.')
[void]$report.AppendLine('올리기 전에 ZIP을 열어 한 번 훑어봐 주세요.')
$entries['report.txt'] = $report.ToString()

$outDir = Join-Path $pluginDir 'reports'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$zip = Join-Path $outDir "AstralPartyBattleLog-report-$(Get-Date -Format 'yyyyMMdd-HHmmss').zip"

Add-Type -AssemblyName System.IO.Compression
$utf8 = New-Object Text.UTF8Encoding($false)
$output = [IO.File]::Open($zip, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try {
    $archive = New-Object IO.Compression.ZipArchive($output, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($k in $entries.Keys) {
            $writer = New-Object IO.StreamWriter($archive.CreateEntry($k).Open(), $utf8)
            try { $writer.Write($entries[$k]) } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $output.Dispose() }

Write-Host "진단 번들을 만들었습니다: $zip"
Write-Host '올리기 전에 ZIP을 열어 내용을 확인해 주세요.'
if (-not $NoOpen) { Start-Process explorer.exe "/select,`"$zip`"" }
