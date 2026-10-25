# AUDIT-dedup — U10 擴展目錄查重對帳報告

日期 2026-10-25　·　產出腳本：同目錄 `dedup_check.py`（絕對路徑寫死，可重跑）
　·　重跑方式：`python "C:\Users\Administrator\.zcode\workspace\draft-0533e841-8f3d-4ca8-8b24-3606a2327a55\U10-parts\dedup_check.py"`
　·　本報告每個數字皆來自該腳本輸出（或腳本輸出所列證據的逐鍵 grep 覆核）。

對帶四方：
- 主目錄：`C:\Users\Administrator\.zcode\workspace\draft-0533e841-8f3d-4ca8-8b24-3606a2327a55\ULTIMATE-10000-EXPANSION-2026-10-24.md`（唯讀；腳本逐行串流解析，未全文載入）
- 倉庫：`C:\Users\Administrator\XinSpect`（唯讀，未修改任何檔案）
- 既有鍵：`Services\FactKeyCatalog.cs`（全鍵）＋`Services\FactKeyDynamicCatalog.cs`（動態家族）
- 標記輸入：`U10-parts\dup-marked.md`（79 列）

---

## 一、方法

**抽取（目錄側）**：逐行取 `| U10-XX-NNNN |` 開頭者為目錄列；列之 ID、域（XX）、列名（ID 欄至第一個反引號間）、候選鍵（第一個「鍵形」反引號塊）。鍵形定義：`^[a-zA-Z0-9][a-zA-Z0-9_.{}*|,@+-]*$` 且非 `0x` 開頭——`0x` 開頭的是 GD 域 GUID 值（542 列每列「鍵＋GUID 值」兩塊，取鍵、棄 GUID）；`{n}`/`{i}`/`{dimm}`/`{a,b}` 交替集、`@60`、連字號（如 `dmt.1280x{960,1024}@60`、`kp.pmu.map.westmereep-dp`）都算鍵。無反引號塊者為「無鍵列」。

**抽取（倉庫側）**：`FactKeyCatalog.cs` 之字串字面值列 `^\s*"[a-z0-9_.]+",$` → 1,044 鍵（與檔內宣稱一致，腳本計數）；`FactKeyDynamicCatalog.cs` 之 `["前綴"] =` → 28 個家族前綴（檔內註釋宣稱 27——見意外發現 5）。

**三級規則（對每個候選鍵 K，比對採大小寫折疊）**：
- **重複**：K 與某既有全鍵完全相等（僅大小寫差）。
- **疑似收口**：既有能力已覆蓋、目錄列至多補陳述。證據型四種（按序判）：
  - `family`：K 落在動態家族前綴下（如 `uefi.boot.*` ⊂ 家族 `uefi.boot.`）；
  - `wildcard-stem`：K 含萬用字元，其 stem（`{`／`*` 前一段）前綴命中既有全鍵；
  - `repo-prefix`：既有全鍵 R 使 K 前綴於 R+"."（既有鍵是目錄鍵的前綴）；
  - `repo-superset`：既有全鍵 R 是 K 的上層節點（目錄鍵為既有鍵前綴——既有能力是超集）。
  （本輪實際命中前三種；`repo-superset` 無命中。）
- **淨新**：其餘。

無鍵列（NX「不做」宣言 17 列）不參與三級判定，單獨計數。另計「目錄內自重複鍵」（同鍵出現於多列）——這是目錄對自身的重複，與對倉庫的「重複」分開呈報，不併入三級。

**輔助近鄰證據**（不影響判定）：對 79 標記列另列倉庫近鄰鍵（最長共同字元前綴 ≥3 且止於點邊界，前 3 筆）——因標記註記常宣稱「查重：X 已有」，近鄰輸出把該宣稱對到倉庫實際鍵名。

---

## 二、輸入基線

