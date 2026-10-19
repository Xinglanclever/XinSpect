# 階段二剩餘 15 把待辦鍵：卡在哪、怎麼解

> **這份文件的用途**：把「還沒做的 15 把鍵」按**卡住的原因**分類，而不是按項目編號排列。
> 專案裡既有的待辦清單（`STAGE-02-FIRMWARE.md` 矩陣）回答「哪一項沒做」，
> 但沒回答「為什麼沒做、需要什麼才能做」——於是每一輪開工前都要重新 grep 一遍，
> 而重新 grep 的結論每次都不一樣（`UNSOURCED-CONSTANTS.md` 記過四次這種盤點）。
>
> **資料時點**：2026-10-19（第 266 輪之後），矩陣 `ok 75／alias 24／todo 15`（114 把鍵、105 項）。
> 第 260 輪完成 P2-105、第 261 輪把 C 群三把（P2-082／089／090）收口成「平台不提供」、
> 第 265 輪把 F 群的六把風險陳述（P2-099～104）落地（見 `GATE-LOG-265`）、
> **第 266 輪完成 P2-098 可更新性宣告**（見 `GATE-LOG-266`）之後重算；
> 數字由 `FirmwareDocReconcileTests` 與文件互相釘住。

---

## 一、五種卡法（同一種卡法要同一個解法）

| 卡法 | 意思 | 需要什麼才能解 |
|---|---|---|
| **A. TBS** | 讀 TPM 的 PCR／事件日誌／NV 索引，全部經過 TBS | 機器上有 TBS 服務（本機沒有，見第四節） |
| **B. ring0** | 要讀 MMIO（SPI 控制器、快閃內容） | 載入本專案的驅動（`XinSpect.sys`）並取得管理員權限 |
| **C. 平台沒有那條 API** | Windows 沒有公開介面可列舉 EFI 變數 | 沒有解法——只能誠實標「這條路不存在」，或改用不公開介面（本專案不做） |
| **D. 規格原文取不到** | 有介面，但欄位佈局沒有可引用的原文 | 離線取得規格 PDF（ACPI／SMBIOS／Intel SDM）並逐欄核對 |
| **E. 知識包沒有資料** | 有本機值，但沒有可比對的清單 | 匯入可離線使用的知識包（CVE、dbx 版本、原廠韌體版本） |
| **F. 規則未定** | 純本機計算，但「怎麼算」是產品決策 | 由使用者圈定規則與權重（本輪不動） |

> **共同的紀律**：A～E 都不是「再努力一點就做得出來」。
> 本專案已經有一份 `UNSOURCED-CONSTANTS.md` 記著「缺一張表就做不完」的代價，
> 這一輪不再用「差一點」的實作去填那個洞——**填錯的編號會讓不存在的東西看起來存在**。

---

## 二、逐鍵清單

