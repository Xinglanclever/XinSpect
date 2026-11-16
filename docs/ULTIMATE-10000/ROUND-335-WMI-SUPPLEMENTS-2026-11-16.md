# 第 335 輪：WMI 匯流排與資源補遺

> 日期 2026-11-16 · 範圍：U10-WM-0001／0008／0010／0012／0036 · 全唯讀

欄位依據：Microsoft Learn [Win32_AssociatedProcessorMemory](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-associatedprocessormemory)、[Win32_PortResource](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-portresource)、[Win32_IRQResource](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-irqresource)。

新增 `WmiSupplementFactsService` 五把鍵，透過 EvidenceLab 共用事實集提供桌面與 CLI 相同結果：

- `wmi.bus`：列舉 `Win32_Bus` 筆數與 BusType 原始碼分布。未知碼保留原值，不按不完整對照表猜名稱。
- `wmi.portres`：只報 `Win32_PortResource` 資源列與 Alias 標記，不匯出埠位址；列數不等於去重實體埠數。
- `wmi.irqres`：只報 `Win32_IRQResource` 資源列及 Hardware 標記分布；不把 WMI 列數說成唯一 IRQ 線或處理器向量數。
- `wmi.cpu.cache.assoc`：報 `Win32_AssociatedProcessorMemory` 的處理器／快取端點參照數與完整率。`Antecedent` 是快取、`Dependent` 是處理器；BusSpeed 保持來源欄位語意，不冒充 CPU 頻率或快取容量。
- `wmi.official.coverage`：Learn 硬體類別頁有 86 類；服務原始碼目前對 22 類有直接 WQL 查詢，64 類未直接查詢。這不是硬體支援率，也不把 SMBIOS／PnP／API 替代來源偽裝成直接 WMI 覆蓋。

每個類別使用明確欄位的唯讀 `SELECT`，各有 WmiGuard 3 秒上限。若其中一類逾時，後續類別查詢略過並標 ReadError，避免單一卡住的 WMI 提供者連續拖長啟動；前面已成功的類別結果保留。類別不存在標 NotSupported、存取遭拒標 InsufficientPrivilege，成功零筆仍是 Present 並明文表示零筆不等於硬體不存在。

本機只做過唯讀 schema／查詢路徑驗證；硬體列數不寫入此文件，避免把單一開發機快照當成產品基準。WM-0018 已由 `UsbTopologyService` 生產 USB 關聯，本輪不重複查詢。與 `IoPortFactsService` 的 POST 0x80 是不同讀取面：本輪 WMI 只呈現作業系統資源列，不讀寫 I/O 埠。

## 發現的規劃缺陷

U10-WM 的 36 個編號是策劃候選項，不是 Learn 頁全表。Round 335 已把完整 86 類清單與直接查詢集合做成 `wmi.official.coverage`，並由測試逐類對照 Services WQL；22/86 只代表直接查詢字面覆蓋，64 類未直接查詢不代表缺少替代硬體來源或平台不支援。

## 驗證

- 定向測試：WMI 官方清單、直接 WQL 逐類對帳、六態、端點方向等 7 項通過；README／HTML／覆蓋／社群文件等守門 62 項通過。
- WM-0036 完成後全套：`dotnet test` 8,139 通過、0 失敗、0 略過。