| 項目 | 數 |
|---|---:|
| 倉庫全鍵（FactKeyCatalog.cs） | 1,044 |
| 倉庫動態家族（FactKeyDynamicCatalog.cs） | 28 |
| 目錄列總數 | 10,022 |
| 含候選鍵列 | 10,005 |
| 無鍵列（全部為 NX 域宣言列） | 17 |
| 目錄內重複 ID | 0 |
| 目錄內自重複鍵 | 29 鍵／60 列（重複列數 31） |
| 反引號塊格式異常（非鍵非 GUID） | 0 |
| 標記 79 列在主目錄尋獲 | 79／79（無失聯） |

---

## 三、總計（僅含鍵列 10,005）

**淨新 9,930　·　疑似收口 71　·　重複 4**

疑似收口證據型：repo-prefix 61／family 9／wildcard-stem 1。重複證據型：exact 4。

## 四、逐域統計

| 域 | 列數 | 含鍵 | 淨新 | 疑似收口 | 重複 | 無鍵 |
|---|---:|---:|---:|---:|---:|---:|
| SB | 1108 | 1108 | 1059 | 48 | 1 | 0 |
| AC | 798 | 798 | 798 | 0 | 0 | 0 |
| PM | 798 | 798 | 798 | 0 | 0 | 0 |
| CP | 787 | 787 | 787 | 0 | 0 | 0 |
| NP | 719 | 719 | 719 | 0 | 0 | 0 |
| IC | 593 | 593 | 593 | 0 | 0 | 0 |
| PH | 554 | 554 | 554 | 0 | 0 | 0 |
| GD | 542 | 542 | 542 | 0 | 0 | 0 |
| FW | 512 | 512 | 489 | 20 | 3 | 0 |
| MR | 485 | 485 | 483 | 2 | 0 | 0 |
| PC | 355 | 355 | 355 | 0 | 0 | 0 |
| NV | 350 | 350 | 350 | 0 | 0 | 0 |
| TS | 227 | 227 | 227 | 0 | 0 | 0 |
| OS | 224 | 224 | 223 | 1 | 0 | 0 |
| BT | 197 | 197 | 197 | 0 | 0 | 0 |
| MS | 187 | 187 | 187 | 0 | 0 | 0 |
| VT | 181 | 181 | 181 | 0 | 0 | 0 |
| SS | 140 | 140 | 140 | 0 | 0 | 0 |
| GP | 139 | 139 | 139 | 0 | 0 | 0 |
| VZ | 127 | 127 | 127 | 0 | 0 | 0 |
| HU | 123 | 123 | 123 | 0 | 0 | 0 |
| AT | 100 | 100 | 100 | 0 | 0 | 0 |
| MM | 87 | 87 | 87 | 0 | 0 | 0 |
| ST | 83 | 83 | 83 | 0 | 0 | 0 |
| GV | 76 | 76 | 76 | 0 | 0 | 0 |
| MT | 73 | 73 | 73 | 0 | 0 | 0 |
| SE | 62 | 62 | 62 | 0 | 0 | 0 |
| FS | 61 | 61 | 61 | 0 | 0 | 0 |
| UI | 48 | 48 | 48 | 0 | 0 | 0 |
| PF | 43 | 43 | 43 | 0 | 0 | 0 |
| VC | 43 | 43 | 43 | 0 | 0 | 0 |
| WM | 36 | 36 | 36 | 0 | 0 | 0 |
| BD | 30 | 30 | 30 | 0 | 0 | 0 |
| RP | 28 | 28 | 28 | 0 | 0 | 0 |
| EN | 25 | 25 | 25 | 0 | 0 | 0 |
| KP | 24 | 24 | 24 | 0 | 0 | 0 |
| NX | 21 | 4 | 4 | 0 | 0 | 17 |
| EV | 20 | 20 | 20 | 0 | 0 | 0 |
| WH | 16 | 16 | 16 | 0 | 0 | 0 |
| **合計** | **10022** | **10005** | **9930** | **71** | **4** | **17** |

收口與重複高度集中：SB（SMBIOS，既有 `sb.typeN` 家族鍵）與 FW（UEFI/TPM，既有 `uefi.*`／`tpm.*` 鍵）兩域合計 68 疑似收口＋4 重複；MR/OS 各 2/1 疑似收口；其餘 35 域全為淨新。

## 五、重複（DUP）4 列全清單

