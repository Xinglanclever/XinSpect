# 閘門紀錄：階段二掃尾、目錄補洞與 TPM 的 WMI 備援（第 258 輪）

> **前情**：第 257 輪（`GATE-LOG-257.md`）接手了停住的代理、修掉 `TPM_PT` 編號與 revision 的實質缺陷，
> 並把覆蓋申報的已檢視基線移到 309。本輪是同一天的續作（跨到 2026-10-18 凌晨），
> 照使用者圈的三件事做：**①TBS 不可用時的備援通路 ②階段二待辦列的掃尾 ③發版 v2.85**。
> 本檔記的是實測到的狀態與改動，不含推測。**最後一節寫明這一輪沒有做什麼。**

---

## 一、工程一：TBS 服務不存在時，走 Windows 自己的視圖（WMI）

第 257 輪查出本機的 `TBS` **服務根本沒有安裝**（`sc query TBS` 回 1060、登錄檔沒有
`HKLM\SYSTEM\CurrentControlSet\Services\TBS`），而 TPM 裝置、驅動與 `Win32_Tpm` 都正常——
所以 `Tbsi_Context_Create` 回 `0x8028400F`（`TBS_E_TPM_NOT_FOUND`）**不是 TPM 的問題**。

**這一輪的解不是「把 TBS 裝回來」，而是給第二條路**：`Services/TpmFactsService.cs` 在 TBS 建不起內容時，
改讀 `root\cimv2\security\microsofttpm` 的 `Win32_Tpm`，並把「這是誰說的」寫進事實。

| 真機實測（`Win32_Tpm`） | 值 |
|---|---|
| SpecVersion | `2.0, 0, 1.16` |
| ManufacturerIdTxt | `INTC` |
| ManufacturerVersion | `500.5.32.0` |
| IsEnabled／IsActivated／IsOwned（`_InitialValue`） | 是／是／是 |

**三條界線也一起寫進事實裡**（這比數字重要）：

1. **來源不是同一個**：WMI 那一列的來源欄是 `Windows WMI（Win32_Tpm；TBS 不可用時的備援）`，
   而 TBS 可用時來源是 `Windows TBS（tbs.dll）`——值與來源都不同，不得互相代言。
2. **`revision` 是換算不是直讀**：`SpecVersion` 的第三段 `1.16` 對應 TBS 讀到的 `PT_REVISION＝116`，
   等於 `1.16×100`（這個 ×100 的關係是第 257 輪照 TPM 2.0 Part 2 重新對出來的）。
3. **WMI 沒有能力欄位**：`tpm.pcr.banks`（PCR 銀行）、`tpm.tcg_log`（事件日誌）、
   `tpm.attest.capable`（證明能力）在備援通路上**標 `NotSupported` 並附原因**，
   **不因為「有 TPM」就代填成支援**。`tpm.owned` 也明說它不是 TPM 自己的 `TPMA_PERMANENT` 位元。

**與 TBS 可用時的行為不變**：注入通路（測試）沒給備援探針時，維持原本的單列三態
（`無TPM_單一事實如實三態` 那條測試原樣通過）；真機才會自動走 WMI。

---

## 二、工程二：階段二的待辦列掃尾

判準是矩陣的「實作鍵」欄能不能**指名出處**。這一輪圈到、且查證成立的有四列（共六把鍵）：

| 列 | 規格鍵 | 改成 | 出處（矩陣裡寫的行號） |
|---|---|---|---|
| P2-080 | `ev.db.count`／`ev.dbx.count` | `alias:uefi.db.count`／`alias:uefi.dbx.count` | `Services/UefiSignatureFactsService.cs:61` |
| P2-091 | `spi.entropy.*` | `alias:spi.entropy_map` | `Services/SpiEntropyService.cs:47` |
| P2-048 | `bios.hash`／`bios.diff` | `alias:spi.bios_hash`／`alias:spi.bios_compare` | `Services/SpiFlashHashService.cs:22`／`Services/SpiFlashCompareService.cs:13` |
| P2-019 | `sb.type0.*`–`sb.type3.*` | `alias:sb.type0` | `Services/SmbiosIdentityFactsService.cs:32` |

