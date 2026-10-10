# 曦覽 XinSpect ＋ My iLove PDF ・ 總索引

**2026-10-02** ・ 兩個專案的完整目錄、文件索引、程式碼結構

> **未納版控**；只 `git add` 明確路徑。
> **用途**：一份文件看完全部 —— 專案結構、所有文件、所有計畫、以及該刪什麼。

---

# 第 0 章 ・ 這份是什麼

| 你要找 | 看哪 |
|---|---|
| XinSpect 有什麼檔案、什麼結構 | 第 1 章 |
| MyILovePDF 有什麼 | 第 2 章 |
| **深度計畫（V7）的完整目錄** | 第 3 章 |
| 兩個專案的對照 | 第 4 章 |
| **該刪哪些檔** | 第 5 章 |

## 0.1 這份索引的自我驗證（含一次真實漂移）

寫完之後我用指令核對了每一個數字，**抓到一次漂移**：

| 宣稱 | 實際 | 原因 |
|---|---|---|
| ~~Views 77 個頁面~~ | **78** | **`FirmwareSecurityView.xaml` 於今天 19:47 由守護代理新增** |

> 我在本 session 開頭（約 00:56）量到 **77 頁** —— 現在是 **78**。
> **不是我在文件裡寫錯，是現實變了。**
>
> **這就是「手寫數字會漂移」最乾淨的示範：數字在寫下的那一刻沒錯，但它會過期。**
> 這也是 `PROGRAM-EVEREST-V7` 第 19 章「文件完整性契約」要解決的問題 ——
> **量化宣稱必須由內容計算，而計算必須能被重跑。**

**本次驗證結果**（全部用指令產出）：

| 項目 | 值 | 一致 |
|---|---|---|
| Services `.cs` | 296 | ✓ |
| Views 頁面 | 78 | ✓（已更正） |
| MyILovePDF 迭代條目 | 163 | ✓ |
| MyILovePDF corpus 檔數 | 8 | ✓ |
| V2–V6 合計 | 181,733 B | ✓（已更正） |

---

# 第 1 章 ・ XinSpect 專案索引

**路徑**：`C:\Users\Administrator\XinSpect`
**狀態**：`main`，2644 測試綠（2026-10-02 實跑），版本 2.1.0「Everest」

## 1.1 目錄結構

| 目錄 | 檔案數 | 內容 |
|---|---|---|
| **Services** | **296** | 服務層（見 1.3） |
| **Views** | 157 | **78 個頁面**（XAML ＋ code-behind） |
| **Bridge** | 468 | net48 XTU 橋接程式（不納主專案編譯） |
| **BlueSquadron** | 147 | 安全守護進程 ＋ **核心驅動 C 源碼** |
| **Tests** | 13894 | 測試（含 `obj/` 建置產物；實際測試檔 173） |
| **XsRegProbe** | 80 | 自寫白名單驅動（C ＋ 建置手冊） |
| **Controls** | 42 | 自訂控件（Badge、五行輪、核心液柱…） |
| **Models** | 23 | 資料模型（`HardwareModels`、`DeviceIcons`、`AppInfo`…） |
| **ViewModels** | 14 | MVVM |
| **docs** | 8 | `AGENT_CONTEXT`、`feature-backlog`、`superpowers/plans` |
| **Nav** | 7 | 導覽、`HelpCatalog`、`ChangelogCatalog` |
| **Dialogs** | 6 | 對話框（含 `ShrineWindow` 拜神） |
| **Assets** | 6 | 圖示、音效 |
| **cloudflare** | 4 | AI proxy Worker |
| **Themes** | 1 | 主題 |

**檔案類型**：`.cs` 670 ・ `.xaml` 98 ・ `.png` 94 ・ `.exe` 36 ・ `.md` 30 ・ `.dll` 29 ・ `.ps1` 13

## 1.2 文件索引（根目錄）

### 深度計畫（7 版，只有 V7 該留）

