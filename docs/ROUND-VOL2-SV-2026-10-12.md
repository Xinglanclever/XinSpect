# Vol2 批次 D（G4 伺服器角色）第一波 — 2026-10-12（並行開發：本輪未動版號）

> **圈選範圍**：`docs/PROGRESS-PLAN-2026-10-11.md` 的 **G4（伺服器角色 10 項）** 中，與另一條並行工作線
> （SV-001 角色盤點／SV-011 Server Core／SV-014 合規摘要）**不重疊**的深層項。
> **本波**：**SV-013（Server 角色服務）＋SV-012（授權／啟用）＋SV-006／SV-007（檔案／列印共用）**。
> **一句話**：把「這台機器扮演哪些伺服器角色」從安裝清單，推進到**實際運作的服務、授權狀態與共用**，
> 三個面向各一把事實鍵，讀不到一律三態。
> **本輪在真機（Windows Server 2025 Datacenter／build 26100）端到端驗證通過。**

---

## 1. 交付一覽

| 項 | 交付 | 事實鍵 | 來源 |
|---|---|---|---|
| SV-013 | 角色服務盤點：固定角色清單（18 個）中哪些服務存在、是否執行中 | `sv.roles.services` | WMI `Win32_Service` |
| SV-012 | 授權與啟用：版本描述＋授權狀態枚舉對照（**刻意不讀產品金鑰**，L3 禁項） | `sv.licensing` | WMI `SoftwareLicensingProduct` |
| SV-006／SV-007 | 檔案與列印共用：磁碟／印表共用計數與**共用名稱**（**刻意不披露本機路徑**） | `sv.shares` | WMI `Win32_Share` |

解碼器：無（純 WMI 讀取，非位元組版面解碼，故不需 `[SpecRef]`）。

## 2. 界線（誠實紅線）

1. **「服務在跑」≠「角色配置正確」**：`sv.roles.services` 只陳述計數與服務狀態，不評級、不下紅黃判決。
2. **授權事實不讀金鑰**：查詢只 `SELECT Name, LicenseStatus, Description`，**不取 `PartialProductKey`**；
   這點由測試 `授權_值不得含產品金鑰欄位` 與程式碼註解雙重釘住。
3. **共用不披露路徑**：只列共用名稱（含系統預設 `C$`／`ADMIN$`／`IPC$`）——路徑可能帶出使用者資料夾名稱；
   由測試 `共用_不披露本機路徑` 釘住。有共用不等於網路可及。
4. **三態**：三個來源各自可注入探針、互不影響；`null`／例外一律 `ReadError` 並附原因，不以 0 冒充。

## 3. 真機驗證（可重跑）

```bash
dotnet build -clp:ErrorsOnly                     # → 建置成功，0 警告 0 錯誤
dotnet test Tests/XinSpect.Tests.csproj --filter "FullyQualifiedName~ServerRoleDeepFactsTests"
#  → 已通過! 失敗: 0，通過: 21
./bin/Debug/net10.0-windows/win-x64/XinSpect.exe --json evidence --query sv.
```

真機輸出（本機 Server 2025）：

| 鍵 | 可用性 | 值（節錄） |
|---|---|---|
| `sv.core` | Present | 含桌面體驗（Desktop Experience）；InstallationType=Server；Windows Server 2025 Datacenter |
| `sv.roles.services` | Present | 6 個角色服務、5 個執行中（LanmanServer／Spooler／vmms／TermService／MSDTC 執行中，MSiSCSI 未執行） |
| `sv.licensing` | Present | ServerDatacenter edition・已啟用（Licensed）（LicenseStatus=1）・OEM_DM channel |
| `sv.shares` | Present | 檔案共用 9 個、列印共用 0 個 |
| `sv.summary` | NotSupported | 角色盤點未在本批事實內——摘要不憑空生（既有行為） |

## 4. 本輪在真機抓到的缺陷並修好（附回歸釘子）

**`sv.shares` 一上真機就是 `ReadError`**：症狀是共用事實永遠讀不到，但單元測試全綠。
根因：WMI `Win32_Share.Type` 對系統預設共用（`C$`／`ADMIN$`）回 `0x80000000`（uint 2147483648），
原以 `Convert.ToInt32` 承接 → **`OverflowException`** → 整把鍵退化成 `ReadError`。
修法：`ShareEntry.Type` 改 `long`、轉換走 `internal static long ShareTypeOf(object?)`。
**這正是本專案最貴的缺陷類別：「單元測試注入探針全綠，真機靜默失靈」**——注入測試用的是 `long` 字面值，
繞過了讀取層的轉換，所以只有**真機端到端**才照得出來。回歸釘子：`共用_系統預設共用的特殊位元不得溢位`
直接餵 `(uint)0x80000000` 給 `ShareTypeOf`。

## 5. 逐檔改動（本輪）

| 檔案 | 內容 |
|---|---|
| `Services/ServerRoleDeepFactsService.cs` | 新服務：三把鍵＋WMI 讀取層＋探針注入＋三態 |
| `Tests/ServerRoleDeepFactsTests.cs` | ＋21 條（三態契約／枚舉對照／禁項紅線／溢位回歸） |
| `Services/EvidenceLabService.cs` | `ServerRoleDeepFacts` 屬性＋`LoadServerRoleDeep()`＋併入 `AllFacts` |
| `Services/EvidenceCollection.cs` | 啟動路徑 `LoadServerRoleDeep()`（UI／CLI 同源） |
| `Services/FactKeyCatalog.cs` | ＋3 把固定鍵（300 → 303）、修正目錄計數註解 |

> **版號／README 徽章／changelog 本輪不動**：依推進計畫，「50 項＝一版」；且 `TestSuiteBaseline.ProjectTests`
> 仍有餘裕（基線 ≥ 方法數），故本波不觸發版號動作。

## 6. 本輪**沒有**做的事（留給後續）

1. G4 其餘項：SV-002 IIS 應用池、SV-003 AD DS、SV-004 DNS 區域、SV-005 DHCP、SV-008 RDS 深層、
   SV-009 Hyper-V 交換器、SV-010 容錯移轉叢集——多需角色安裝後才有資料；本波先交付**永遠可得**的三把。
2. 未新增 UI 頁面；事實已進 `AllFacts`（CLI／報告／時間膠囊可見），韌體安全頁的分類列由既有機制自動納入。
3. **並行工作線的紅燈（非本輪引入，據實記錄）**：`FactKeyCatalogTests`／`FactKeyRuntimeReconcileTests`／
   `EvidenceLabIntegrationTests` 三條紅，出處皆為另一條工作線的 `sb.pci.*`／`sb.pcieaer.*`／`acpi.table.*` 鍵
   （本輪的 `sv.*` 鍵不在任何不一致清單中）。
