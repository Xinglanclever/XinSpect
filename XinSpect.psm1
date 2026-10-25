# 曦覽 XinSpect ・ PowerShell 模組（V7 WP32／A42＋U10-PF-0017 逐域 cmdlet）
# 包裝 XinSpect.exe 的 CLI 模式：headless 收集驅動相依證據組、JSON 輸出、退出碼語意。
# 用法（先 Import-Module .\XinSpect.psm1）：
#   Get-XinSpectEvidence -ExePath C:\Tools\XinSpect.exe
#   Get-XinSpectEvidence -ExePath .\XinSpect.exe -Query platform. -OutFile .\evidence.json
#   Get-XinSpectCpu -ExePath .\XinSpect.exe          # 逐域便捷 cmdlet，輸出 PSObject
#   Get-XinSpectStorage | Where-Object Availability -eq 'Present'
# 追蹤鍵：ps.modules.gap（U10-PF-0017 逐域 cmdlet）。
# 各域前綴清單只列 2026-10-25 盤點時程式碼裡真有鍵的前綴——查無鍵的前綴（wifi.、typec.、nfc.、
# thermal.）不列：Wi-Fi／藍牙走 nw.，USB 走 usb.與 nw.usb.*，熱／感測器走 cpu.thermal、ec.thermal、
# fan.、volt.、sensors.——查空前綴不是錯誤，但把不存在的東西寫進說明就是誤導。

## ── 通用（原始 JSON）──────────────────────────────────────────────

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

## ── 逐域便捷 cmdlet（U10-PF-0017）───────────────────────────────
# 每個域 cmdlet 內部走 Get-XinSpectEvidence -Query <前綴>，再把 JSON 轉成 PSObject。

function script:InvokeDomain([string]$Domain, [string[]]$Prefixes, [string]$ExePath) {
    $facts = @()
    foreach ($p in $Prefixes) {
        $json = Get-XinSpectEvidence -ExePath $ExePath -Query $p
        # 查無鍵的前綴 CLI 回空——空不是錯誤，如實跳過（不編造空物件冒充事實）。
        if (-not [string]::IsNullOrWhiteSpace($json)) {
            $facts += @($json | ConvertFrom-Json)
        }
    }
    Write-Verbose "$Domain：取得 $($facts.Count) 筆事實"
    return $facts
}

function Get-XinSpectCpu {
    <#
    .SYNOPSIS
    取得 CPU 事實（頻率、拓撲、CPUID 能力位、微碼、電源）
    .DESCRIPTION
    鍵前綴：cpu. cache. msr. amd. mc. pmu. numa. ms.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain 'CPU' @('cpu.', 'cache.', 'msr.', 'amd.', 'mc.', 'pmu.', 'numa.', 'ms.') $ExePath
}

function Get-XinSpectMemory {
    <#
    .SYNOPSIS
    取得記憶體事實（SPD、容量、延遲、ECC、快取映射）
    .DESCRIPTION
    鍵前綴：mem. spd.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '記憶體' @('mem.', 'spd.') $ExePath
}

function Get-XinSpectMotherboard {
    <#
    .SYNOPSIS
    取得主機板與韌體事實（SMBIOS、ACPI、BIOS、晶片組、CMOS）
    .DESCRIPTION
    鍵前綴：sb. ac. acpi. chipset. cmos. smbus.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '主機板' @('sb.', 'ac.', 'acpi.', 'chipset.', 'cmos.', 'smbus.') $ExePath
}

function Get-XinSpectFirmware {
    <#
    .SYNOPSIS
    取得韌體與信任根事實（UEFI、TPM、開機鏈、安全開機、微碼、SPI）
    .DESCRIPTION
    鍵前綴：fw. uefi. boot. tpm. mc. spi. esp.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '韌體' @('fw.', 'uefi.', 'boot.', 'tpm.', 'mc.', 'spi.', 'esp.') $ExePath
}

function Get-XinSpectStorage {
    <#
    .SYNOPSIS
    取得儲存事實（NVMe、ATA/SATA、SMART、檔案系統、GPT、分割）
    .DESCRIPTION
    鍵前綴：nvme. ata. disk. fs. smart. storage. usbstor. raid.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '儲存' @('nvme.', 'ata.', 'disk.', 'fs.', 'smart.', 'storage.', 'usbstor.', 'raid.') $ExePath
}

function Get-XinSpectGpu {
    <#
    .SYNOPSIS
    取得 GPU 與顯示事實（NVML、ADL、EDID、模式、HDR）
    .DESCRIPTION
    鍵前綴：gpu. edid. disp. mon. display. cam.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain 'GPU' @('gpu.', 'edid.', 'disp.', 'mon.', 'display.', 'cam.') $ExePath
}

function Get-XinSpectNetwork {
    <#
    .SYNOPSIS
    取得網路與周邊事實（NIC、Wi-Fi、藍牙、USB）
    .DESCRIPTION
    鍵前綴：net. bt. usb. nic. nw.（Wi-Fi＝nw.wifi.*、藍牙＝nw.bt.*——沒有 wifi./typec./nfc. 前綴的鍵）
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '網路' @('net.', 'bt.', 'usb.', 'nic.', 'nw.') $ExePath
}

function Get-XinSpectPower {
    <#
    .SYNOPSIS
    取得電源／熱／感測器事實（電池、風扇、電壓軌、PSU、感測器）
    .DESCRIPTION
    鍵前綴：fan. volt. bat. psu. pw. sensors.（熱事件走 cpu.thermal／ec.thermal，屬 CPU 與 EC 域——無 thermal. 前綴的鍵）
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '電源熱' @('fan.', 'volt.', 'bat.', 'psu.', 'pw.', 'sensors.') $ExePath
}

function Get-XinSpectOs {
    <#
    .SYNOPSIS
    取得 OS／驅動事實（驅動版本、事件、登錄檔、音訊端點、DEVPKEY）
    .DESCRIPTION
    鍵前綴：os. audio. drvinsp. reg. evt.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain 'OS' @('os.', 'audio.', 'drvinsp.', 'reg.', 'evt.') $ExePath
}

function Get-XinSpectSecurity {
    <#
    .SYNOPSIS
    取得安全態勢事實（VBS、BitLocker、簽章、BYOVD、攻擊面）
    .DESCRIPTION
    鍵前綴：sec. byovd. sa. ds. virt. guard. cert.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '安全' @('sec.', 'byovd.', 'sa.', 'ds.', 'virt.', 'guard.', 'cert.') $ExePath
}

function Get-XinSpectTests {
    <#
    .SYNOPSIS
    取得檢測與測試結果（唯讀探測、跑分、螢幕/音訊/輸入測試）
    .DESCRIPTION
    鍵前綴：ts. test.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param([string]$ExePath = "XinSpect.exe")
    InvokeDomain '測試' @('ts.', 'test.') $ExePath
}

Export-ModuleMember -Function Get-XinSpectEvidence
Export-ModuleMember -Function @(
    'Get-XinSpectCpu', 'Get-XinSpectMemory', 'Get-XinSpectMotherboard',
    'Get-XinSpectFirmware', 'Get-XinSpectStorage', 'Get-XinSpectGpu',
    'Get-XinSpectNetwork', 'Get-XinSpectPower', 'Get-XinSpectOs',
    'Get-XinSpectSecurity', 'Get-XinSpectTests'
)