| 檔案 | 大小 | 狀態 |
|---|---|---|
| **PROGRAM-EVEREST-V7-2026-10-02.md** | 37,755 B | **現行（機器驗證通過）** |
| PROGRAM-EVEREST-V6-2026-10-02.md | 37,077 B | 被取代 |
| PROGRAM-EVEREST-V5-2026-10-02.md | 37,898 B | 被取代 |
| PROGRAM-EVEREST-V4-2026-10-02.md | 39,254 B | 被取代 |
| PROGRAM-EVEREST-V3-2026-10-02.md | 35,380 B | 被取代 |
| PROGRAM-EVEREST-V2-2026-10-02.md | 32,124 B | 被取代 |

**六版合計 219 KB** —— **這就是「目錄很大」的原因。**

### 制度文件

| 檔案 | 大小 | 內容 |
|---|---|---|
| `ROADMAP-給GLM.md` | **54,762 B** | 八輪願景彙整（最大的一份） |
| `HANDOFF-ZCODE-ITER20-2026-10-02.md` | 18,396 B | Z Code 接手 20 輪的移交 |
| `HANDOFF-DEEP-REGISTER-2026-10-02.md` | 16,446 B | **深層暫存器計畫**（P0–P5） |
| `CODE-REVIEW-2026-09-29.md` | 12,594 B | 七代理碼審（P0/P1/P2 全清單） |
| `TASK-GLM.md` | 11,001 B | 工作指令書（Opus 4.8 撰寫） |
| `HANDOFF-1.3.2.md` | 10,422 B | 舊交接 |
| `REPORT-GLM.md` | 7,762 B | GLM 回報 |
| `HANDOFF-2026-09-30.md` | 6,001 B | 舊交接 |
| `HANDOFF-2026-09-29.md` | 5,966 B | 舊交接 |
| `PROGRAM-2026-09-29.md` | 4,390 B | **三底座 ＋ 願景（V7 有引用，保留）** |
| `HANDOFF-2026-10-01.md` | 3,756 B | 舊交接 |
| `HANDOFF-2026-08-30.md` | 32,053 B | 舊交接（最大的一份） |

### 其他

`README.md`（17 KB）、`1.1.zip`（72 MB，**只有發佈產物、零原始碼**）、7 張頁面截圖、`cpuz_probe.txt`（486 KB）

## 1.3 程式碼結構（Services：296 檔）

| 子目錄 | .cs | 內容 |
|---|---|---|
| （第一層） | **218** | 全部服務 |
| **DeepBench** | **52** | 深測中心（38 項矩陣、Interop、Engines） |
| Overclock | 9 | 超頻（XTU／NVML／NVAPI） |
| BlueSquadron | 7 | 安全守護進程 |
| ExternalSensors | 5 | 外部感測器 |
| Gpu | 4 | GPU 深度 |
| StressBridge | 1 | 壓力橋接 |

**第一層 218 檔的關鍵分類**：

| 類別 | 代表檔 |
|---|---|
| **誠實契約** | `CpuMsrFacts`、`PlatformSecurityMsrFacts`、`SmbiosFacts`、`VerifyFacts` |
| **對帳與矛盾** | `VerifyRules`、`VerifyThresholds`、`MachineVerdict`、`SpdConsistencyAuditService` |
| **驅動與特權** | `WinRing0Bridge`、`DriverMmioReader`、`RawRegisterSnapshot*`、`DeepAccessService` |
| **硬體解碼** | `SmbiosService`、`SpdDecoder`、`NvmeLogDecoder`、`EdidService`、`CpuIdService` |
| **晶片組／韌體** | `ChipsetSecurityService`、`SpiFlashService`、`AcpiTableService`、`FirmwareService` |
| **深度服務** | `TopDownService`、`RdtsService`、`UncorePmuService`、`TimerFoundationService` |
| **驗機與證據** | `EvidenceLabService`、`EvidenceTimelineService`、`HardwareSnapshotService` |
| **安全姿態** | `SecurityPostureService`、`PlatformTrustService`、`DriverAuditService` |
| **在地化** | `LanguageService`、`ZhTerms`（138 詞條） |

