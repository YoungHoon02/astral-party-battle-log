param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'AstralPartyBattleLog.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)
$version = [string]$projectXml.Project.PropertyGroup.Version
$plugin = Get-Content -LiteralPath (Join-Path $root 'Plugin.cs') -Raw -Encoding UTF8
if ($plugin -notmatch ('\[BepInPlugin\(Guid,\s*"Astral Party Battle Log",\s*"' + [regex]::Escape($version) + '"\)\]')) {
    throw 'Plugin.cs and project versions differ.'
}

& dotnet build $project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

$dll = Join-Path $root "bin/$Configuration/net6.0/AstralPartyBattleLog.dll"
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "bin/$Configuration" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$zip = Join-Path $OutputDirectory "AstralPartyBattleLog-v$version.zip"
if (Test-Path -LiteralPath $zip) { throw "Package already exists: $zip" }
$files = @(
    @{ Source = $dll; Entry = 'BepInEx/plugins/AstralPartyBattleLog/AstralPartyBattleLog.dll' },
    @{ Source = (Join-Path $root 'release/USER_AGREEMENT.md'); Entry = 'BepInEx/plugins/AstralPartyBattleLog/USER_AGREEMENT.md' },
    @{ Source = (Join-Path $root 'release/HOW_TO_INSTALL.md'); Entry = 'BepInEx/plugins/AstralPartyBattleLog/HOW_TO_INSTALL.md' },
    @{ Source = (Join-Path $root 'LICENSE'); Entry = 'BepInEx/plugins/AstralPartyBattleLog/LICENSE' }
)

Add-Type -AssemblyName System.IO.Compression
$output = [IO.File]::Open($zip, [IO.FileMode]::Create, [IO.FileAccess]::Write)
try {
    $archive = New-Object IO.Compression.ZipArchive($output, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            if (-not (Test-Path -LiteralPath $file.Source)) { throw "Missing package file: $($file.Source)" }
            $entry = $archive.CreateEntry($file.Entry, [IO.Compression.CompressionLevel]::Optimal)
            $source = [IO.File]::OpenRead($file.Source)
            $target = $entry.Open()
            try { $source.CopyTo($target) }
            finally { $target.Dispose(); $source.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $output.Dispose() }

Write-Output $zip
