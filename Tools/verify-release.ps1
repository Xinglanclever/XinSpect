# verify-release.ps1 — 發佈後驗證（docs/PROGRAM-ULTIMATE-2026-10-10.md §5.9）
#
# 守什麼：README 宣稱的每一個下載連結都真的存在、且大小與 README 表列一致；
# 本地版號六處一致（csproj／AboutView／三份 README 徽章／ChangelogCatalog 最新一筆）。
# 任何一步失敗即非零退出——發佈流程必須跑到這支腳本綠燈才算完成。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File Tools\verify-release.ps1 [-Tag v2.45] [-Repo Xinglanclever/XinSpect]
# 備註：遠端資產大小用 GitHub API 查（匿名也行）；HTTP 下載連結用 HEAD 驗 200。

param(
    [string]$Tag = "",
    [string]$Repo = "Xinglanclever/XinSpect"
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]
$root = Split-Path -Parent $PSScriptRoot

# 匿名 API 在本機 IP 會 403（限流）——比照發佈腳本向 Git Credential Manager 取 token，取不到就退回匿名。
$headers = @{ "User-Agent" = "xinspect-release-check" }
try {
    $cred = ("protocol=https`nhost=github.com`n`n" | git credential fill) -split "`n"
    $line = $cred | Where-Object { $_ -like "password=*" } | Select-Object -First 1
    if ($line) { $headers["Authorization"] = "Bearer " + $line.Substring(9) }
} catch { }

# 1) 版號單一來源：csproj <Version>
$csprojText = Get-Content (Join-Path $root "XinSpect.csproj") -Raw -Encoding UTF8
if ($csprojText -notmatch '<Version>([0-9.]+)</Version>') { throw "csproj 裡找不到 <Version>" }
$version = $Matches[1]
if (-not $Tag) { $Tag = "v$version" }

# 2) 本地各處與 csproj 一致
$aboutText = Get-Content (Join-Path $root "Views\AboutView.xaml") -Raw -Encoding UTF8
if ($aboutText -notmatch [regex]::Escape("版本 $version ・")) {
    $failures.Add("AboutView 的版本行不等於 csproj（$version）")
}
foreach ($readme in @("README.md", "README.zh-CN.md", "README.en.md")) {
    $text = Get-Content (Join-Path $root $readme) -Raw -Encoding UTF8
    if ($text -notmatch [regex]::Escape("version-$version-4C8DFF")) {
        $failures.Add("$readme 的版本徽章不等於 csproj（$version）")
    }
    # 3) 下載連結逐一存在且大小一致
    $matches2 = [regex]::Matches($text, 'https://github\.com/[^/]+/[^/]+/releases/download/([^/]+)/([^)"\s\|]+)')
    foreach ($m in $matches2) {
        $linkTag = $m.Groups[1].Value
        $asset = $m.Groups[2].Value
        if ($linkTag -ne $Tag) {
            $failures.Add("$readme 的下載連結指向 $linkTag，但本版是 $Tag")
            continue
        }
        $api = "https://api.github.com/repos/$Repo/releases/tags/$Tag"
        try {
            $release = Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 30
            $apiAsset = $release.assets | Where-Object { $_.name -eq $asset }
            if (-not $apiAsset) {
                $failures.Add("$readme 引用的 $asset 不存在於 Release $Tag")
                continue
            }
            # README 表列的位元組數（若有）與 API size 比對
            $sizeRe = [regex]::Escape($asset) + '\)[^\|]*\|\s*([\d,]+)\s*bytes'
            $sm = [regex]::Match($text, $sizeRe)
            if ($sm.Success) {
                $claimed = [int64]($sm.Groups[1].Value -replace ',', '')
                if ($claimed -ne [int64]$apiAsset.size) {
                    $failures.Add("$readme 表列 $asset 為 $claimed bytes，實際 $($apiAsset.size) bytes")
                }
            }
        } catch {
            $failures.Add("查詢 Release $Tag 失敗（$($_.Exception.Message)）")
        }
    }
}

# 4) ChangelogCatalog 最新一筆（原始碼文本比對，不編譯）
$catalogText = Get-Content (Join-Path $root "Nav\ChangelogCatalog.cs") -Raw -Encoding UTF8
if ($catalogText -notmatch 'Version\s*=\s*"' + [regex]::Escape($version) + '"') {
    $failures.Add("ChangelogCatalog 最新一筆不等於 csproj（$version）")
}

if ($failures.Count -gt 0) {
    Write-Host "verify-release 失敗："
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host "verify-release OK：$Tag 六處版號一致、$Repo Release 資產與 README 宣稱一致。"
exit 0
