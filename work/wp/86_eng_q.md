## 第五十七章　中英術語對照

### 57.1 本章的用途

本專案的文件與程式碼混用中文與英文術語。本章提供對照表，用於跨語言的閱讀與溝通（例如以英文回報問題、或在英文版文件中查找對應概念）。

對照的原則為：**技術術語以規格書或業界通用的英文為準**（例如 MSR、SPD、SMBIOS），而**本專案自訂的概念以中文命名並提供英文釋義**。因此本表分為兩部分。

### 57.2 規格與業界術語

| 中文 | 英文 | 說明 |
|---|---|---|
| 型號特定暫存器 | Model-Specific Register（MSR） | 處理器的控制與狀態暫存器 |
| 設定空間 | Configuration Space | PCI／PCIe 裝置的標準化暫存器空間 |
| 記憶體映射輸出入 | Memory-Mapped I/O（MMIO） | 以記憶體位址存取的裝置暫存器 |
| 序列存在偵測 | Serial Presence Detect（SPD） | 記憶體模組上的組態 EEPROM |
| 溫度感測器 | Temperature Sensor on DIMM（TSOD） | 記憶體模組上的溫度感測器 |
| 自我監控分析與報告技術 | Self-Monitoring, Analysis and Reporting Technology（SMART） | 儲存裝置的健康屬性 |
| 系統管理基本輸出入系統 | System Management BIOS（SMBIOS） | 韌體提供的系統描述結構 |
| 進階組態與電源介面 | Advanced Configuration and Power Interface（ACPI） | 韌體提供的平台描述表 |
| 統一可延伸韌體介面 | Unified Extensible Firmware Interface（UEFI） | 現代平台的開機韌體介面 |
| 視窗管理instrumentation | Windows Management Instrumentation（WMI） | 作業系統的管理資料介面 |
| 事件追蹤 | Event Tracing for Windows（ETW） | 作業系統的事件追蹤機制 |
| 延遲程序呼叫 | Deferred Procedure Call（DPC） | 驅動程式的延遲執行機制 |
| 執行期平均功率限制 | Running Average Power Limit（RAPL） | 處理器的能耗計數 |
| 效能監控單元 | Performance Monitoring Unit（PMU） | 處理器的效能計數器 |
| 資源導向技術 | Resource Director Technology（RDT） | 快取與記憶體頻寬的配置技術 |
| 可調整大小的基底位址暫存器 | Resizable BAR（ReBAR） | PCIe 裝置的 BAR 大小協商 |
| 進階錯誤報告 | Advanced Error Reporting（AER） | PCIe 的錯誤報告機制 |
| 信任平台模組 | Trusted Platform Module（TPM） | 平台的信任根 |
| 平台組態暫存器 | Platform Configuration Register（PCR） | TPM 中的量測值暫存器 |
| 安全開機 | Secure Boot | 開機映像的簽章驗證 |
| 全域加密 | Total Memory Encryption（TME） | 記憶體加密 |
| 安全加密虛擬化 | Secure Encrypted Virtualization（SEV） | AMD 的機密運算技術 |
| 信任域擴充 | Trust Domain Extensions（TDX） | Intel 的機密運算技術 |
| 資料中心可設定性 | Compute Express Link（CXL） | 高速裝置互連 |
| 非統一記憶體存取 | Non-Uniform Memory Access（NUMA） | 多節點記憶體架構 |
| 韌體介面表 | Firmware Interface Table（FIT） | 平台韌體階段的位址描述 |
| 韌體效能資料表 | Firmware Performance Data Table（FPDT） | 開機階段的計時資料 |
| 帶外管理 | Out-of-Band Management | 獨立於作業系統的管理通道 |
| 易受攻擊驅動 | Bring Your Own Vulnerable Driver（BYOVD） | 以合法但脆弱的驅動進行的攻擊 |

### 57.3 本專案自訂術語

| 中文 | 英文對應 | 說明 |
|---|---|---|
| 事實 | fact | 一次觀測的結果，見 3.1 |
| 事實鍵 | fact key | 事實的唯一識別碼 |
| 可信層級 | trust level | `Measured`／`Reported`／`Derived`／`Unknown`，見 3.3 |
| 可用性 | availability | `Present`／`NotSupported`／`InsufficientPrivilege`／`ReadError`／`NotApplicable` |
| 三態誠實 | three-state honesty | 讀不到必須說出原因的原則，見 2.1 |
| 對帳 | reconciliation | 跨來源的交叉檢查 |
| 判定 | verdict | 相符／矛盾／無法判定 |
| 逐碟 | per-disk scope | 規則的作用域，見 8.3 |
| 明文豁免 | explicit exemption | 刻意不對帳並附理由，見 10.3 |
| 覆蓋申報 | coverage report | 對每個事實鍵申報其對帳狀態 |
| 動態家族 | dynamic key family | 成員由機器決定的鍵家族，見 10.2 |
| 殭屍前綴 | zombie prefix | 已登記但無生產者的家族前綴 |
| 守門測試 | guard test | 驗證不變式的測試 |
| 探針接線死路 | dead probe wiring | 產生但未被消費的探針結果 |
| 快照 | snapshot | 一次完整事實蒐集的結果 |
| 時間膠囊 | time capsule | 快照的序列化容器 |
| 硬體變更時間軸 | hardware change timeline | 兩份快照的比對結果 |
| 判斷的登記表 | decision registry | 把需要人為決定的分岔登記為資料 |
| 寫入閘門 | write gate | 集中寫入操作的模組 |
| 降級 | graceful degradation | 來源失敗時的部分結果保留 |
| 不可見停頓 | invisible stall | 無法歸因的系統停頓，見 39.3 |
| 自驗量測 | self-validating measurement | 回報前先驗證量測機制 |
| 可重現基準 | reproducible baseline | 由掃描產生的統計數字 |
| 規範化 | canonical | 同一組事實產生相同的序列化輸出 |
| 選用功能 | optional feature | 作業系統的可安裝功能 |

### 57.4 使用上的注意

**中文術語的穩定性。** 本專案自訂術語的中文名稱以本表為準。程式碼中的標識符使用英文，但其對應的概念以中文命名。

**英譯的性質。** 本表的英文對應為**釋義**而非正式譯名。技術溝通時建議直接使用英文術語（第二節所列的規格術語），避免因翻譯產生歧義。

**混用的必然性。** 部分術語在中文語境中沒有慣用的對應（例如 `Measured` 的層級語意），此時以英文原文加中文說明的方式表達。此混用並非疏漏，而是技術領域的常態。

---