- U10-SB-0019（UUID（位元組序陷阱成文；全 0/全 F 之「無效」判讀））→ 重複：`sb.type1.uuid`＝既有鍵同名
- U10-FW-0004（KEK 清單計數與大小）→ 重複：`uefi.kek.count`＝既有鍵同名
- U10-FW-0060（PCR bank 清單與各 bank 已分配索引遮罩）→ 重複：`tpm.pcr.banks`＝既有鍵同名
- U10-FW-0106（EC 存在性判讀（ACPI PNP ID 如 ITE/ENE 之跡證；僅存在性））→ 重複：`ec.present`＝既有鍵同名

## 六、疑似收口 71 列（全清單，按腳本輸出序）

SB 域 48 列（repo-prefix 型，既有 `sb.type0`～`sb.type4` 彙總鍵之下擴欄位）：U10-SB-0009～0058 連續段之 `sb.type0.*`／`sb.type1.*`／`sb.type2.*`／`sb.type3.*`／`sb.type4.*` 各列——既有的 Type 彙總鍵已存在，目錄要求逐欄展開，屬「既有能力、目錄補陳述」。

FW 域 20 列：
- U10-FW-0003（PK 存在性與大小）→ `uefi.pk.size`（repo-prefix：uefi.pk）
- U10-FW-0005（db／dbx／dbt／dbr 存在性與計數）→ `uefi.dbx.count.cross`（repo-prefix：uefi.dbx）
- U10-FW-0007（db 逐張憑證鏈展開）→ `uefi.db.chain`（repo-prefix：uefi.db）
- U10-FW-0008（dbx 撤銷媒介統計）→ `uefi.dbx.kinds`（repo-prefix：uefi.dbx）
- U10-FW-0009（憑證效期分佈）→ `uefi.db.expiry`（repo-prefix：uefi.db）
- U10-FW-0013～0021（BootOrder/BootCurrent/BootNext/BootTimeout/Boot#### 逐條/跳號）→ `uefi.boot.*` 各鍵（family：uefi.boot.，9 列）
- U10-FW-0050（映像 FV/FFS 分解）→ `fw.fv.cross`（repo-prefix：fw.fv）
- U10-FW-0051（TPM 存在性與來源）→ `tpm.present.cross`（repo-prefix：tpm.present）
- U10-FW-0061（PCR 逐 bank 現值）→ `tpm.pcr.{bank}`（wildcard-stem：tpm.pcr.banks）
- U10-FW-0108（Intel ME／AMD PSP 存在性與版本）→ `fw.me.psp.info`（repo-prefix：fw.me）
- U10-FW-0109（ME/PSP 讀取界線成文）→ `fw.me.stance`（repo-prefix：fw.me）
- U10-FW-0507（KEK 條目簽章演算法分布）→ `uefi.kek.algos`（repo-prefix：uefi.kek）

MR 域 2 列：U10-MR-0023（0x8B AMD64_PATCH_LEVEL）→ `msr.0x8b.amd64.patch.level`、U10-MR-0024（0x8B IA32_UCODE_REV）→ `msr.0x8b.ia32.ucode.rev`（皆 repo-prefix：msr.0x8b）。

OS 域 1 列：U10-OS-0176（音訊端點清單）→ `audio.endpoints.list`（repo-prefix：audio.endpoints）。

---

## 七、79 標記列逐列判定

判定分佈：**淨新 74　·　疑似收口 5　·　重複 0　·　無法判定 0**

### 7.1 判為疑似收口的 5 列

- U10-FW-0005（db／dbx／dbt／dbr 存在性與計數〔查重：uefi.db.* 已做——收口〕）→ 疑似收口：`uefi.dbx.count.cross`（repo-prefix：uefi.dbx；另有家族 uefi.dbx.list.）
- U10-FW-0021（條目跳號偵測〔查重〕）→ 疑似收口：`uefi.boot.gaps`（family：uefi.boot.）
- U10-FW-0050（映像 FV/FFS 分解〔查重：階二已做〕）→ 疑似收口：`fw.fv.cross`（repo-prefix：fw.fv）
- U10-FW-0051（TPM 存在性與來源〔查重：已有〕）→ 疑似收口：`tpm.present.cross`（repo-prefix：tpm.present）
- U10-FW-0061（PCR 逐 bank 現值〔查重：已有——收口擴 bank〕）→ 疑似收口：`tpm.pcr.{bank}`（wildcard-stem：tpm.pcr.banks）