| ID | 鍵 | 卡法 | 具體缺什麼 | 本機現在可量測嗎 |
|---|---|---|---|---|
| P2-008 | `ac.hmat.*` | **D** | ACPI 6.5 §5.2.27 的子結構欄位圖（三個子型別的位移、Data Type 的 entry 寬度、Entry Base Unit 的指數語意） | **否**：本機 ACPI 表列沒有 HMAT（第四節） |
| P2-029 | `sb.proc_add` | **D** | SMBIOS Type 44（Table 128）的欄位圖與 x86 區塊的 Block Length 計法 | 待 D 解決；本機是否有 Type 44 未查（要先能正確解才值得查） |
| ★P2-036 | （測試） | **B** | 真實 UEFI 映像或快閃傾印作 golden | **否**：磁碟上沒有 `.rom`／`.cap`／BIOS 映像 |
| P2-053 | `bt.state` | **B＋D** | Boot Guard 狀態 MSR 的位元定義（Intel SDM）＋ ring0 讀 MSR | 否（FIT 那半已完成，見 `bt.ibb`／`bt.manifests`） |
| P2-060 | `bt.chain.gap` | **A** | TCG 事件日誌（TBS）＋ 宣告鏈 | 否 |
| P2-066 | `tpm.replay.first_mismatch` | **A** | 重放本身就要事件日誌 | 否：`tpm.replay.summary` 在本機是 NotApplicable（TBS 無法建立內容） |
| P2-067 | `tpm.replay.unsupported.count` | **A** | 同上 | 否 |
| P2-070 | `tpm.key.present` | **A** | EK／AK 存在性要 TPM NV 讀取 | 否 |
| P2-074 | `tpm.cve` | **E** | TPM 韌體版本→已知弱點的對照表（可離線） | 版本讀得到（`Win32_Tpm`），**但沒有清單可比** |
| P2-077 | `tpm.cross.hwchange` | **A** | 事件日誌＋硬體變更時間線 | 否 |
| P2-081 | `ev.dbx.stale` | **E** | 只剩 dbx 版本知識包——**dbx 本身 2026-10-18 起讀得到了**（77 條撤銷簽章、3724 bytes，見 `GATE-LOG-262`） | 有本機值，缺的是可比的清單 |
| P2-086 | `spi.hash.*` | **B** | 逐區域讀快閃（MMIO） | 否：目前沒有載入中的 ring0 服務 |
| P2-087 | `spi.changed` | **B** | 同上＋前次快照 | 否 |
| P2-088 | `spi.desc.*` | **B** | 快閃描述符讀取 | 否 |
| P2-097 | `pdev.fw.delta` | **E** | 原廠最新韌體版本清單 | 版本讀得到，**沒有清單可比** |
| P2-098 | `pdev.fw.updatable` | — | **第 266 輪完成**（見 `GATE-LOG-266`）：NVMe 讀 log page 0x03 與 Identify Controller OACS bit2；ATA 讀 word 83 bit0／word 69 bit8（三態） | 來源具備；本輪以 CIM 對照確認匯流排分類（3 顆 NVMe、2 顆 ATA 家族），log 0x03 的實機回值待人工掃描確認 |
| P2-099 | `fw.risk.facts.parts` | — | **第 265 輪完成**：構成＝既有的對帳規則集（不另立清單） | ✅ |
| P2-100 | `fw.risk.facts.level` | — | **第 265 輪完成**：等級只用「有沒有」判定（矛盾／缺口），沒有權重與門檻 | ✅ |
| P2-101 | `fw.risk.facts.gap` | — | **第 265 輪完成**：缺口逐條列出，不當 0、不當「沒問題」 | ✅ |
| P2-102 | `fw.risk.facts.baseline` | — | **第 265 輪完成**：canonical JSON＋內容雜湊（不含時間與機器識別） | ✅ |
| P2-103 | `fw.risk.facts.delta` | — | **第 265 輪完成**：由時間膠囊比較產生（只比參與鍵、跨機器拒比） | ✅ |
| P2-104 | `fw.risk.facts.report` | — | **第 265 輪完成**：沿用既有的自足單檔 HTML（尾端嵌可重算雜湊） | ✅ |
| P2-105 | `fw.doc.reconcile` | — | **第 260 輪完成**（見 `GATE-LOG-260`） | ✅ |

---

## 三、依卡法分組（同一批可以一起解）

> **先看合計，免得又要拿計算機：**5（A）＋4（B）＋2（D）＋3（E）＝**14**，
> 再加上**同時卡在 B 與 D 的 P2-053**（那 1 把只在括號裡出現過一次）＝**15 把**。
> **C 群 3 把（第 261 輪）、F 群 6 把（第 265 輪）、D 群的 P2-098（第 266 輪）都不在這個 15 裡**——
> 留在這裡只是為了說明「曾經被歸在這裡的那三群」的下場。
> 這一段先前只把 P2-053 寫在括號裡而沒有計入任何一組，於是逐組加起來是 21、標題卻寫 22——
> **一個只在括號裡的項目，讀起來像附註而不像成員；把它寫成算式就不必再猜。**

- **A（TBS，5 把）**：P2-060／066／067／070／077。**同一個開關**：機器要有 TBS 服務。
  有 TBS 之後，這五把是「既有重放結果的再切分」，不是新讀取路徑。
- **B（ring0，4 把）**：★P2-036／086／087／088（P2-053 還要加上規格原文）。
  同一個開關：驅動載入 + 讀得到快閃。
- **C（沒有 API，3 把）**：P2-082／089／090（**只有這三把**——P2-081 曾經被歸在這裡，但它的 C 成分是「讀不到 dbx」，那是缺陷不是平台限制）。**已於第 261 輪收口**：
  三把都以 `NotSupported` 陳述「Windows 沒有列舉 EFI 變數的公開 API」，
  矩陣標成 `ok:`（收口也是完成——留著 todo 只會讓帳本宣稱它們在排隊等實作）。
  這一群沒有「還沒解鎖」的列了，所以它從下面的建議順序裡消失。
- **D（規格原文，2 把）**：P2-008／029（P2-053 的 MSR 半邊）。**解鎖條件是離線規格**。
  P2-098 原本也被歸在這裡，但第 266 輪證明**它不在這裡**：它要的原文（NVMe log page 0x03 的佈局、
  ATA 的 word 83 bit0／word 69 bit8）**就在本機的 Windows SDK 標頭裡**（`shared/nvme.h`、`km/ata.h`），
  不必等離線規格 PDF。真正卡住的不是原文，是**我一開始查錯了來源**——見第五節的更正與 `GATE-LOG-266`。
