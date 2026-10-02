# 曦覽 XinSpect ・ PowerShell 模組（V7 WP32／A42）
# 包裝 XinSpect.exe 的 CLI 模式：headless 收集驅動相依證據組、JSON 輸出、退出碼語意。
# 用法（先 Import-Module .\XinSpect.psm1）：
#   Get-XinSpectEvidence -ExePath C:\Tools\XinSpect.exe
#   Get-XinSpectEvidence -ExePath .\XinSpect.exe -Query platform. -OutFile .\evidence.json

function Get-XinSpectEvidence {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        # XinSpect.exe 的路徑（預設假設當前目錄有 exe）
        [string]$ExePath = "XinSpect.exe",
        # 只輸出 key 以此前綴開頭的事實（例：platform. 只看平台安全組）
        [string]$Query,
        # 寫入檔案而不輸出 JSON 字串
        [string]$OutFile
    )

    $cliArgs = @("--json", "evidence")
    if ($Query) { $cliArgs += @("--query", $Query) }
    if ($OutFile) { $cliArgs += @("--out", $OutFile) }

    $output = & $ExePath @cliArgs
    # 退出碼語意：0＝全部 Present；2＝收集完成但部分讀不到（三態如實反映在 JSON 裡）；其他＝致命錯誤。
    if ($LASTEXITCODE -eq 2) {
        Write-Warning "部分事實讀不到——availability 與原因在 JSON 的 availability／unavailableReason 欄位，請逐項判讀。"
    }
    elseif ($LASTEXITCODE -ne 0) {
        throw "XinSpect CLI 失敗（退出碼 $LASTEXITCODE）。"
    }

    if ($OutFile) { return Get-Content -Raw $OutFile }
    return ($output -join "`n")
}

Export-ModuleMember -Function Get-XinSpectEvidence
