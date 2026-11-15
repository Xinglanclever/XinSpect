# U10 第四圈候選清單（v0，2026-11-15）

> 從主目錄 10,022 列中挑出 32 項（逐列數出來的，不是估的）。第三圈補的是「可信度」；
> 第四圈的主題是**「把機器自述的意義補齊」**——解碼表（WHEA 節區、DMT 時序、SMART 廠商屬性）、
> 開機元素的現值（BCD）、與 WMI 官方類的補遺。全部唯讀，大部分是純資料表。
> 本清單為建議，圈選權在使用者。下列「初判線索」不是判定，圈選後照四步走（查重腳本 → 唯讀探測 → 查重收口 → 知識包）。

## 挑選原則（沿第三圈）

| 原則 | 意思 |
|---|---|
| 唯讀 | 不寫 MSR／EC／BCD；bcdedit 只走 `/enum` 或登錄檔讀取 |
| 可驗 | 知識包要有「唯一分母」可機器複算（第 276 輪 VIC 包先例）；現值鍵要有六態形狀 |
| 不重複 | 候選鍵前綴（`whea.sec.`／`dmt.`／`bcd.`／`wmi.`／`kp.smart.intel/hgst`）經 grep 確認 0 命中 |
| 域分散 | 六個子域，一輪只動一個讀取面 |

## 本圈開工前的一次盤點教訓（記錄在案）

圈選前做了一次「孤兒服務」全庫盤點（211 個 `*FactsService` 的生產引用數），
第一輪掃出 68 個「零引用」——**全部是工具誤判**：`grep` 對含中文內容（UTF-8）的檔案
在部分條件下回「binary file matches」並跳過匹配，必須加 `-a` 才算數。加 `-a` 重掃後
真孤兒為 **0**：本專案自 2026-10-10 起就有 `Tests/WiringGuardTests.cs` 在守「服務必須被
生產入口引用」（檔案內宣告的類別名為準），兩個檔案層級的假陽性都是「檔名≠類別名」
（`ChassisAndHpaFactsService.cs` 內是 Chassis／Hpa 兩類、`ExportAndCacheFactsService.cs`
內是 JsonlExportService／QueryResultCache）。**教訓：引用數掃描一律 `grep -a`；
「服務沒接線」的結論要過 `WiringGuardTests` 的口徑再說出口。**

## 32 項清單

### A ・ WHEA 節區知識包（12 項）——主目錄 WH 域剩餘全部

| ID | 項目 | 初判線索 |
|---|---|---|
| U10-WH-0001 | x86/x64 處理器錯誤節區（型別 0）解碼 | `whea.sec.*` 0 命中；第 326 輪已建 CPER 解碼形狀（WheaMemorySectionDecoder） |
| U10-WH-0002 | 處理器泛用錯誤節區（型別 1）解碼 | 同上 |
| U10-WH-0005 | 韌體錯誤記錄參照節區（型別 3） | 同上 |
| U10-WH-0006 | PCIe 錯誤節區（型別 4）解碼 | 同上；裝置型別欄全表見 WH-0007 |
| U10-WH-0007 | PCIe 節區之裝置型別欄全表 | 純對照表（Root Port/Endpoint/Bridge…） |
| U10-WH-0008 | PCI/PCI-X 匯流排節區（型別 5；legacy 如實） | 純解析 |
| U10-WH-0009 | PCI/PCI-X 裝置節區（型別 6；legacy） | 純解析 |
| U10-WH-0010 | 裝置驅動錯誤節區解碼 | 純解析 |
| U10-WH-0012 | IPF/CPE 遺產節區（現代 x86＝NotApplicable 如實） | 六態形狀，不猜 |
| U10-WH-0013 | 未知名節區 GUID 原始列出 | 知識包未收錄者不猜語意——沿用主目錄規則 |
| U10-WH-0014 | 泛用錯誤狀態塊（GHES）位元組解碼 | cper.h／HEST 對照 |
| U10-WH-0015 | 通知類型（HEST/GHES：GPIO/SCI/HED/NMI…）解碼表 | 純對照表 |

### B ・ DMT 顯示時序知識包（5 項）——VIC 包（第 276 輪）的另一半

| ID | 項目 | 初判線索 |
|---|---|---|
| U10-VC-0001～0020 | DMT 逐型號表（640x350@85 至 2048x1536@60，21 組併 4 列） | `dmt.` 0 命中；Cta861.cs 只有 VIC 沒有 DMT |
| U10-VC-0021 | DMT 代碼號對照（EDID standard timing 位元組→DMT 名） | 已定案（AUDIT-pending），可入表 |
| U10-VC-0038 | HDMI VIC 與 CTA VIC 分命名空間成文 | 純規則列；VIC 表已能掛 |
| U10-VC-0040 | 建立時序 bitmap 與 DMT 對照之完整性自校 | 知識包自校先例：VIC 154 條唯一分母 |
| U10-VC-0043 | 時序知識包版本與來源宣告 | `vic.kp.version` 未見 |

### C ・ BCD 開機元素（8 項）——第 315 輪（boot.order.*）的延續