- **E（知識包，3 把）**：P2-074／081／097（P2-081 原本另有「讀不到 dbx」的成分，**那已於 2026-10-18 修掉**）。**解鎖條件是資料**。
- **F（規則，6 把）**：P2-099～104。**已於第 265 輪收口**——但收口的形狀與 C 群不同：
  計畫原本寫「解鎖條件是決定（規則與權重由使用者圈定）」，實作時發現**可以完全避開那個決定**：
  構成改用既有的 `FactRelationRules.All`（每條規則自己就帶出處理由）、等級只用「有沒有」判定
  （矛盾 ≥1／缺口 ≥1／全部無法驗證），於是**沒有任何需要圈定的權重或門檻**。
  這是這一群真正的教訓：**「需要一個產品決定」有時候不是沒有選擇，是還沒找到不需要那個選擇的形狀。**
  代價寫在 `GATE-LOG-265`：等級陳述的是「證據能陳述到什麼程度」，不是風險高低。

---

## 四、本機實測（2026-10-18 第 265 輪、2026-10-19 第 266 輪，真的跑過的）

```
TBS 服務：Get-Service TBS → 不存在（不是停止，是沒有這個服務）
ACPI 表列：DBGP MCFG FACP SRAT MSCT SLIT DMAR APIC HPET FPDT FIDT MIGT PCAT PCCT
           RASF SVOS WDDT OEM4 OEM1 SSDT NITR SSDT LPIT WSMT SSDT DBG2 BGRT WPBT TPM2
           → 沒有 HMAT（也沒有 NFIT／PPTT）
EFI 變數：GetFirmwareEnvironmentVariableExW("BootOrder") → 0 bytes，GetLastError 1314
           （ERROR_PRIVILEGE_NOT_HELD）——而該行程是 elevated
ring0：服務清單沒有 XinSpect／WinRing0 項目；XinSpect.sys 只存在於舊的 publish 目錄
規格原文：uefi.org 在本機被 DNS 解析到 198.18.0.12（保留位址），DMTF PDF 也取不到
          但**本機就有 Windows SDK 的標頭**（`Windows Kits/10/Include/10.0.28000.0/`）：
          `shared/nvme.h`、`km/ata.h`、`um/winioctl.h` 三份都在——第 266 輪的三個關鍵出處全出自這裡
匯流排（2026-10-19）：CIM `MSFT_PhysicalDisk.BusType` 的原始值＝NVMe 17（3 顆）、SATA 11（2 顆），
          與 SDK 的 `STORAGE_BUS_TYPE` 列舉逐項相符——**錯的是本專案自己的兩張名稱表**（見第六節）
```

**兩個容易誤讀的地方：**

1. **EFI 變數的 1314 不是「這台機器不支援 UEFI」。** 韌體是 UEFI（`BiosFirmwareType=Uefi`），
   TPM2／WPBT 表都在。1314 說的是**權杖沒有啟用 `SeSystemEnvironmentPrivilege`**——
   那是可以 `AdjustTokenPrivileges` 解決的，與 P2-082 的「沒有列舉 API」是**兩件不同的事**。
   前者擋住的是 P2-081／089（需要讀特定變數），後者擋住的是 P2-082／090（需要列舉）。

   **⚠ 2026-10-18 追查後補記（見 `GATE-LOG-262`）**：權限只是那條路上**三個**缺陷之一。
   另外兩個是：**`db`／`dbx` 的 GUID 是錯的**（實測只有
   `{d719b2cb-3d3a-4596-a3bc-dad00e67656f}` 讀得到：db 8188 bytes、dbx 3724 bytes／77 條簽章），
   以及**「先查大小再讀」把 `pdwAttributes` 當成大小用**（`size` 永遠是 0 → 每個變數都回 null）。
   三個都修掉之後，本機第一次真的讀到值：`uefi.db` 11 條、`uefi.dbx` 77 條、`uefi.kek` 3 條、
   `uefi.pk` 886 bytes、`uefi.boot_entries.count` 9 個。
   **所以 P2-081 的 C 成分已經消失，P2-089 需要的是「按名字讀得對」＋知識包，
   真正只剩列舉那條路的仍是 P2-082／090。**
2. **HMAT 缺席不等於讀取失敗。** 表列是成功的（29 個表項目、含 3 個 SSDT 都列出來了），
   缺席是「這台消費級平台沒有提供 HMAT」——即使現在寫好解碼器，本機也只會得到
   `NotApplicable`，**拿不到一次真機 golden**。這也是 P2-008 至今沒有動的原因之一。

---

## 五、下一輪的建議順序