### 7.2 判為淨新的 74 列

以下判定一律為「倉庫無此鍵、亦無前綴／家族關係」。逐列（括號內為標記註記宣稱的查重對象；其可證性見 7.3 與意外發現 2/4）：

- U10-CP-0148（SGX 子葉 `cpuid.12.epc{n}`；註記：階二已做者收口）
- U10-CP-0217（`mc.rev.crosscheck`；註記：mc.* 收口）
- U10-CP-0223（`numa.slit.grid`；註記：SRAT 已有者收口）
- U10-MS-0053（`mc.rev.msr`；註記：mc.*）
- U10-MM-0040（`mem.latency.curve`；註記：roadmap 舊項）
- U10-MM-0052（`mem.mmio.total`；註記：sb.memmap 收口）
- U10-MM-0059（`mem.whea.errors`；註記：OS 域交叉）
- U10-SB-0097（`sb.type19.cross`；註記：sb.memmap 已做——對帳收口）
- U10-SB-0161（`sb.type45.pdev.cross`；註記：查重收口）
- U10-AC-0042（`acpi.tcpa.cross`；註記：階二已做——收口對帳）
- U10-AC-0043（`acpi.dbg2spcr.cross`；註記：2.84 已做——收口對帳）
- U10-AC-0047（`acpi.srat.cross`；註記：已有 Service——收口）
- U10-AC-0048（`acpi.slit.grid`；註記：查重）
- U10-FW-0037（`fw.spi.hash.cross`；註記：階二 SPI 地圖——收口）
- U10-FW-0046（`fw.bootguard.cross`；註記：階二 FIT/Boot Guard——收口）
- U10-FW-0075（`tpm.ev.types.cross`；註記：tpm.ev.*）
- U10-FW-0104（`pdev.fw.inventory`；註記：pdev.fw.updatable——擴充）
- U10-FW-0113（`fw.risk.cross`；註記：fw.risk.facts.*——收口引用）
- U10-NV-0068（`nvme.fw.slots.cross`；註記：已做——收口）
- U10-OS-0025／0028／0029／0030／0033／0038／0040／0057／0061（`wmi.cpu.gap`、`wmi.bios.cross`、`wmi.board.cross`、`wmi.mem.cross`、`wmi.disk.gap`、`wmi.gpu.gap`、`wmi.net.gap`、`wmi.pnpentity`、`wmi.tpm.cross`；註記均宣稱 Win32_* 已用/已有）
- U10-SE-0003（`sec.vbs.state`）、U10-SE-0036（`sec.pdevfw.surface`）、U10-SE-0043（`sec.l3.certname.gate`）、U10-SE-0044（`sec.export.unmask.ledger`）、U10-SE-0047（`rep.fw.risk`）、U10-SE-0051（`sec.egress.zero`）、U10-SE-0054（`sec.sac.state`）、U10-SE-0058（`sec.sddl.controllers`）、U10-SE-0059（`sec.ads.drivers`）、U10-SE-0060（`sec.apps.signed.hw`）、U10-SE-0061（`sec.logon.hw.correlate`）
- U10-MT-0036（`mt.seasonal.cross`）、U10-MT-0044（`mt.decoder.tests`）、U10-MT-0049（`mt.decoder.citation.gate`）
- U10-VZ-0014（`viz.sankey.cross`）、U10-VZ-0016（`viz.tree.cross`）、U10-VZ-0064（`ui.factatlas.spark`）、U10-VZ-0069（`viz.no3d.cross`）、U10-VZ-0078（`ui.pinned.panel`）
- U10-UI-0010（`ui.help.coverage`）、U10-UI-0014（`ui.lang.live.cross`）、U10-UI-0015（`ui.lang.sync.gate`）、U10-UI-0032（`ui.mini.custom`）、U10-UI-0033（`ui.changelog.view`）、U10-UI-0034（`ui.about.licenses`）
- U10-PF-0001（`rep.html.cross`）、U10-PF-0009（`exp.hash.chain`）、U10-PF-0012（`cli.headless.cross`）、U10-PF-0017（`ps.modules.gap`）、U10-PF-0028（`installer.cross`）、U10-PF-0029（`release.singlefile.gate`）
- U10-EN-0012（`kp.evolution.proc`）
- U10-GV-0002／0003／0004／0005／0006／0007／0008／0009／0011／0012／0014／0015／0036／0040（`guard.*` 14 列；倉庫僅有 `guard.inventory` 一鍵，無前綴關係）

