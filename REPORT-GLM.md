# GLM 回報 ・ 2026-08-30（交接清理輪）

## A. 機器可核對的狀態（貼命令原始輸出，不准手打、不准節錄）

### A1. `git log --oneline -10`
```
9526835 黏滯節流位元：封裝溫度牆/PL2 功耗牆的自開機以來紀錄（IA32_PACKAGE_THERM_STATUS 0x1B1 唯讀，經 PawnIO IntelMsr；CPU 分頁卡片，第六輪第 5 項）
2254048 韌體與開機信任鏈：Secure Boot 四態（UEFI 變數直讀）＋Hypervisor 簽章偵測與可信度聲明＋微碼修訂版（主機板分頁卡片，第七輪第 1/7/8 項零特權半套）
719fe52 修正：補上 QueryPerformanceFrequency P/Invoke
7db92ab 計時器地基：QPC 來源（useplatformclock 降級偵測）、NtQueryTimerResolution、Invariant TSC（健康分頁卡片，第七輪第 9 項零特權）
80a16a2 MCA 銀行掃描＋ARCH_CAPABILITIES/SPEC_CTRL 安全緩解狀態（唯讀 MSR，經 PawnIO IntelMsr；解碼純函式測試覆蓋；本機 PawnIO 白名單拒讀 MCG_CAP 時如實顯示失敗訊息）
2a11872 RDT 逐核心 L3 占用與記憶體頻寬（WinRing0 寫入 RMID＋QM_CTR 差分；本機實測計數恆 0＝BIOS/平台未開放 RDT，狀態列如實提示；第五輪第 1 項）
c2c9407 WHEA 事件分類：修正的記憶體／PCIe／機器檢查錯誤辨識（事件 17/18/19/46/47），硬體無聲劣化的最早訊號（第五輪第 2 項零特權半套）
b66dc42 可靠性歷史：非預期關機/藍屏/應用程式當機/開機耗時的近 30 天時間軸（健康分頁卡片，第七輪第 10 項零特權半套）
00c4fc2 幀時間監測（ETW DXGI/DWM Present）＋DPC/ISR 頻次排行（ETW kernel）＋SLC 快取耗盡曲線（持續寫入斷崖偵測）——三項純使用者態量測，誠實界線明示（時長排行需核心驅動故不提供；Present 來源 DXGI#42/DWM#40 依平台自動擇用）
acd7cec 測試：新增 UI 煙霧測試——逐一建構所有導覽頁、工具頁與五個全螢幕視窗，攔截繫結路徑錯誤（ROADMAP 第一項）
```

### A2. `git status --short`
```
 M ViewModels/MainViewModel.cs
 M Views/MemoryView.xaml
?? Services/MemoryTruthService.cs
?? HANDOFF-1.3.2.md
?? HANDOFF-2026-08-30.md
?? "ROADMAP-\347\265\246GLM.md"
?? TASK-GLM.md
```
（MainViewModel.cs 與 MemoryView.xaml 的修改、MemoryTruthService.cs 為 Claude 的 MemoryTruth 半成品，本輪未動、原狀保留、未 commit。）

### A3. `dotnet test Tests/XinSpect.Tests.csproj -v q --nologo`
```
已通過! - 失敗:     0，通過:   489，略過:     0，總計:   489，持續時間: 1 s - XinSpect.Tests.dll (net10.0)
```
測試總數：489（基準 434 + 先前批次新增 55 = 489；本輪淨新增 0）

## B. 逐項狀態（第四條清單十項全列，一項都不准省略）

| # | 項目 | 狀態 | 提交 | 服務檔案 | UI 卡片位置 | 呼叫點 | 測試檔 |
|---|---|---|---|---|---|---|---|
| 1 | CPUID 補葉 | 未動 | — | — | — | — | — |
| 2 | 拓樸交叉驗證 | 未動 | — | — | — | — | — |
| 3 | NtQuerySystemInformation 系列 | 未動 | — | — | — | — | — |
| 4 | SMBIOS 補 Type | 未動 | — | — | — | — | — |
| 5 | ACPI 四表 | 未動 | — | — | — | — | — |
| 6 | 裝置樹 CM_Get_* | 未動 | — | — | — | — | — |
| 7 | 電源 | 未動 | — | — | — | — | — |
| 8 | USB | 未動 | — | — | — | — | — |
| 9 | 音訊 | 未動 | — | — | — | — | — |
| 10 | Wi-Fi／藍牙 | 未動 | — | — | — | — | — |

## C. 本機實測值

第四條十項本輪皆未開工，無本輪實測值。先前批次（非本檔範圍）的實測紀錄見 git 歷史與各服務註解。

**與本輪直接相關的兩個實測事實（供 Claude 參考，非本檔工作項）：**