---

# 第 2 章 ・ MyILovePDF 專案索引

**路徑**：`C:\Users\Administrator\MyILovePDF`
**狀態**：`main`，v1.9.152，596 測試，**163 條迭代記錄**（v1.0.0 → v1.9.152，約 2 天）

## 2.1 目錄結構

| 目錄 | 檔案數 | 內容 |
|---|---|---|
| **Tests** | 447 | 測試（含 obj） |
| **Services** | 21 | 見 2.3 |
| **corpus** | **8** | **7 個真實 PDF ＋ README** |
| **docs** | 5 | 見 2.2 |
| Assets | 1 | 圖示 |
| Models | 1 | — |
| tools | 1 | — |

**檔案類型**：`.cs` 151 ・ `.md` 7 ・ **`.pdf` 7** ・ `.exe` 3

## 2.2 docs 索引 —— **五份，全部納版控** ★

| 檔案 | 大小 | 內容 |
|---|---|---|
| **ITERATIONS.md** | **31,341 B** | **163 條迭代日誌**（版本／內容／測試數／commit hash） |
| HANDOFF.md | 16,223 B | AI 移交文件（含 git author、驗證命令） |
| **TOOLMAP.md** | 10,129 B | **全工具有損性地圖（55 工具）** ★ |
| ROADMAP.md | 5,449 B | 超級目標 ＋ 每輪鐵律 ＋ 終止條件 |
| **CORPUSNOTES.md** | 5,227 B | **真檔巡檢筆記**（初勘 12 工具、二勘 34 工具） |

> **對照 XinSpect**：這五份**全部在版控裡**，而 XinSpect 的制度文件全部未追蹤。
> **TOOLMAP 的「有損性地圖」正是先前建議的那一條 —— 它已經做了。**

## 2.3 Services（21 檔）

`AppSettings`、`CrashReporter`、`FileCollector`、`ImageModes`、`KeyboardShortcuts`、`LibreOfficeService`、`LogExport`、`OcrService`、`PageDropHints`、`PageOps`、`PageRestoreOps`、`PageSelectionOps`、`PdfOps`、`RecentFiles`、`RunSummary`、`ThemePalettes`、`ThumbnailQuality`、`ThumbnailRenderer`、`ToolCatalog`、`ToolSearch`、`UpdateChecker`

## 2.4 corpus（8 檔）

`中國之命運.PDF`（3.5 MB）、`俺の部屋には天使がいるシリーズ [DL版].pdf`（44 MB）、`ws.pdf`（100 MB）、`comic_translator_images.pdf`（35 MB）、`钢铁雄心4控制台命令_超高质量 (1).pdf`（1 MB ×2）、`DeepSeek-群聊政史辩论总结.pdf`（2.2 MB）、`README.md`

> **隱私紅線**：`corpus/README.md` 寫「測試一律不得引用 corpus」。

---

# 第 3 章 ・ 深度計畫（V7）完整目錄

**檔案**：`PROGRAM-EVEREST-V7-2026-10-02.md`（716 行，機器驗證通過）

## 3.1 二十章

| 章 | 標題 |
|---|---|
| 0 | 前言：這份是校訂版 |
| 1 | 前提與契約 |
| 2 | 驅動策略（三後端） |
| **3** | **A 類：能力層（55 項）** |
| **4** | **M 類：機制（10 項）** |
| **5** | **T 類：測試方法（5 項）** |
| **6** | **D 類：五個橫向維度** |
| 7 | 量測方法學 |
| 8 | 對標：RWEverything |
| 9 | 看齊清單（五個缺口） |
| 10 | 寫入能力決策 |
| **11** | **工作包（WP1–WP51）** |
| 12 | 誠實契約（不可違反） |
| 13 | 成功定義（可量測） |
| 14 | 風險與限制 |
| 15 | 權威參考 |
| 16 | 執行順序 —— 四條線 |
| 17 | 世界第一的條件 |
| **18** | **執行手冊 —— 怎麼從今天走到最強** |
| **19** | **文件完整性契約** |