出處不是裝飾：`SpecImplementationReconciliationTests.每一個alias都要附出處_而且出處必須真的存在`
會驗格式、檔案存在、行號落在檔案範圍內。**「兩個鍵語意相同」是人工判斷，出處是它唯一的依據。**

**新增實作一列——P2-096 周邊韌體版本盤點（`pdev.fw.version`）**：

- 硬碟／SSD 的韌體版本取自 `Win32_DiskDrive.FirmwareRevision`，**Trust＝Reported（裝置自述）**；
  事實裡明寫「這不是對照原廠清單後判定的最新版」——那件事是 P2-097 `pdev.fw.delta`，需要知識包。
- **網卡那半邊沒有來源，而且這件事要說出來**：Windows 的 `Win32_NetworkAdapter` **沒有韌體欄位**，
  唯一像的 `DriverVersion` 是驅動版本。事實裡寫明「不以驅動版本冒充」，把那半邊標成**缺源**，
  並附上「查到幾張實體網卡」——讓「缺源」與「沒查」分得開。
- 三態齊備：列舉失敗＝`ReadError`（並明說那與「沒有硬碟」是不同的斷言）／查詢成功但回空＝`NotApplicable`
  ／有硬碟但裝置不自述韌體＝`Present` 且值裡寫「沒有任何一顆自述」。8 條測試（`Tests/PeripheralFirmwareFactsTests.cs`）。
- 接線進 `EvidenceLabService`（`PeripheralFirmwareFacts` 屬性、`LoadPlatformFacts` 收集、`AllFacts` 併入），
  否則它就是一支孤兒服務——`WiringGuardTests` 正是為這種事存在的。

**掃尾後的階段二狀態**（同一支腳本數出來，含三列 `★` 錨記——先前用樸素字串比對會**安靜地少算三列**）：

| 實作鍵狀態 | 把數 |
|---|---|
| `ok:`（規格鍵＝實作鍵） | 61 |
| `alias:`（語意相同、名字不同） | 24 |
| `todo:` | 28 |
| **合計（105 列，一列可多把鍵）** | **113** |

---

## 三、順手照出來的兩個真缺陷（目錄與掃描面）

做 P2-080 的別名時要查 `uefi.db.count` 在不在目錄裡，於是發現：

1. **`uefi.db.count`／`uefi.dbx.count`／`uefi.kek.count` 三把鍵目錄裡沒有，執行期卻真的在產。**
   原因是它們在 `CollectSigList` 裡用 `keyPrefix + ".count"` **組出來**，而
   `CoverageService` 的掃描器**只認字面**——「目錄沒有」與「事實存在」於是同時成立。
   這正是本專案最在意的那類缺陷：**掃不到的東西不會紅燈，只會安靜地少算。**
   逐條清單 `uefi.{db,dbx,kek}.list.{0..4}` 同理（有幾條由變數內容決定，最多列 5 條）。
   修法是讓它成為字面：`UefiSignatureFactsService` 新增
   `DbCountKey`／`DbxCountKey`／`KekCountKey`（名字含 `Key` → 掃描器收進鍵集）
   與 `DbListPrefix`／`DbxListPrefix`／`KekListPrefix`（**名字刻意不含 `Key`** → 它是動態家族前綴、
   **不是一把鍵**，進鍵集會憑空多出不存在的鍵），並在 `FactKeyCatalogTests` 的動態家族上限裡登記。
2. **動態家族上限 23 → 26**（+3，理由是上面那三個清單家族），**覆蓋申報的已檢視上限 309 → 313**
   （+1 `pdev.fw.version`、+3 三把 count 進掃描面）。**兩個上限都不是為了讓紅燈變綠**：
   它們是「回頭數一數」的觸發器，所以每一次移動都寫出**是哪幾把鍵、為什麼**（註解就在測試檔裡）。
   實測當下的覆蓋申報：**事實鍵 1,030 個｜24 個有規則考慮｜693 個明文豁免｜313 個尚未被任何規則碰到**
   （26 條規則共引用 24 個鍵）。**未覆蓋不是錯誤**，是「還沒有人替它寫規則」。