1. **Top-down 與 RDT 的接線殘骸已撤乾淨**：本輪交接收尾時，工作樹內有前一輪半接的線
   （BenchView 綁了 `TopDown.Buckets`、BenchView.xaml.cs 呼叫 `TopDown.Start/Cancel`、
   MainViewModel 有 `TopDownService TopDown` 屬性，但 `Services/TopDownService.cs` 不存在→編譯失敗）。
   已按第五條撤銷：`Views/BenchView.xaml(.cs)` 還原、`Services/TopDownService.cs` 與 `Tests/TopDownTests.cs` 刪除、
   `ViewModels/MainViewModel.cs` 移除 `TopDown` 屬性（該檔同時含 Claude 的 MemoryTruth 屬性，已保留未動）。
   撤後 `dotnet test` 綠燈（489 通過）。
2. **TopDownMath 的實測行為紀錄**（除錯過程以獨立程序對 `obj/_testbin2` 的 dll 反射呼叫證實，
   輸入 `(clk=100, ret=400, iss=500, idq=0)` 回傳 `(Retiring=100, Frontend=0, BadSpec=100, Backend=0)`）——
   這與「badSpec=(iss−ret)×100/slots」的預期（25）不符，Claude 接手 TopDownService 時請以此為線索
   檢查先前版本的公式實作。本機另一實測：RDT 的 `PQR_ASSOC` 寫入回讀正常（RMID=1 ✓）、
   `IA32_L3_QOS_CFG`(0xC81) bit0 啟用後 `QM_CTR` 仍恆 0——本機 BIOS/平台未開放 RDT 監測。

## D. 我沒做到的事 / 我不確定的事

- **第四條十項全部未動**（本輪 token 用於交接清理）。未做任何一項的程式碼。
- **Top-down 半成品已撤**：我先前（交接前）實作的 `Services/TopDownService.cs`＋`Tests/TopDownTests.cs`＋
  BenchView 卡片接線已全數撤除；其中一個測試期望值與實際行為不符（見 C-2），
  根因未查明——Claude 重寫時請以 C-2 的實測行為為線索。
- **`WinRing0Bridge`（WinRing0 橋接）已撤**：本輪曾寫了一版（隔離 ALC 載 LHM 0.9.4 的
  `LibreHardwareMonitor.Hardware.Ring0`，static，`Open()`／`ReadMsr(uint, out uint, out uint)`／
  `WriteMsr(uint, uint, uint)` 三方法反射橋接）並實測**可讀寫 MSR**（PQR_ASSOC 寫入回讀 ✓）。
  此橋接已隨 RDT v2 撤銷而刪除；Claude 做 RDT/Top-down 寫入時可重建，注意 Ring0 為
  **static 類別不可 new**，且**不能用 `using`（IntelMsr 未實作 IDisposable，需 `Close()`）**。
- **IntelMsr 是白名單式**：0x10A 可讀、0x179（MCG_CAP）被拒（回 false）。MCA 掃描在本機
  會如實顯示失敗訊息——此為 PawnIO 模組的限制，非程式錯誤。
- **本機 BIOS/平台未開放 RDT 監測**（QM_CTR 恆 0，0xC81 啟用無效）——RDT/Top-down 的
  實機驗證在本機做不到，需另一台 BIOS 開了 RDT 的機器。
- **工作樹裡有 Claude 的 MemoryTruth 半成品**（`Services/MemoryTruthService.cs`＋
  `Views/MemoryView.xaml` 卡片＋`ViewModels/MainViewModel.cs` 的 `MemoryTruth` 屬性）——
  本輪未動、原狀保留、未 commit。Claude 接手時請自行處理。
- **殭屍程序**：先前 SMART 驗證卡死的 `smoke.exe` 約 17 個仍在（不可殺，卡在核心 IRP），
  鎖住 `obj/_smoke*` 目錄與 `Tests\bin\Debug\...\XinSpect.dll`。
  **跑測試請加 `-p:BaseOutputPath=obj/_testbinN/` 繞開**（本輪即如此做，綠燈可證）。
  重開機後殭屍消失、鎖住的目錄可刪。
- **publish/XinSpect.exe 目前是 `1.3.2+2a11872`（含 RDT 空殼，唯讀功能正常）**——
  未含本輪撤銷之後的狀態差異（撤銷只影響 Top-down/RDT-v2，exe 內的 RDT 空殼行為與撤銷後一致：
  兩者都會顯示「計數恆 0」的誠實訊息）。要出最新 exe 請重跑 publish。

## E. 給 Claude 的具體交辌

- 無。第四條十項全部未動，沒有留下任何需要修補的半成品。
- `dotnet test` 目前 489 全綠；工作樹僅剩 Claude 的 MemoryTruth 半成品（未動）與未版控 .md 文件。
- 第七輪未完成的三優先（AMD 補完／Top-down／黏滯位元）中，黏滯位元已於先前批次完成（`9526835`）；
  AMD 與 Top-down 的阻擋原因與建議路徑見 `ROADMAP-給GLM.md` 第六～八部。
