#requires -Version 5.1
# 一鍵安裝器（XinSpectDeploy.exe）的建置腳本。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\build-installer.ps1
#
# 為什麼不用 dotnet：这个 exe 只有十幾 KB，是拿 .NET Framework 4.8 隨附的 csc.exe
# 直接編 Installer\Install.cs 出來的——使用者机器上一定裝著 .NET Framework 4.8，
# 所以一支十幾 KB 的執行檔就能開機下載，不需要 self-contained runtime、也不需要 dotnet SDK。
# 副作用：csc 4.8 只支援到 C# 5，所以 Install.cs 裡不能寫插值字串、expression-bodied
# member、nameof 這些新語法（編譯會直接紅，不會悄悄過）。
#
# 這支腳本是為了让「改了 Install.cs 卻忘了重編 Installer\XinSpectDeploy.exe」變成不可能：
# 發佈前一律跑一次，release.ps1 的上傳步驟取的正是這個輸出路徑。
# 編完之後會用 Installer\tests\ProgressTest.cs 驗一次進度列（含真下載與斷線），
# 那一步會開本機 HttpListener，需要以系統管理員身分執行。
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { Write-Host 'BUILD-FAIL: 找不到 .NET Framework 4.8 的 csc.exe' -ForegroundColor Red; exit 1 }

    $src = Join-Path 'Installer' 'Install.cs'
    $out = Join-Path 'Installer' 'XinSpectDeploy.exe'
    $rsp = Join-Path $env:TEMP 'xinspect-installer.rsp'
    # 用 response file 送參數：路徑裡有反斜線，從 shell 直接傳會被当选项吃掉（踩過兩次）。
    @(
        '/nologo'
        '/target:exe'
        '/optimize+'
        '/platform:anycpu'
        "/out:$out"
        $src
    ) | Set-Content -Path $rsp -Encoding ASCII

    & $csc "@$rsp"
    if ($LASTEXITCODE -ne 0) { Write-Host "BUILD-FAIL: csc exit $LASTEXITCODE" -ForegroundColor Red; exit 1 }
    if (-not (Test-Path $out)) { Write-Host "BUILD-FAIL: $out 不存在" -ForegroundColor Red; exit 1 }

    $bytes = (Get-Item $out).Length
    Write-Host "BUILD-OK: $out = $bytes bytes"
    # 安裝器是產品的一部分，所以也要能被驗：這裡只做最小檢查——
    # 必須是 PE、必須含 .NET 中繼資料、必須在 20 KB 以内（超過就是有人把 runtime 塞進來了）。
    $head = [IO.File]::ReadAllBytes($out)
    if ($head[0] -ne 0x4D -or $head[1] -ne 0x5A) { Write-Host 'BUILD-FAIL: 不是 PE 檔' -ForegroundColor Red; exit 1 }
    $text = [Text.Encoding]::ASCII.GetString($head)
    if ($text -notmatch '_CorDllMain|mscorwks|mscorlib') { Write-Host 'BUILD-FAIL: 看不到 .NET 中繼資料' -ForegroundColor Red; exit 1 }
    if ($bytes -gt 20000) { Write-Host "BUILD-FAIL: $bytes bytes 超過 20 KB——安裝不該帶 runtime" -ForegroundColor Red; exit 1 }
    Write-Host 'BUILD-OK: 形態檢查通過（PE + .NET + <20 KB）'

    # 進度列的驗證：載入剛編好的那支 exe（不是原始碼），用反射餵它假的位元組數，
    # 再起一個本機 HTTP 伺服器真的下載三遍——完整、只回一半就斷線、沒有 Content-Length。
    # 「重編了」不等於「重編出來的東西能畫進度列」，所以這一步跟建置綁在一起。
    $ptSrc = Join-Path 'Installer' 'tests\ProgressTest.cs'
    if (-not (Test-Path $ptSrc)) { Write-Host 'BUILD-FAIL: 找不到 Installer\tests\ProgressTest.cs' -ForegroundColor Red; exit 1 }
    $ptExe = Join-Path $env:TEMP 'xinspect-progress-test.exe'
    $ptRsp = Join-Path $env:TEMP 'xinspect-progress-test.rsp'
    @('/nologo', '/target:exe', "/out:$ptExe", $ptSrc) | Set-Content -Path $ptRsp -Encoding ASCII
    & $csc "@$ptRsp"
    if ($LASTEXITCODE -ne 0) { Write-Host "BUILD-FAIL: 進度列驗證程式編譯失敗（csc exit $LASTEXITCODE）" -ForegroundColor Red; exit 1 }
    & $ptExe $out
    if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD-FAIL: 進度列驗證沒有全過——看上面哪一條 FAIL' -ForegroundColor Red; exit 1 }
    Write-Host 'BUILD-OK: 進度列（條子／速度／剩餘時間／斷線）全數通過'
    exit 0
} finally {
    Pop-Location
}
