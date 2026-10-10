#requires -Version 5.1
# XinSpect one-command release pipeline.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\release.ps1 -Version 2.54 `
#     -Title "..." -BodyFile notes.md [-Paths NewFile1.cs,NewFile2.cs] [-SkipTests] [-DryRun]
#
# Before running: bump the version in the usual six places (csproj, AboutView,
# three README badges/links/footnote/history lines) + ChangelogCatalog latest entry,
# and set Tests/TestSuiteBaseline.cs to the new method count. This script then does
# the mechanical part end to end with gates, so the manual steps that historically
# drifted (README sync, tag push, byte-count docs commit) cannot be forgotten.
#
# Gates: every step aborts on the first failure with RELEASE-FAIL; nothing is
# published unless the full suite is green, and the post-release verifier must say OK.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,      # bare, e.g. 2.54
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$BodyFile,
    [string[]]$Paths = @(),
    [switch]$SkipTests,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
function Fail([string]$m) { Write-Host "RELEASE-FAIL: $m" -ForegroundColor Red; exit 1 }
function Ok([string]$m)   { Write-Host "RELEASE-OK: $m" -ForegroundColor Green }

function ReadUtf8($path) {
    $b = [IO.File]::ReadAllBytes($path)
    $bom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
    $t = [Text.Encoding]::UTF8.GetString($b)
    if ($bom) { $t = $t.Substring(1) }
    , @($t, $bom)
}
function WriteUtf8($path, $text, $bom) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($bom)))
}

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    if ($Version -notmatch '^\d+\.\d+$') { Fail "Version must be like 2.54, got '$Version'" }
    if (-not (Test-Path $BodyFile)) { Fail "BodyFile not found: $BodyFile" }

    # ---------- preflight: version declared everywhere and nowhere else ----------
    Write-Host '==> preflight (version sync)'

    $cl = (ReadUtf8 'Nav\ChangelogCatalog.cs')[0]
    $m = [regex]::Match($cl, 'Version = "(\d+\.\d+)"')
    if (-not $m.Success) { Fail 'ChangelogCatalog: cannot find latest Version entry' }
    if ($m.Groups[1].Value -ne $Version) { Fail "ChangelogCatalog latest = $($m.Groups[1].Value), expected $Version" }

    $proj = (ReadUtf8 'XinSpect.csproj')[0]
    if ($proj -notmatch "<Version>$Version</Version>") { Fail "XinSpect.csproj <Version> != $Version" }

    $about = (ReadUtf8 'Views\AboutView.xaml')[0]
    if ($about -notmatch "版本 $Version ・") { Fail "AboutView.xaml version line != $Version" }

    $base = (ReadUtf8 'Tests\TestSuiteBaseline.cs')[0]
    $N = [regex]::Match($base, 'ProjectTests = (\d+)').Groups[1].Value
    if (-not $N) { Fail 'TestSuiteBaseline.ProjectTests not found' }

    $zhFiles = @('README.md', 'README.zh-CN.md')
    foreach ($f in ($zhFiles + @('README.en.md'))) {
        $t = (ReadUtf8 $f)[0]
        $stray = [regex]::Matches($t, '/releases/(?:tag|download)/v(\d+\.\d+)') |
            ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne $Version } | Select-Object -First 1
        if ($stray) { Fail "${f}: download link still points to v$stray" }
        $vb = [regex]::Match($t, 'badge/version-(\d+\.\d+)-')
        if ($vb.Groups[1].Value -ne $Version) { Fail "${f}: version badge = $($vb.Groups[1].Value)" }
        if ((ReadUtf8 $f)[0] -notmatch "tests-$N%20passed") { Fail "${f}: tests badge != baseline $N" }
    }
    foreach ($f in $zhFiles) {
        $t = (ReadUtf8 $f)[0]
        if ($f -eq 'README.md' -and $t -notmatch "本版（v$Version）") { Fail "$f`: footnote version != v$Version" }
        if ($f -eq 'README.zh-CN.md' -and $t -notmatch "本版（v$Version）") { Fail "$f`: footnote version != v$Version" }
        $first = [regex]::Match($t, '(?m)^- \*\*v(\d+\.\d+)')
        if ($first.Groups[1].Value -ne $Version) { Fail "$f`: history top entry = v$($first.Groups[1].Value)" }
    }
    Ok "preflight: $Version everywhere, baseline $N"

    # ---------- full suite ----------
    if ($SkipTests) {
        Write-Host '!!! SKIPPING TEST SUITE (-SkipTests) - only acceptable right after a -DryRun of the same version' -ForegroundColor Yellow
    } else {
        Write-Host '==> full test suite'
        if (Test-Path 'Tests\TestResults\rel.trx') { Remove-Item 'Tests\TestResults\rel.trx' -Force }
        # 直出 console（呼叫端自行重定向）——PS 5.1 把大量輸出經管道餵 Out-File 會卡死，
        # 這裡刻意不用管道，維持與手動發版相同的執行方式。
        & dotnet test Tests\XinSpect.Tests.csproj -c Debug --nologo `
            -p:BaseOutputPath="obj/_rel_$Version/" --logger "trx;LogFileName=rel.trx"
        if ($LASTEXITCODE -ne 0) { Fail "test suite exit code $LASTEXITCODE" }
        $trxPath = 'Tests\TestResults\rel.trx'
        if (-not (Test-Path $trxPath)) { Fail 'trx result file not found at Tests\TestResults\rel.trx' }
        $trxText = [IO.File]::ReadAllText((Resolve-Path $trxPath))
        if ($trxText -notmatch '<ResultSummary outcome="Completed"') { Fail 'suite outcome != Completed' }
        $cn = [regex]::Match($trxText, '<Counters[^>]*/>')
        function Cnt([string]$name) { [int][regex]::Match($cn.Value, "$name=`"(\d+)`"").Groups[1].Value }
        $failed = Cnt 'failed'; $errs = Cnt 'error'; $notExec = Cnt 'notExecuted'; $total = Cnt 'total'
        if ($failed -gt 0 -or $errs -gt 0 -or $notExec -gt 0) {
            Fail "suite counters failed=$failed error=$errs notExecuted=$notExec"
        }
        if ($total -lt [int]$N) { Fail "executed total $total < baseline $N" }
        Ok "suite green: executed $total, baseline $N"
    }

    if ($DryRun) { Ok 'dry run stops here (no commit/publish/upload)'; exit 0 }

    # ---------- commit + tag + push ----------
    Write-Host '==> commit / tag / push'
    # -File 呼叫模式下逗號不會拆成數組——這裡自己拆（教訓：v2.54 首發漏了所有新檔）
    $Paths = @($Paths | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    git add -u | Out-Null
    foreach ($p in $Paths) {
        if (-not (Test-Path $p)) { Fail "Paths entry not found: $p" }
        git add -- $p | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "git add failed for $p" }
    }
    $staged = git diff --cached --name-only
    if (-not $staged) { Fail 'nothing staged - working tree clean but nothing to commit?' }
    Write-Host ("staged: " + ($staged -join ', '))
    git commit -m "$Title ＋ 版號 $Version" | Out-Null
    git tag "v$Version"
    git -c http.version=HTTP/1.1 push origin main | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'push main failed' }
    git -c http.version=HTTP/1.1 push origin "v$Version" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "push tag v$Version failed" }
    $remoteTag = git -c http.version=HTTP/1.1 ls-remote --tags origin "refs/tags/v$Version"
    if (-not $remoteTag) { Fail 'remote tag missing after push (lightweight tags are NOT pushed by --follow-tags)' }
    $remoteMain = (git -c http.version=HTTP/1.1 ls-remote origin refs/heads/main) -split '\s' | Select-Object -First 1
    $localHead = git rev-parse HEAD
    if ($remoteMain -ne $localHead) { Fail "remote main ($remoteMain) != local HEAD ($localHead)" }
    Ok "pushed main=$($localHead.Substring(0,7)) + tag v$Version on remote"

    # ---------- publish ----------
    Write-Host '==> dotnet publish (single file)'
    & dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:BaseOutputPath="obj/_pub_$Version/" -o "publish-$Version" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'publish failed' }
    $exe = "publish-$Version\XinSpect.exe"
    if (-not (Test-Path $exe)) { Fail "publish output missing: $exe" }
    $bytes = (Get-Item $exe).Length
    $disp = $bytes.ToString('#,0')
    Ok "published $exe = $disp bytes"

    # ---------- byte-count docs commit (zh pair declares exact bytes) ----------
    $changed = @()
    foreach ($f in $zhFiles) {
        $r = ReadUtf8 $f; $t = $r[0]
        $new = [regex]::Replace($t, "(XinSpect\.exe\]\([^)]*\) \| )[\d,]+(?= bytes)", ('${1}' + $disp))
        if ($new -ne $t) { WriteUtf8 $f $new $r[1]; $changed += $f; Write-Host "$f`: byte count -> $disp" }
    }
    if ($changed.Count -gt 0) {
        git add -- @($changed) | Out-Null
        git commit -m "docs：README 主程式位元組數改為 v$Version 實測值（$disp）" | Out-Null
        git -c http.version=HTTP/1.1 push origin main | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'push docs commit failed' }
        Ok 'docs byte-count commit pushed'
    } else {
        Write-Host 'byte counts already current - no docs commit needed'
    }

    # ---------- release + assets ----------
    Write-Host '==> create/update Release and upload three assets'
    $bridge = 'publish-2.44-bridge\BlueSquadronBridge.exe'
    if (-not (Test-Path $bridge)) { Fail "bridge asset source missing: $bridge" }
    & python Tools\publish_release.py --tag "v$Version" --title $Title --body-file $BodyFile `
        --asset "XinSpect.exe=$exe" `
        --asset "BlueSquadronBridge.exe=$bridge" `
        --asset "XinSpectDeploy.exe=Installer\XinSpectDeploy.exe"
    if ($LASTEXITCODE -ne 0) { Fail 'asset upload failed' }

    # ---------- post-release verifier ----------
    Write-Host '==> verify-release.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify-release.ps1
    if ($LASTEXITCODE -ne 0) { Fail 'verify-release.ps1 exited non-zero' }

    Ok "v$Version released end to end"
}
finally {
    Pop-Location
}
