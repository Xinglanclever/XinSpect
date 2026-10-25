#requires -Version 5.1
# XinSpect 可重現建置比對（U10-GV-0015）：同一份原始碼連續兩次單檔發佈，比對兩顆
# XinSpect.exe 的 SHA-256，輸出一致／不一致與兩個雜湊。
#
# ⚠ 手動／發版前檢查用，不是常設閘——全套等於跑兩次 Release 發佈，太慢，放進常設
#   測試會被 GuardBudgetTests 的時間預算咬；發版前手動跑一次、結果如實記錄。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File Tools\repro-build.ps1
# （一致性成立 exit 0；不一致 exit 2——不是腳本壞了，是建置真的不可重現，如實回報。）
#
# 教訓（2026-10-25 v2.88）：XinSpect.csproj 的 BuildAndEmbedXtuBridge target 檢查路徑寫死在
# Bridge\bin\$(Configuration)\net48\XtuBridge.exe，而命令列的 -p:BaseOutputPath 會經巢狀
# MSBuild 的全域屬性把 XtuBridge 的產出改道——寫死的檢查就找不到檔案。所以這支腳本
# 先手動把 Bridge 以 Release 建好（產出落在寫死路徑），再跑兩次 publish。

$ErrorActionPreference = 'Stop'
function Fail([string]$m) { Write-Host "REPRO-FAIL: $m" -ForegroundColor Red; exit 1 }
function Ok([string]$m)   { Write-Host "REPRO-OK: $m" -ForegroundColor Green }

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    # ── step 0：先把 XtuBridge 建到寫死的檢查路徑（v2.88 教訓）─────────────────
    Write-Host '==> dotnet build Bridge/XtuBridge.csproj -c Release'
    & dotnet build Bridge/XtuBridge.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'bridge build failed' }
    $bridgeExe = 'Bridge/bin/Release/net48/XtuBridge.exe'
    if (-not (Test-Path $bridgeExe)) { Fail "bridge output missing at hardcoded check path: $bridgeExe" }
    Ok "bridge built at $bridgeExe"

    # ── 兩次 publish：只差 BaseOutputPath 與輸出目錄，其他參數與 release.ps1 相同 ──
    $pairs = @(
        @{ Base = 'obj/_repro_a/'; Out = 'publish-repro-a' },
        @{ Base = 'obj/_repro_b/'; Out = 'publish-repro-b' }
    )
    $runs = @()
    foreach ($p in $pairs) {
        Write-Host ("==> dotnet publish -> " + $p.Out)
        & dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:Deterministic=true `
            -p:BaseOutputPath=$($p.Base) -o $p.Out | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "publish failed for $($p.Out)" }
        $exe = Join-Path $p.Out 'XinSpect.exe'
        if (-not (Test-Path $exe)) { Fail "publish output missing: $exe" }
        $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
        $bytes = (Get-Item $exe).Length
        $runs += , @($exe, $hash, $bytes)
        Ok ("{0} = {1:N0} bytes, SHA-256 {2}…" -f $exe, $bytes, $hash.Substring(0, 16))
    }

    Write-Host ''
    Write-Host ("A: " + $runs[0][1])
    Write-Host ("B: " + $runs[1][1])

    if ($runs[0][1] -eq $runs[1][1]) {
        Ok '兩顆 XinSpect.exe 的 SHA-256 一致——可重現建置成立'
    }
    else {
        Write-Host 'REPRO-DIFF: 兩顆 XinSpect.exe 的 SHA-256 不一致——如實記錄，不為了綠而調參。' -ForegroundColor Yellow
        Write-Host '可能成因：巢狀 MSBuild 對 XtuBridge／BlueSquadronBridge 的兩次建置之間存在非確定性輸出' -ForegroundColor Yellow
        Write-Host '（PE 時間戳、中繼路徑差異、NuGet 快取差異）；可用 dotnet build -bl 對兩次建置的二進位輸入追因。' -ForegroundColor Yellow
        exit 2
    }
}
finally {
    Pop-Location
}