1. **F 群（風險分數，6 把）**——唯一「不需要任何外部條件」的一群：
   它把既有事實（Secure Boot 四態、dbx 世代、Boot Guard、TPM 狀態、韌體版本、周邊韌體）
   收斂成可匯出的風險陳述。**但規則與權重必須由你圈定**，否則就是發明一套沒有出處的評分。
2. ~~**P2-098 `pdev.fw.updatable`**——只缺兩張有出處的欄位表（NVMe 韌體槽、ATA word 69），
   而且它與既有的 `pdev.fw.version` 是同一個來源家族。~~ **第 266 輪已做完**（見 `GATE-LOG-266`）；
   連帶修掉一個匯流排名稱表的缺陷（第六節）。
3. ~~C 群（3 把）的「結案」~~——**第 261 輪已做完**（`FirmwareVariableSurfaceFactsService`）。

---

## 六、第 266 輪的兩個更正（一個是我自己的判斷，一個是專案裡的表）

### 6.1 我把 ATA 判成「缺源」，那是錯的——錯在「用消費者的用法反推規格有沒有這一欄」

這一輪做到 P2-098 時，我一度把 ATA 那一半判成缺源，理由是 Linux 的 `include/linux/ata.h`：
word 69 是 `ATA_ID_ADDITIONAL_SUPP`，核心只用它的 TRIM 位（`0x4020`）與 zoned 位（`0x3`），
整份檔案沒有 firmware-download 的 IDENTIFY 欄位。**這個推理是錯的**：那份檔案只定義
**核心自己會用到的位元**，核心沒用到的位不代表規格沒定義。

換到規格原文（本機 Windows SDK 的 `km/ata.h`）一看，兩個位元明明白白：

| 欄位 | 位元 | SDK 標頭裡的名字 | 意義 |
|---|---|---|---|
| word 83（Command Set Support） | bit 0 | `DownloadMicrocode` | 「The DOWNLOAD MICROCODE command is supported」 |
| word 69（Additional Supported） | bit 8 | `DownloadMicrocodeDmaSupported` | DMA 版的同一件事 |

而且**本專案早就解碼了這兩個位元**（`AtaIdentify` 的 `FirmwareUpdateCapability`，三態，
有效性看 word 53 bit1）——我當時既沒查規格原文，也沒先搜專案裡有沒有人做過，
用一份第三方原始碼的使用情形就下了結論。**教訓：要否定一個欄位，看規格本身，不要看某個消費者怎麼用。**

順帶更正交接文件：它寫的「ATA IDENTIFY word 69」**基本正確**（搭配 word 83 bit0 更完整），
第 265 輪把它記成「交接寫錯」是錯的——錯的是我，已於本輪更正。

### 6.2 匯流排名稱表有兩份，兩份都位移一格——收斂成一份

同一輪把新的鍵接上真實路徑時，發現 `StorageSmartService` 與 `DiskInfoService` 各有一張
「BusType 數值→名稱」的表，兩張**都從列舉中段開始位移一格**，而且彼此不一致：

| BusType | 兩張表原本寫的 | SDK `STORAGE_BUS_TYPE` 的事實 |
|---|---|---|
| 6 | 「USB」（另一張寫 SSA） | `BusTypeFibre` |
| 9 | 「SAS」／「RAID」 | `BusTypeiScsi` |
| 10 | 「SATA」／「iSCSI」 | `BusTypeSas` |
| 16 | 「NVMe」／（缺） | `BusTypeSpaces` |
| 17 | 「SCM/NVMe 類」 | `BusTypeNvme` |

本機實測（CIM 的原始值）：NVMe 給 17、SATA 給 11——**與 SDK 列舉相符**，
所以錯的不是來源，是這兩張表。修法不是各修一份，是**只留一份**：
`StorageSmartService.BusTypeName` 與 `ChannelOf`（通道分群）成為唯一出處，
`DiskInfoService.BusZh` 與 SMART 直讀的分流都改呼叫它，並由
`Tests/FirmwareUpdatableFactsTests` 逐項釘住（16 個值 + 通道分群 + 「別處不得再有一張表」的原始碼掃描）。

> **為什麼值得記在這裡**：這兩張表的用途是「告訴使用者這顆碟掛在哪條匯流排」，
> 而**錯誤的標籤不會讓程式壞掉**——它只會安靜地把 SAS 講成 SATA。
> 這種缺陷單元測試抓不到，只有「拿規格列舉逐項對」才會現形。

---

## 七、這份文件不是承諾

它是一份「下一個人可以直接照著解鎖」的清單。任何一列解鎖後，
**請把該列從本文件移除，並在 `GATE-LOG-*.md` 記下出處**——
文件留著已完成的項目，就會變成另一種「看起來還在等工作」的假訊號。