## 3.2 A 類能力（55 項，五個區塊）

| 區塊 | 範圍 | 內容 |
|---|---|---|
| **CPU 與平台** | A1–A10 | MSR、溫度節流、功耗、安全韌體 MSR、快取/Uncore、PMU 全家桶、MSR 寫入、晶片組安全、SPI/UEFI、CMOS/BIOS 設定 |
| **記憶體、I/O 與匯流排** | A11–A20 | 記憶體控制器/DRAM、SMBus/PECI/VRM/EC、PCIe/IOMMU、ACPI/錯誤注入、周邊匯流排、網路、電源平面、帶外管理、CXL、時間同步 |
| **儲存與 GPU** | A21–A30 | NVMe/ATA、儲存控制器、RAID OOB、企業儲存網路、GPU 核心、GPU 顯示、GPU 訊號層、主機板解碼、筆電平台、周邊裝置 |
| **系統、安全與鑑識** | A31–A40 | 安全深度、系統核心、系統軟體層、虛擬化、鑑識時間軸、記憶體攻擊面、SMM/ME/DCI 觀測、開機階段、對帳矛盾、可驗證性 |
| **產品面與新能力** | A41–A55 | 報告視覺化、腳本化 API、多機器、查詢語言、規則引擎、引導式診斷、審計日誌、公開規格、corpus、生態、**ESG**、**資產生命週期**、**硬體變更通知**、**效能預算**、**SDK＋模擬器** |

## 3.3 M 類機制（10 項）

`M1` SpecRef ・ `M2` 合成產生器＋Fuzzing ・ `M3` 正確性論證 ・ `M4` 可重現包＋時間戳 ・ `M5` 報告與核驗器 ・ `M6` 規則引擎＋市集 ・ `M7` 引導式診斷 ・ `M8` 審計日誌 ・ `M9` 二手平台 API＋保固 ・ `M10` SDK＋模擬器＋資料主權

## 3.4 T 類測試方法（5 項）

`T1` Property-based（FsCheck）・ `T2` Mutation（Stryker.NET）・ `T3` Differential ・ `T4` 形式驗證 ・ `T5` 可測性設計

## 3.5 D 類維度（5 個）

`D1` 閉環 ・ `D2` 知識庫 ・ `D3` 預測 ・ `D4` 可稽核的正確性 ・ `D5` 可證明性

## 3.6 工作包（51 個，四條線）

| 線 | WP | 數量 |
|---|---|---|
| **驗機線** | WP1–WP9 | 9 |
| **廣度線** | WP10–WP26 | 17 |
| **平台線** | WP27–WP43 | 17 |
| **標準線** | WP44–WP51 | 8 |

## 3.7 里程碑

`G1` 三後端＋矛盾矩陣 ・ `G2` SMBus＋CMOS ・ `G3` 報告＋API ・ **`G4` SpecRef＋Fuzzing＋測試方法學** ・ **`G5` 公開規格＋corpus** ・ `G6` 其餘

> **G1–G3 ＝ 世界級的工具。G4–G5 ＝ 世界第一的候選。**

---

# 第 4 章 ・ 兩專案交叉對照

| | **XinSpect** | **MyILovePDF** |
|---|---|---|
| 定位 | 硬體診斷儀器 | PDF 工具箱 |
| 語言／框架 | C# / WPF / .NET 10 | C# / WPF / .NET 10 |
| 歷史 | 約 6 週（08-19 起） | **約 2 天**（09-30 起） |
| 版本 | 2.1.0「Everest」 | **1.9.152** |
| 迭代 | 約 250 commit | **163 條迭代記錄** |
| 測試 | **2644** | 596 |
| 頁面／工具 | **78 頁** | 55 工具 |
| 核心驅動 | **有**（BlueSquadron ＋ XsRegProbe） | 無 |
| **真實樣本** | ❌ 極少（僅 SMBIOS/SPD fixture） | **✅ corpus/ 7 檔 ＋ 巡檢筆記** |
| **制度文件版控** | ❌ **全部未追蹤** | **✅ docs/ 五份全在版控** |
| 迭代日誌 | ❌ | **✅ ITERATIONS.md（163 條）** |
| 有損性地圖 | ❌ | **✅ TOOLMAP.md** |
| CI | ❌ | ✅ GitHub Actions |
| 自動更新 | ❌ | ✅ |
| 版本 → commit 對應 | ❌（tag 與資料夾對不上） | **✅（每版記 hash）** |

