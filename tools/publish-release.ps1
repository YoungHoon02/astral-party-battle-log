param(
    [switch]$Draft,
    [switch]$Prerelease,
    # 릴리스는 나갔는데 알림 인덱스 게시만 실패했을 때, 인덱스만 다시 올린다.
    [switch]$IndexOnly,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'YoungHoon02/astral-party-battle-log'

# 업데이트 알림(Net/ReleaseCheck.cs)이 raw CDN에서 읽는 인덱스를 distribution 브랜치에 올린다.
# 게시된 자산을 직접 받아 해시를 재므로 인덱스가 실제 배포본과 어긋날 수 없다. 자산이
# 받아지지 않으면 올리지 않는다 — 알림을 보고 받으러 간 이용자가 404를 보게 된다.
function Publish-ReleaseIndex([string]$Tag, [string]$Version) {
    $name = "AstralPartyBattleLog-$Tag.zip"
    $url = "https://github.com/$repo/releases/download/$Tag/$name"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("battlelog-index-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $prevIndex = $env:GIT_INDEX_FILE
    try {
        $asset = Join-Path $tmp $name
        for ($try = 1; ; $try++) {
            try { Invoke-WebRequest -Uri $url -OutFile $asset -UseBasicParsing -TimeoutSec 120; break }
            catch { if ($try -ge 3) { throw "릴리스 자산을 받을 수 없습니다($url): $_" }; Start-Sleep -Seconds 5 }
        }
        $index = [ordered]@{
            schemaVersion = 1
            version       = $Version
            downloadUrl   = $url
            sha256        = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
            size          = (Get-Item -LiteralPath $asset).Length
        }
        $json = Join-Path $tmp 'release-index.json'
        # BOM이 붙으면 System.Text.Json이 파싱에 실패해 알림이 조용히 끊긴다.
        [IO.File]::WriteAllText($json, ($index | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

        # 작업 트리와 main을 건드리지 않고 이 파일 하나짜리 커밋을 만든다.
        $blob = (& git -C $root hash-object -w -- $json).Trim()
        $env:GIT_INDEX_FILE = Join-Path $tmp 'index'
        & git -C $root update-index --add --cacheinfo "100644,$blob,release-index.json"
        $tree = (& git -C $root write-tree).Trim()
        $env:GIT_INDEX_FILE = $prevIndex
        if ($LASTEXITCODE -ne 0 -or -not $tree) { throw '인덱스 트리를 만들지 못했습니다.' }

        $remoteRef = & git -C $root ls-remote origin refs/heads/distribution
        if ($LASTEXITCODE -ne 0) { throw 'origin의 distribution 브랜치를 확인하지 못했습니다.' }
        $commitArgs = @('commit-tree', $tree, '-m', "Release $Tag")
        if ($remoteRef) {
            & git -C $root fetch --quiet origin distribution
            if ($LASTEXITCODE -ne 0) { throw 'distribution 브랜치를 가져오지 못했습니다.' }
            $commitArgs += @('-p', (& git -C $root rev-parse FETCH_HEAD).Trim())
        }
        $commit = (& git -C $root @commitArgs).Trim()
        & git -C $root push origin "${commit}:refs/heads/distribution"
        if ($LASTEXITCODE -ne 0) { throw 'distribution 브랜치 푸시에 실패했습니다.' }
        Write-Output "업데이트 알림 인덱스 게시: $Tag (sha256 $($index.sha256), $($index.size) bytes)"
        Write-Output '(raw CDN은 5분 캐시라 알림은 최대 5분 뒤부터 뜹니다)'
    }
    finally {
        $env:GIT_INDEX_FILE = $prevIndex
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'gh CLI가 없습니다. winget install --id GitHub.cli 로 설치한 뒤 gh auth login 하세요.'
}

$project = Join-Path $root 'AstralPartyBattleLog.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)
$version = [string]$projectXml.Project.PropertyGroup.Version
$tag = "v$version"

if ($IndexOnly) {
    $state = (& gh release view $tag --repo $repo --json isDraft,isPrerelease | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw "릴리스 $tag 를 찾을 수 없습니다." }
    if ($state.isDraft -or $state.isPrerelease) { throw '초안·사전 릴리스는 알림 인덱스에 올리지 않습니다.' }
    Publish-ReleaseIndex $tag $version
    return
}

$branch = (& git -C $root rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "main이 아닙니다: $branch" }
if (& git -C $root status --porcelain) { throw '커밋되지 않은 변경이 있습니다.' }

& git -C $root fetch --quiet --tags origin main
if ($LASTEXITCODE -ne 0) { throw 'origin에서 가져오지 못했습니다.' }
if (& git -C $root tag --list $tag) { throw "이미 있는 태그입니다: $tag" }

# 태그와 ZIP은 원격 main에 없는 커밋을 가리키면 안 된다. 받은 사람이 소스를
# 대조할 수 없는 릴리스가 된다.
$head = (& git -C $root rev-parse HEAD).Trim()
$remote = (& git -C $root rev-parse origin/main).Trim()
if ($head -ne $remote) {
    & git -C $root log --oneline "origin/main..HEAD"
    throw "origin/main과 다릅니다. 먼저 밀어 주세요: git push origin main"
}

# 릴리스 본문은 README의 버전 섹션이 원본이다. 커밋 메시지(개발자 관점)와
# 릴리스 노트(사용자 관점)는 층위가 달라 기계 변환할 수 없다.
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Encoding UTF8
$start = -1
for ($i = 0; $i -lt $readme.Count; $i++) {
    if ($readme[$i].TrimEnd() -eq "## $tag") { $start = $i + 1; break }
}
if ($start -lt 0) {
    $prev = (& git -C $root tag --list 'v*' --sort=-v:refname | Select-Object -First 1)
    Write-Warning "README.md에 '## $tag' 섹션이 없습니다. 아래 커밋을 사용자 관점으로 옮겨 적으세요."
    & git -C $root log "$prev..HEAD" --oneline --no-merges
    throw "README.md에 '## $tag' 섹션을 먼저 추가하세요."
}
$body = @()
for ($i = $start; $i -lt $readme.Count; $i++) {
    if ($readme[$i] -match '^## ') { break }
    $body += $readme[$i]
}
$notes = "# $tag`n`n" + (($body -join "`n").Trim()) + "`n"
if ($notes.Trim() -eq "# $tag") { throw "'## $tag' 섹션이 비어 있습니다." }

$zip = Join-Path $root "bin/$Configuration/AstralPartyBattleLog-$tag.zip"
if (Test-Path -LiteralPath $zip) { throw "이전 패키지가 남아 있습니다. 지우고 다시 실행하세요: $zip" }
& (Join-Path $PSScriptRoot 'build-release.ps1') -Configuration $Configuration | Out-Null
if (-not (Test-Path -LiteralPath $zip)) { throw "패키지를 찾을 수 없습니다: $zip" }

Write-Output "--- $tag 릴리스 노트 ---"
Write-Output $notes
Write-Output "--- 패키지: $zip"
if ((Read-Host '이대로 게시할까요? (y/N)') -ne 'y') { throw '중단했습니다.' }

# BOM이 붙으면 릴리스 본문 첫 글자로 새어 나간다 (v0.1.8에서 실제로 발생).
$notesFile = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($notesFile, $notes, (New-Object Text.UTF8Encoding $false))

& git -C $root tag $tag
& git -C $root push origin $tag
if ($LASTEXITCODE -ne 0) {
    & git -C $root tag -d $tag | Out-Null
    throw '태그 푸시에 실패했습니다.'
}

$ghArgs = @('release', 'create', $tag, $zip,
    '--title', $tag, '--notes-file', $notesFile,
    '--target', 'main', '--repo', $repo)
if ($Prerelease) { $ghArgs += '--prerelease' }
if ($Draft) { $ghArgs += '--draft' }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    # gh는 릴리스 생성과 자산 업로드를 따로 처리하므로 실패해도 릴리스가 남아 있을 수 있다.
    # 없다고 확인된 경우에만 태그를 되돌린다 — 인증·네트워크로 확인에 실패한 것을
    # "없음"으로 읽으면 멀쩡한 릴리스의 태그를 지운다.
    # (PS 5.1은 네이티브 명령의 stderr 리다이렉트를 오류로 승격시키므로 Stop을 잠시 푼다.)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $view = (& gh release view $tag --repo $repo 2>&1 | Out-String)
    $viewCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEap

    if ($viewCode -eq 0) {
        Write-Warning '릴리스가 남아 있습니다. 태그는 그대로 두었습니다. 상태를 확인하고 이어서 하세요:'
        Write-Warning "  gh release view $tag --repo $repo --web"
        Write-Warning "  gh release upload $tag '$zip' --repo $repo --clobber    # 자산만 빠졌을 때"
        Write-Warning "  gh release delete $tag --repo $repo --cleanup-tag       # 처음부터 다시 할 때"
        throw '릴리스가 일부만 만들어졌습니다.'
    }
    if ($view -notmatch 'release not found|HTTP 404') {
        Write-Warning '릴리스 상태를 확인하지 못했습니다(인증·네트워크). 태그는 그대로 두었습니다:'
        Write-Warning ($view.Trim())
        Write-Warning "  gh auth status"
        Write-Warning "  gh release view $tag --repo $repo    # 없으면: git push --delete origin $tag"
        throw '릴리스 상태를 확인할 수 없습니다.'
    }
    # 태그만 남으면 다음 실행이 중복 태그 검사에 막혀 손으로 지워야 한다.
    & git -C $root push --delete origin $tag | Out-Null
    & git -C $root tag -d $tag | Out-Null
    throw '릴리스 게시에 실패했습니다. 태그는 되돌렸습니다.'
}
Remove-Item -LiteralPath $notesFile -Force

if ($Draft -or $Prerelease) {
    Write-Output '초안·사전 릴리스라 업데이트 알림 인덱스는 올리지 않았습니다.'
    return
}
try { Publish-ReleaseIndex $tag $version }
catch {
    Write-Warning "릴리스는 게시됐지만 알림 인덱스를 올리지 못했습니다: $_"
    Write-Warning '  .\tools\publish-release.ps1 -IndexOnly'
    throw
}