| ID | 項目 | 初判線索 |
|---|---|---|
| U10-BD-0002 | toolsdisplayorder | `bcd.` 0 命中；bcdedit /enum 同一讀取面 |
| U10-BD-0004 | {fwbootmgr} displayorder（與 UEFI BootOrder 對照） | 韌體層順序 |
| U10-BD-0005 | bootsequence（一次性開機順序） | 同上 |
| U10-BD-0006＋0007 | bootmgr 與 OS loader 的 device／path | 一體兩面 |
| U10-BD-0011 | nx（DEP 政策值） | 唯讀現值 |
| U10-BD-0014 | hypervisorlaunchtype（與 CPUID/ECFER 證據並列） | 唯讀現值 |
| U10-BD-0023＋0024 | badmemorylist／removememory（記憶體遮蔽——「少 1GB」的答案） | 唯讀現值 |
| U10-BD-0030 | BCD 存取法與快取新鮮度宣告 | 界線列 |

### D ・ WMI 官方類補遺（4 項）

| ID | 項目 | 初判線索 |
|---|---|---|
| U10-WM-0001 | Win32_Bus（實體匯流排物件） | `wmi.` 0 命中 |
| U10-WM-0008＋0010 | Win32_PortResource／Win32_IRQResource（I/O 埠與 IRQ 資源） | 與既有 `io.*`（IoPortFactsService）對帳——初判只補 WMI 這一面 |
| U10-WM-0018 | Win32_USBControllerDevice（控制器↔裝置關聯——拓撲佐證） | 與 USB 拓撲既有鍵交叉，不重複生產 |
| U10-WM-0036 | 官方硬體類清單對帳（本目錄覆蓋率 vs Learn 頁全表） | 收尾列：缺者補列、多者註明來源 |

### E ・ SMART 知識包補遺（1 項併 2 廠）

| ID | 項目 | 初判線索 |
|---|---|---|
| U10-KP-0004～0006＋0011～0012 | Intel/Solidigm 與 HGST 兩家屬性包 | SmartAttributeKnowledge 已收 Samsung/Seagate/WD/Kingston/Micron/SanDisk 六家（並行工作階段 2026-10-25 收口），**獨缺 Intel/Solidigm 與 HGST**；wd/seagate 等既有者不重做 |

### F ・ 唯讀檢測（2 項）

| ID | 項目 | 初判線索 |
|---|---|---|
| U10-TS-0185＋0187 | 磁碟塊尺掃描（512B～128K 分列）＋讀取熱區重複測（快取效應二次量測） | `ts.*` 動態家族已註冊；等級＝唯讀 |
| U10-TS-0191＋0192 | 感測器雙後端交叉驗證＋會話期時鐘漂移 | 重複量測之自動化；MT 域第 312 輪形狀可參照 |

## 已收口（截至第 331 輪，2026-11-15）

| 批次 | ID | 狀態 | 提交 |
|---|---|---|---|
| 一・WHEA 知識包 | WH-0005～0010 命名層、WH-0012／0013／0014／0015 | **已收口**（對照表層；逐欄節區解碼器留下一輪——界線寫在 `whea.sec.boundary`） | 本輪 |

## 建議批次（圈選參考）

| 批次 | ID | 為什麼一起做 |
|---|---|---|
| 一・WHEA 知識包 | WH 全部 12 項 | 一個解碼器檔＋一張 GUID 表；零新讀取面（節區位元組由呼叫端提供，沿第 326 輪界線） |
| 二・DMT 表 | VC 5 項 | 接在 Cta861.cs／EdidService 既有 EDID 解碼上 |
| 三・BCD 元素 | BD 8 項 | 同一次 `bcdedit /enum`（第 315 輪通道） |
| 四・WMI 補遺 | WM 4 項 | 每類一個查詢；全唯讀、秒級 |
| 五・SMART 補遺 | KP 1 項 | 純資料表，併入 SmartAttributeKnowledge 現有形狀 |
| 六・唯讀檢測 | TS 2 項 | 自負載＝0；兩項都是「量了就說」形狀 |

## 風險與邊界

- **WHEA 節區解碼**沿第 326 輪界線：事件 data 段外層包裝無可核出處，本圈**不拆 blob**——
  知識包與解碼器以純函式落地，節區位元組由呼叫端提供；沒有呼叫端傳入就是 NotApplicable 並寫明缺什麼。
- **BCD 只讀**：`/enum` 或登錄檔；fwbootmgr 在部分機器需要管理員權限，讀不到要顯示 `InsufficientPrivilege` 並說明。
- **WMI 補遺**的 legacy 類（PCMCIA／軟碟／數據機）常態 NotApplicable，「沒有」與「讀不到」分開。
- **DMT 表**只做對照不解碼原生時序：CVT-RB 生成式時序沿主目錄 NX 立場明文不做。
- **SMART 補遺**沿知識包三態規則：raw 語意按公開知識（drivedb.h／廠商手冊），無出處不出值。

## 與前三圈的關係

第一圈＝硬體事實（量得到）；第二圈＝呈現與操作（看得見）；第三圈＝可信度（說得清楚為什麼讀不到）；
第四圈＝**意義**（解碼表讓自述能被讀懂、開機元素的現值讓「誰在開機鏈上」可以被回答）。
