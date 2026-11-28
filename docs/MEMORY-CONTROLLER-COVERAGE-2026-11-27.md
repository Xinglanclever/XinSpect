# 目前記憶體控制器觀測：來源與能力界線

日期：2026-11-27。範圍是本輪新增的生產接線與純投影，不是全平台或本機硬體精度驗收。

## 既有實際來源

`SensorService.Poll` 原本就會更新 LibreHardwareMonitor 0.9.6。新增的
`SensorService.MemoryController.cs` 在同一次成功輪詢的鎖內，複製已存在的 AMD CPU
`Clock` 感測器 `Memory`、`Uncore`、`Fabric`，分別保留 MCLK、UCLK、FCLK；
不再次呼叫 `Update`，不建立另一個硬體存取服務，不擴大 SMN 位址白名單。

限定 `HardwareType.Cpu`、`/amdcpu/<非負整數>` 的 CPU 身分，以及該 CPU 下
`/clock/<非負整數>` 的精確感測器身分；GPU Memory、Intel Uncore、跨 CPU 感測器
及重複感測器不會混為同一個控制器值。多 CPU 各保留自己的範圍，不擇一當全機值。

NuGet 0.9.6 的 repository commit 為 `3d331e3370efb858411f19511373eff65a218701`。
本輪以該已安裝 MPL-2.0 元件的 win-x64/net10.0 組件核對三欄名稱、型別、
比例與失敗語意（SHA-256 `984B539048880D37257A5EDEAB36378152C54943069F400D68A34565292D35DA`）；
沒有執行其中的硬體呼叫。其 `RyzenSMU` 三欄映射存在於下列特定 PM-table 佈局：

| PM-table 版本 | 上游尺寸派發涉及的代號（非實機支援證據） | 此版三欄 |
|---|---|---|
| `0x001E0004` | Raven Ridge／Raven Ridge 2／Picasso | Memory／Uncore／Fabric |
| `0x00240903` | Matisse | Memory／Uncore／Fabric |
| `0x00380805` | Vermeer | Memory／Uncore／Fabric |
| `0x00540004` | Raphael／Granite Ridge | Memory／Uncore／Fabric |

這不是世代全覆蓋表；同屬 Zen 2／3／4、Threadripper／EPYC，不能只憑名稱推定相容。
本服務只接受本輪硬體樹真的提供的感測器，沒有按 CPU 名稱猜時脈或 PM-table 位址。
Intel、其他 AMD PM-table 版本及沒有提供三欄的 provider 如實顯示未支援／缺值。

## 新鮮度與六態

套件的 `RyzenSMU.GetPmTable()` 讀取失敗可回長度一的陣列；`Amd17Cpu` 只有在
陣列長度大於欄位索引時才更新該 `Sensor.Value`，其餘欄位可能仍是舊值。
成功呼叫 `hardware.Update()` 不代表每個
PM-table 欄位成功刷新，因此本路徑的正有限時脈數字為 **Reported／Unknown**，
理由明列 provider 快取與更新時間未知，不宣稱 Measured／Present 的即時控制器量測。

本程式擷取時間另存 UTC；來源實際更新時間保持 null／未知。應用程式擷取在單調鐘
上超過 30 秒即清除數字並顯示「擷取已過期」；不是用牆鐘差計算耗時。這個本程式
快照 TTL 大於既有可設定的 10 秒主輪詢間隔，並不確認 provider 內部快取新鮮度。
輪詢失敗或服務關閉立即使本程式已保存的數字失效為 ReadError。
控制器觀測支線的 optional getter／Identifier 擷取異常只使這條支線發布具名原因的
ReadError，不能將其他 CPU／感測器的成功輪詢改成失敗，也不能當作沒有硬體。
缺值、0、負值、NaN、Infinity、超出既有 `SensorSanity.MaxClockMHz`（12000 MHz）
的值與重複同名感測器不會轉成成功，也不套用上次成功值；沒有額外倍率校正。

## WMI、CPU-Z、SPD 與副時序

`MemoryControllerService.FromTimings` 在既有來源優先序合成前保留各量。
WMI `Win32_PhysicalMemory.ConfiguredClockSpeed` 仍是 Windows 設定資料速率；
不除以二填成目前 MCLK，不推定 UCLK、FCLK 或控制器分頻模式。
CPU-Z `Memory Frequency` 另保留原始報告的 DRAM 時脈，不因 WMI 設定值優先而遺失。
CPU-Z 的通用 `Uncore Frequency` 沒有足夠平台語意，不能改名為 AMD UCLK。
來源報告沒有裝置／控制器索引時，範圍明列全機及未提供逐控制器範圍。

目前副時序支援既有 CPU-Z 報告欄位 tRFC、tCCD、tCCD_L、tCCD_WR、tCCD_WR_L，
逐欄保留來源、信任、六態、raw field 與原文；正整數才顯示為可解讀的 cycles。
未知或畸形原文保留作證據並標 Unknown。來源本身的缺權限或讀取錯誤不升格為 Present。
這輪**沒有新增控制器暫存器副時序實讀**。tRC、tRRD_S/L、tFAW、tWTR_S/L、
tWR、tRTP、tCWL 未支援。SPD JEDEC／XMP／EXPO 是能力表，不能填成目前值。

## 範圍、預算與驗證

新路徑沒有 I/O、額外執行緒、背景工作、重試或硬體壓力。執行量受既有硬體樹
CPU 與感測器集合限制；沿用既有 `_pollLock` 的輪詢／關閉生命週期。
不可將呼叫端逾時描述成已取消底層 LHM I/O。來源是否已載入驅動及既有安全閘門
仍由原本感測器服務處理；新投影不啟用風險設定、不安裝／載入任何新驅動。

UI 由記憶體頁現有兩秒計時器刷新，不新增採集。新欄位目前是桌面記憶體頁的
來源觀測；CLI 尚未新增逐 MCLK／UCLK／FCLK 或副時序 producer，不能把本次
桌面接線描述為 CLI 能力完成。

測試檔：`Tests/MemoryControllerTests.cs`，涵蓋 provider 身分／單位／六態、
重複與多 CPU、無效值、單調鐘到期、失敗失效、SPD 隔離，以及正式 resolver
保留 CPU-Z 目前時脈與 WMI 設定值。實際建置／測試結果由 root 整合輪次另記，
本文件不預填通過數。尚缺限定 AMD 實機的來源更新行為及硬體對照證據。

## 可追溯的原始來源

- LibreHardwareMonitor 0.9.6 repository commit，`Hardware/RyzenSMU.cs`：PM-table 版本、欄位名稱與單位。
  <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/RyzenSMU.cs>
- 同 commit，`Hardware/Cpu/Amd17Cpu.cs`：SMU 感測器建立與不完整表的更新語意。
  <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs>
- Microsoft `Win32_PhysicalMemory.ConfiguredClockSpeed`：WMI 回報的設定時脈來源，
  本專案既有 DDR 資料速率投影另有方法學界線。
  <https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory>

沒有使用或複製 CPU-Z、AIDA64、HWiNFO 的專有原始碼或二進位。