### 7.3 淨新判定的兩個誠實註記（判定不變，但讀者必須知道）

**(a) 鍵級淨新 ≠ 能力不存在——命名空間分歧。** 下列標記列的註記宣稱「已有」在倉庫**確有對應能力**，但實作鍵名與目錄鍵名不同命名空間，鍵級比對（規則所限）只能判淨新。腳本近鄰證據與逐鍵 grep 覆核：

- U10-MS-0053 `mc.rev.msr` ↔ 倉庫 `mc.version.msr`；U10-CP-0217 `mc.rev.crosscheck` ↔ 倉庫 `mc.version.cross`（另有 `mc.version.registry`）——同一能力、`rev`/`version` 命名分岔
- U10-AC-0042 `acpi.tcpa.cross` ↔ 倉庫 `ac.tpm2`／`ac.tpm2.method`；U10-AC-0043 `acpi.dbg2spcr.cross` ↔ 倉庫 `ac.dbg2`／`ac.spcr`／`ac.bgrt`；U10-AC-0047 `acpi.srat.cross` ↔ 倉庫 `ac.srat.*` 7 鍵——倉庫 ACPI 主命名空間是 `ac.`，目錄用 `acpi.`
- U10-CP-0223／U10-AC-0048 `numa.slit.grid`／`acpi.slit.grid` ↔ 倉庫 `numa.slit.nodes`（兄弟鍵，非前綴）
- U10-SE-0054 `sec.sac.state` ↔ 倉庫 `ds.sac`／`ds.sac.deep`；U10-SE-0003 `sec.vbs.state` ↔ 倉庫 `virt.vbs`／`platform.hvci`；U10-SE-0058 `sec.sddl.controllers` ↔ 倉庫 `sa.sddl.deep`／`sa.sddl.summary`
- U10-VZ-0014 `viz.sankey.cross` ↔ 倉庫 `vr.sankey`；U10-PF-0012 `cli.headless.cross` ↔ 倉庫 `ag.headless`
- U10-FW-0113 `fw.risk.cross` ↔ 倉庫 `fw.risk.facts.*` 6 鍵（兄弟節點）；U10-FW-0104 `pdev.fw.inventory` ↔ 倉庫 `pdev.fw.updatable`／`pdev.fw.version`（兄弟節點）

**(b) 鍵級淨新且倉庫鍵面查無近鄰——註記宣稱無法由事實鍵目錄證實。** `wmi.*`（9 列）、`ui.*`、`rep.*`、`exp.*`、`mt.*`、`installer.*`、`release.*`、`ps.*`、`kp.evolution.*`、`guard.*`（除 `guard.inventory`）、`fw.spi.hash.*`（倉庫僅 `fw.spiperm.summary`）、`fw.bootguard.*`（倉庫僅 `fw.bg.acm`／`fw.bg.profile`）、`tpm.ev.*`（倉庫僅 `tpm.log.histogram`／`tpm.log.summary`）、`nvme.fw.slots.*`——這些能力可能存在於程式碼／測試／XAML（事實鍵目錄本來就不涵蓋 UI 與測試類能力），但**以鍵目錄為證據，宣稱不成立**。查不到就是查不到。

---

## 八、意外發現（皆可由腳本或單行 grep 重現）

