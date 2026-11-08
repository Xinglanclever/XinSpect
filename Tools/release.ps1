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

    # ---------- preflight: 每一支會被編進產品的原始碼都要在版控裡 ----------
    # v2.66 踩過一次：`dotnet publish` 從工作樹編譯，未追蹤的 .cs 會被一起吃進 exe，
    # 所以本機看起來一切正常，而 tag 的乾淨 clone 建置直接紅（CS0246）。
    # v3.02 又踩一次（AiUsage.cs 沒進版控，標籤推上去才發現）。
    # 這裡在發佈前就把「磁碟上有、git 沒有」的 .cs 全部擋下來——這是唯一能提前發現的地方。
    Write-Host '==> preflight (untracked sources)'
    $untracked = & git ls-files --others --exclude-standard -- '*.cs' |
        Where-Object { $_ -notmatch '^(work|obj|bin)/' -and $_ -notmatch '/obj/' -and $_ -notmatch '/bin/' -and $_ -notmatch '^publish-' }
    if ($untracked) {
        Fail ("以下原始碼沒有進版控——本機能建置（publish 從工作樹編譯），但乾淨 clone 會 CS0246：`n  " +
              (($untracked | Select-Object -First 20) -join "`n  "))
    }
    Ok 'preflight: every tracked-buildable .cs is in git'

    # ---------- preflight: 安裝器必須現編，不能上傳上一次的成品 ----------
    # Installer/XinSpectDeploy.exe 是二進位檔：改了 Install.cs 而忘了重編不會有任何編譯錯誤，
    # 只會安靜地上傳一個舊安裝器。每次發佈都在這裡重編一次，有改動就一起進發佈提交。
    Write-Host '==> preflight (installer rebuild)'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-installer.ps1')
    if ($LASTEXITCODE -ne 0) { Fail 'installer build failed' }
    $deployChanged = git status --porcelain -- 'Installer/XinSpectDeploy.exe'
    if ($deployChanged) { Write-Host "  note: installer rebuilt; it will be part of the release commit" }
    Ok 'preflight: installer rebuilt from Install.cs'

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
    # 輸出路徑與 trx 檔名都帶時間戳：殘骸 testhost 會鎖住固定的 obj/_rel_<版號>/ 與 rel.trx，
    # 那些行程殺不掉也刪不掉（taskkill 回 success 但行程還在），固定路徑等於「再發一次一定撞鎖」
    # ——MSB3027／MSB3021 說的是檔案被 testhost 佔用，不是產品壞了。
    $stamp = Get-Date -Format 'HHmmss'
    if ($SkipTests) {
        Write-Host '!!! SKIPPING TEST SUITE (-SkipTests) - only acceptable right after a -DryRun of the same version' -ForegroundColor Yellow
    } else {
        Write-Host '==> full test suite'
        $trx = "rel_$stamp.trx"
        # 直出 console（呼叫端自行重定向）——PS 5.1 把大量輸出經管道餵 Out-File 會卡死，
        # 這裡刻意不用管道，維持與手動發版相同的執行方式。
        # -m:1 ＋ 關共用編譯器：平行節點與常駐的 csc 伺服器會同時要一大塊 commit，
        # 在 commit 吃緊的機器上直接讓編譯器以 0xC0000142（DLL init failed）結束，
        # 或讓 MSBuild 只印一句 "Out of memory."。單節點一次只起一個 csc，實測過得了。
        & dotnet test Tests\XinSpect.Tests.csproj -c Debug --nologo -m:1 `
            -p:UseSharedCompilation=false `
            -p:BaseOutputPath="obj/_rel_${Version}_$stamp/" --logger "trx;LogFileName=$trx"
        if ($LASTEXITCODE -ne 0) { Fail "test suite exit code $LASTEXITCODE" }
        $trxPath = "Tests\TestResults\$trx"
        if (-not (Test-Path $trxPath)) { Fail "trx result file not found at $trxPath" }
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
    # 八資產矩陣（2026-11-06）：GUI 二版（不帶環境／帶環境）＋ CLI 四版（兩個 Windows 世代 ×
    # 帶不帶環境）＋ Bridge ＋ 部署器。每顆各自 -o：dotnet publish 會清輸出目錄，
    # 多資產共用一個目錄會互相刪檔（2026-11-06 實測）。
    # CLI 的 OutputPath 也各自獨立——預設 bin 被殭屍進程鎖住時不必等重開機。
    Write-Host '==> dotnet publish (eight assets)'
    $P = "publish-$Version"
    $null = New-Item -ItemType Directory -Force -Path $P

    # 1) GUI XinSpect.exe（framework-dependent 單檔）
    & dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false -m:1 `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:UseSharedCompilation=false `
        -p:BaseOutputPath="obj/_pub_${Version}_$stamp/" -o "$P-gui" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'publish gui failed' }
    Copy-Item "$P-gui\XinSpect.exe" "$P\XinSpect.exe" -Force

    # 2) GUI XinSpect-full.exe（self-contained 單檔，免裝 .NET）
    & dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained true -m:1 `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:UseSharedCompilation=false `
        -o "$P-guifull" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'publish gui-full failed' }
    Copy-Item "$P-guifull\XinSpect.exe" "$P\XinSpect-full.exe" -Force

    # 3) CLI XinSpectConsole.exe（self-contained 單檔）
    & dotnet publish XinSpectCli\XinSpectCli.csproj -c Release -m:1 `
        -p:PublishSingleFile=true -p:SelfContained=true -p:UseSharedCompilation=false `
        -p:OutputPath="XinSpectCli\bin-_pub317" -o "$P-cli" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'publish cli failed' }
    Copy-Item "$P-cli\XinSpectConsole.exe" "$P\XinSpectConsole.exe" -Force

    # 4) CLI legacy（self-contained，Win10／Server 2016-2022 純文字相容界面）
    & dotnet publish XinSpectCli\XinSpectCli.Legacy.csproj -c Release -m:1 `
        -p:PublishSingleFile=true -p:SelfContained=true -p:UseSharedCompilation=false `
        -p:OutputPath="XinSpectCli\bin-_pub317l" -o "$P-clilegacy" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'publish cli-legacy failed' }
    Copy-Item "$P-clilegacy\XinSpectConsole-legacy.exe" "$P\XinSpectConsole-legacy.exe" -Force

    # 5) CLI nort（framework-dependent：需 .NET 10 Desktop Runtime）
    & dotnet publish XinSpectCli\XinSpectCli.csproj -c Release -m:1 `
        -p:PublishSingleFile=true -p:SelfContained=false -p:EnableCompressionInSingleFile=false -p:UseSharedCompilation=false `
        -p:OutputPath="XinSpectCli\bin-_pubnort" -o "$P-clinort" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'publish cli-nort failed' }
    Copy-Item "$P-clinort\XinSpectConsole.exe" "$P\XinSpectConsole-nort.exe" -Force

    # 6) CLI legacy-nort
    & dotnet publish XinSpectCli\XinSpectCli.Legacy.csproj -c Release -m:1 `
        -p:PublishSingleFile=true -p:SelfContained=false -p:EnableCompressionInSingleFile=false -p:UseSharedCompilation=false `
        -p:OutputPath="XinSpectCli\bin-_publnort" -o "$P-clilegacynort" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'publish cli-legacy-nort failed' }
    Copy-Item "$P-clilegacynort\XinSpectConsole-legacy.exe" "$P\XinSpectConsole-legacy-nort.exe" -Force

    # ---------- deep kernel driver ----------
    # 2026-11-07 使用者裁定：XsRegProbe.sys 每版必上（v3.18 補上後成為常態）——
    # 先建置驗證，再進資產清單；SHA256SUMS 與 byte-count docs 會自動把它算進去。
    Write-Host '==> build XsRegProbe.sys'
    $driverSys = 'XsRegProbe\Driver\x64build\XsRegProbe.sys'
    $driverBat = 'XsRegProbe\Driver\x64build\build.bat'
    if (-not (Test-Path $driverSys)) {
        if (-not (Test-Path $driverBat)) { Fail "driver asset missing: $driverSys (no build script either)" }
        & cmd /c $driverBat | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'XsRegProbe.sys build failed (see XsRegProbe/BUILD-給使用者.md)' }
    }
    if (-not (Test-Path $driverSys)) { Fail "driver asset missing after build: $driverSys" }
    Ok "driver $driverSys = $((Get-Item $driverSys).Length) bytes"

    # 九件清單（bridge／deployer 由既有產出提供）
    $assets = @(
        @('XinSpect.exe', "$P\XinSpect.exe"),
        @('XinSpect-full.exe', "$P\XinSpect-full.exe"),
        @('XinSpectConsole.exe', "$P\XinSpectConsole.exe"),
        @('XinSpectConsole-legacy.exe', "$P\XinSpectConsole-legacy.exe"),
        @('XinSpectConsole-nort.exe', "$P\XinSpectConsole-nort.exe"),
        @('XinSpectConsole-legacy-nort.exe', "$P\XinSpectConsole-legacy-nort.exe"),
        @('BlueSquadronBridge.exe', 'publish-2.44-bridge\BlueSquadronBridge.exe'),
        @('XinSpectDeploy.exe', 'Installer\XinSpectDeploy.exe'),
        @('XsRegProbe.sys', 'XsRegProbe\Driver\x64build\XsRegProbe.sys')
    )
    foreach ($a in $assets) {
        if (-not (Test-Path $a[1])) { Fail "asset missing: $($a[1])" }
        Ok ("asset $($a[0]) = " + (Get-Item $a[1]).Length.ToString('#,0') + ' bytes')
    }

    # ---------- byte-count docs commit（README 表列的每一顆都對實測值）----------
    $changed = @()
    foreach ($f in $zhFiles + @('README.en.md', 'docs/百科-v3.17.md')) {
        $r = ReadUtf8 $f; $t = $r[0]
        $new = $t
        foreach ($a in $assets) {
            $disp = (Get-Item $a[1]).Length.ToString('#,0')
            $new = [regex]::Replace($new, '(' + [regex]::Escape($a[0]) + '\]\([^)]*\) \| )[\d,]+(?= bytes)', ('${1}' + $disp))
        }
        if ($new -ne $t) { WriteUtf8 $f $new $r[1]; $changed += $f; Write-Host "$f`: byte counts updated" }
    }
    if ($changed.Count -gt 0) {
        git add -- @($changed) | Out-Null
        git commit -m "docs：README 主程式位元組數（含全部資產）改為 v$Version 發佈實測值" | Out-Null
        git -c http.version=HTTP/1.1 push origin main | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'push docs commit failed' }
        Ok 'docs byte-count commit pushed'
    } else {
        Write-Host 'byte counts already current - no docs commit needed'
    }

    # ---------- release + assets ----------
    Write-Host '==> create/update Release and upload nine assets + SHA256SUMS.txt'
    $bridge = 'publish-2.44-bridge\BlueSquadronBridge.exe'
    if (-not (Test-Path $bridge)) { Fail "bridge asset source missing: $bridge" }

    # SHA256SUMS.txt：逐資產雜湊，讓不用 GitHub API 的使用者也能比對下載完整性。
    # 界線（寫進檔頭註解）：它防傳輸損毀，防不了能替換整個 Release 的攻擊者——
    # 同一通道可同時換掉 SUMS；真正的不可否認要簽名（minisign/sigstore，排期）。
    # 2026-11-06 曾依指示移除、11-07 審計後回復——「每個數字可複算」的自我要求包含發佈物本身。
    $sums = Join-Path $env:TEMP "SHA256SUMS-$Version.txt"
    $sumLines = @("# XinSpect v$Version SHA-256",
                  "# 防下載損毀與傳輸錯誤；不可否認性需簽名驗證（尚未提供）。比對：certutil -hashfile <檔> SHA256")
    foreach ($a in $assets) {
        $h = (Get-FileHash -Algorithm SHA256 -Path $a[1]).Hash.ToLowerInvariant()
        $sumLines += "$h  $($a[0])"
    }
    [IO.File]::WriteAllText($sums, (($sumLines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
    Ok "SHA256SUMS.txt = $((Get-Item $sums).Length) bytes（逐資產實算）"

    $up = @()
    foreach ($a in $assets) { $up += '--asset'; $up += "$($a[0])=$($a[1])" }
    $up += '--asset'; $up += "SHA256SUMS.txt=$sums"
    & python Tools/publish_release.py --tag "v$Version" --title $Title --body-file $BodyFile @up
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