**新增一條守門**：`EfiVariableFactsTests.執行期產生的鍵都要被目錄或動態家族收錄`——
餵一份人造的 db／dbx／KEK 變數，把服務在執行期產生的每一把鍵拿去問目錄與動態家族，
漏登記就紅。它含正對照（`uefi.db.count`／`uefi.db.list.0`／`uefi.kek.list.0` 必須出現），
否則「服務什麼都沒產」也能讓它變綠。

---

## 四、全套件結果

**6,393 條、0 失敗**（6 段過濾執行之和：1,056＋1,117＋1,055＋1,055＋1,055＋1,055；
電源計畫＝**高效能**；最重的一段 7 分 15 秒）。

**為什麼分段跑**：單一指令的全套在這台機器上約 11 分鐘，而**工具單次呼叫上限是 10 分鐘**，
所以改成「同一份組建、六個互斥且完全覆蓋的類別過濾」。分段與單跑的口徑差別要說清楚：
分段是**以類別為單位**（519 個類別全部指派、無重疊），**不是**「只跑了一部分」——
但這一輪**沒有**留下一個單一指令的全套綠燈紀錄，這一點如實記在這裡。

**基線與徽章同步**：`TestSuiteBaseline.ProjectTests` 6,381 → 6,393；三份 README 的測試徽章同步
（`ChangelogTests.三份README的測試徽章等於專案測試基線` 釘住這件事）；
事實鍵目錄 1,026 → **1,030** 把、動態家族 23 → **26** 個。

---

## 五、這一輪的教訓

1. **「目錄有而原始碼沒有」與「原始碼有而目錄沒有」是兩種不同的病，而掃描器那條守門只看得見後者。**
   組出來的鍵（`keyPrefix + ".count"`）在兩個方向上都缺席，於是**兩個方向的檢查都不會叫**——
   要等到有人為了別的理由去查那把鍵時才會發現。**「沒有紅燈」不等於「對帳過」**。
2. **命名可以是語意的一部分**：`*CountKey` 與 `*ListPrefix` 的差別不是風格，
   而是「這是一把鍵」與「這是一個家族前綴」——而掃描器只看得見前者。**名字選錯，鍵集就多一把不存在的鍵。**
3. **上限提高要寫出「是哪幾把、為什麼」，否則下一個人只看到一個數字變大。**
   這兩個 cap 都是「回頭數一數」的觸發器，不是容許值。
4. **備援通路最大的風險不是讀不到，是把它讀到的東西說成主通路的東西。**
   所以 WMI 那一批事實的每一條都寫了它與 TBS 那一批的界線，包含「這一列不代表本專案的 TPM 通路可用」。

---

## 六、這一輪**沒有**做的事（不假裝做完）

- **階段二仍有 28 把鍵標 `todo`**（`fw.risk.facts.*` 六把、`spi.hash.*`／`spi.desc.*`、
  `tpm.cve`／`tpm.key.present`／`tpm.replay.*`、`ev.nvram.usage`、`ec.thermal.readable`、
  `bt.state`／`bt.chain.gap`、`sb.memmap.*`、`ac.hmat.*`…）。**沒有動它們**。
- **TPM 的真機值仍是 WMI 那一份**：TBS 服務不存在，所以 PCR 讀值、事件日誌、
  證明能力在真機上仍是 `NotSupported`。**要拿到 TBS 那一份值，得先把 TBS 服務裝起來**
  （這是環境的事，不是程式的事），這一輪沒有動系統設定。
- **`pdev.fw.version` 只在本機驗證過「讀得到硬碟那一半」**；網卡那半邊在本機的實際結果
  （實體網卡張數）沒有寫進任何文件——它是機器狀態，不是規格。

---

## 七、發版（同一輪的第三件事）

- 版本 **v2.85 Ultima I**：`Nav/ChangelogCatalog.cs` 的 2.85 條目補上階段二與 TPM 兩條，
  三份 README 的版本沿革與下載表同步。
- 產出物與 Release 的實際位元組數記在下一節（發佈後補寫，**不先寫數字**）。

（本節在發佈完成後補完；**位元組數以實際發佈的檔案為準**。）