## 結論：同一台機器上的兩套方法論

| | XinSpect | MyILovePDF |
|---|---|---|
| 強 | **深度**（量測、ring0、驗機） | **紀律**（可追溯、可驗證、可交接） |
| 弱 | **來歷管理** | **功能深度** |

> **XinSpect 缺的那套，答案就在隔壁資料夾裡。**

---

# 第 5 章 ・ 建議的檔案處置

## 5.1 XinSpect —— 可刪（省 219 KB ･ 六個檔）

| 檔案 | 大小 | 為什麼可刪 |
|---|---|---|
| PROGRAM-EVEREST-V2 ～ V6（5 份） | **181,733 B** | **全部被 V7 取代**（V7 是校訂版，V6 的問題已列在 V7 第 0 章） |
| 舊 HANDOFF（1.3.2、08-30、09-29、09-30、10-01） | 58 KB | 已被後續取代（**建議先確認沒有獨有內容**） |

```powershell
# 只刪已被 V7 明確取代的五版
Remove-Item -LiteralPath `
  'C:\Users\Administrator\XinSpect\PROGRAM-EVEREST-V2-2026-10-02.md',
  'C:\Users\Administrator\XinSpect\PROGRAM-EVEREST-V3-2026-10-02.md',
  'C:\Users\Administrator\XinSpect\PROGRAM-EVEREST-V4-2026-10-02.md',
  'C:\Users\Administrator\XinSpect\PROGRAM-EVEREST-V5-2026-10-02.md',
  'C:\Users\Administrator\XinSpect\PROGRAM-EVEREST-V6-2026-10-02.md'
```

## 5.2 XinSpect —— 應保留

| 檔案 | 理由 |
|---|---|
| `PROGRAM-EVEREST-V7-2026-10-02.md` | 現行計畫 |
| `PROGRAM-2026-09-29.md` | 三底座 ＋ dbx 2026-06 的來源（V7 有引用） |
| `ROADMAP-給GLM.md` | 八輪願景，內容最多 |
| `CODE-REVIEW-2026-09-29.md` | 審查權威（P0/P1/P2） |
| `HANDOFF-DEEP-REGISTER`、`HANDOFF-ZCODE-ITER20` | 最近的深度進度 |
| `REPORT-GLM.md`、`TASK-GLM.md` | 多模型協作的紀錄 |
| `1.1.zip` | **唯一的 1.1 時代留存**（雖然只有發佈產物） |

## 5.3 建議搬進版控（XinSpect）

> **MyILovePDF 的五份 docs 全在版控 —— XinSpect 的制度文件全部沒有。這是最大的落差。**

建議至少納入：`ROADMAP-給GLM.md`、`CODE-REVIEW-2026-09-29.md`、`PROGRAM-2026-09-29.md`、`PROGRAM-EVEREST-V7-2026-10-02.md`、`HANDOFF-DEEP-REGISTER-2026-10-02.md`

```powershell
cd C:\Users\Administrator\XinSpect
git add PROGRAM-EVEREST-V7-2026-10-02.md
git add PROGRAM-2026-09-29.md
git add CODE-REVIEW-2026-09-29.md
git add HANDOFF-DEEP-REGISTER-2026-10-02.md
# 只 add 明確路徑，絕不 git add .
```

---

_建立：2026-10-02。索引掃描涵蓋兩專案全部目錄，數字皆由指令產出。_