1. **目錄內部自重複 29 鍵／60 列**（同鍵出現在多列，扣首列後重複列 31）。最大戶：`kp.pmu.uops_issued_any` ×3（U10-PM-0386、U10-PM-0387、U10-PM-0612）、`kp.pmu.baclears_any` ×3（U10-PM-0493、U10-PM-0768、U10-PM-0769），其餘 27 鍵 ×2 多為 PM 域事件表重複列。**跨域重複 3 鍵**：`cache.sharemap`（U10-CP-0224、U10-MM-0037）、`cache.l{n}.size`（U10-CP-0225、U10-MM-0036）、`cache.policy`（U10-CP-0227、U10-MM-0039）——快取能力在 CP 與 MM 兩域各列一次。
2. **命名空間系統性分歧**（詳 7.3a）：目錄新造 `acpi.`／`sec.`／`viz.`／`wmi.`／`ui.`／`rep.`／`exp.` 命名空間，而倉庫既有實作在 `ac.`／`sa.`／`ds.`／`virt.`／`vr.`／`ag.`；`mc.rev` vs `mc.version`。若日後收口，這是必須先解的鍵名對齊問題，否則任何逐鍵守門（如 guard.catalog.parity 類）都會把兩套名字當兩把鍵。
3. **倉庫確有 `acpi.` 命名空間，但僅 4 鍵**：`acpi.tables`、`acpi.bert.record`、`acpi.bert.region_length`、`acpi.hest.sources`。目錄 AC 域 74 個 `acpi.*` 鍵與這 4 鍵無任何前綴關係（全淨新）；AC 域 798 列疑似收口為 0 的原因即此。
4. **目錄的「已做」註記與鍵面事實有 14 處對不上**（見 7.3a 逐條）：14 列標記註記宣稱的既有能力確在倉庫、但鍵名分屬不同命名空間或兄弟節點；另有 `tpm.ev.*`、`wmi.*`、`fw.spi`、`fw.bootguard` 等宣稱在鍵目錄查無（7.3b）——註記的「查重」結論不可直接引用，需以本報告判定為準。
5. **FactKeyCatalog.cs 註釋宣稱「27 個動態家族」，實際 FactKeyDynamicCatalog.cs 有 28 個**——文件數字漂移（腳本計數 28；`grep -c '\["' FactKeyDynamicCatalog.cs` 亦得 28，28 筆全為活碼、無註解列）。
6. **34 列的候選鍵使用倉庫鍵格式之外的語法**：交替集 `amd.pstate.{ctl,status}`、`spd.xmp.{p1,p2,p3}`、`nvme.smart.du.{r,w}`、`kp.smart.samsung.{177,187,190}`；模式字 `dmt.1280x{960,1024}@60`（20 列 VC）；連字號 `kp.pmu.map.westmereep-dp`（2 列）；混合大小寫 `ata.w77.sfSup`（2 列）。倉庫 1,044 鍵全部為 `^[a-z0-9_.]+$`——這 34 鍵未來入鍵目錄前需改寫格式（初版腳本曾誤判其為異常塊而排除，已修正並納入比對；修正前後總列數 9,971→10,005 可對帳）。
7. **GD 域 542 列每列兩個反引號塊**（`guid.xxx` 鍵＝`0x…` GUID 值）——抽鍵規則取首個鍵形塊、棄 GUID，無誤抽（該域 542 列全數取得鍵）。
8. NX 域 21 列中 17 列無候選鍵（0001–0017 為「不做」宣言，屬預期格式），另 4 列有鍵參與判定。

---

## 九、結論

- 對倉庫 1,044 全鍵＋28 動態家族：10,005 含鍵目錄列中**淨新 9,930、疑似收口 71、重複 4**；無鍵列 17（NX 宣言）。
- 79 標記列：**淨新 74、疑似收口 5、重複 0**——標記註記大量宣稱「已做／收口」，但以鍵目錄為證據僅 5 列成立；其中 14 列屬命名空間分歧下的「能力已有、鍵名不同」（7.3a 逐條可對帳），其餘宣稱在鍵面查無實據（7.3b）。
- 目錄自身另有 29 鍵／31 重複列（含 3 個跨域重複），應先於目錄內部去重，再談對倉庫收口。
- 本報告僅判定「事實鍵面」；UI／測試／發布類能力本不在鍵目錄範圍，其「已做」與否不能由本對帳證實或證偽。
