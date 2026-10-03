# G6 全線路線圖（V7 廣度線＋平台線，2026-10-03 立）

> 依「安全、驗機價值、本機可驗證」排序。每批一個主題、一輪一提交、全套綠才算數。
> 需要網路的（NTP、RFC 3161）與涉及寫入／風險的（Rowhammer、EC 直寫）一律加同意閘門或誠實延後。

## 批次序列

| 批次 | 工作包 | 主題 | 狀態 |
|---|---|---|---|
| ITER32 | WP14-①② | TPM 2.0 量測開機鏈：PCR 0–7（SHA-256 bank）＋TCG 事件 log 摘要（Windows TBS 仲介，零核心風險） | 本批 |
| ITER33 | WP21＋WP22-① | NUMA 完整拓撲（GetLogicalProcessorInformationEx 純解碼）＋記憶體攻擊面的誠實面（SMBIOS ECC 事實、TRR 只標風險不施測） | 待開 |
| ITER34 | WP13＋WP23-① | 網路（卸載實際狀態 WMI、Wi-Fi Native API RSSI/頻道）＋音訊（WASAPI 裝置完整格式） | 待開 |
| ITER35 | WP41＋WP42 | ESG 能耗（RAPL PKG_ENERGY_STATUS 量測，MSR 唯讀）＋資產生命週期／硬體變更通知（快照差分自動化） | 待開 |
| ITER36 | WP28＋WP36 | 規則引擎市集（使用者自訂規則 JSON＋驗證器）＋審計日誌（雜湊鏈不可否認） | 待開 |
| ITER37 | WP35＋WP26-② | 事實查詢語言（對 CLI JSON 的可組合管線）＋開機階段計時（事件記錄派生） | 待開 |
| ITER38 | WP19＋WP11 | CXL（ACPI CEDT 掃描——本機無則如實標）＋筆電平台（本機桌面→誠實 NotApplicable 示範） | 待開 |
| ITER39 | WP12＋WP18 | 儲存控制器（RAID 偵測→誠實標）＋帶外管理（IPMI KCS 0xCA2 探測→誠實標） | 待開 |
| ITER40 | WP17＋WP25 | 主機板解碼深化（SuperIO HWM 感測器）＋時間同步（HPET/PM timer/TSC 互校，usermode 部分） | 待開 |
| 之後 | WP33/34/37/38/39/43/27 | 量測方法學、API server、部署、訊號層、資料主權、效能預算、PMU（需沙箱驗證） | 規劃中 |

## 誠實界線（全線適用）

- **EC 直寫（WP31）**：Windows ACPI 驅動共享 EC，直寫 0x62/0x66 有交易衝突風險——**風險評估已完成（docs/EC-RISK-ASSESSMENT.md，ITER44）**：結論為不實作任何 EC 埠存取（「唯讀」亦含命令埠寫入），重開條件見該文件。
- **Rowhammer／記憶體攻擊面施測（WP22）**：V7 §14 明列「會弄壞東西」——只標風險與 ECC 現況，施測需專用機器＋明確同意。
- **網路觸及（WP13 部分已 usermode、WP40 RFC 3161、NTP）**：出網前加同意閘門。
- **PMU（WP27）**：V7 明列「需先驗證沙箱」，延後。
