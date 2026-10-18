# 階段二剩餘 22 把待辦鍵：卡在哪、怎麼解

> **這份文件的用途**：把「還沒做的 22 把鍵」按**卡住的原因**分類，而不是按項目編號排列。
> 專案裡既有的待辦清單（`STAGE-02-FIRMWARE.md` 矩陣）回答「哪一項沒做」，
> 但沒回答「為什麼沒做、需要什麼才能做」——於是每一輪開工前都要重新 grep 一遍，
> 而重新 grep 的結論每次都不一樣（`UNSOURCED-CONSTANTS.md` 記過四次這種盤點）。
>
> **資料時點**：2026-10-18，矩陣 `ok 68／alias 24／todo 22`（114 把鍵、105 項）。
> 第 260 輪完成 P2-105、第 261 輪把 C 群三把（P2-082／089／090）收口成「平台不提供」之後重算；
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
| P2-098 | `pdev.fw.updatable` | **D（小）** | 逐裝置「可更新」的來源：NVMe Firmware Slot Information、ATA IDENTIFY word 69 | **可能可以**：兩者都是有出處的欄位，但需要先核對規格原文 |
| P2-099 | `fw.risk.facts.parts` | **F** | 風險分數由哪些既有事實組成 | 可以（純本機），但規則是決策 |
| P2-100 | `fw.risk.facts.level` | **F** | 分級門檻 | 同上 |
| P2-101 | `fw.risk.facts.gap` | **F** | 缺項對分數的影響 | 同上 |
| P2-102 | `fw.risk.facts.baseline` | **F** | 基線格式與匯出 | 同上 |
| P2-103 | `fw.risk.facts.delta` | **F** | 兩次基線的差分定義 | 同上 |
| P2-104 | `fw.risk.facts.report` | **F** | 報告格式（給第三方複核） | 同上 |
| P2-105 | `fw.doc.reconcile` | — | **本輪完成** | ✅ |

---

## 三、依卡法分組（同一批可以一起解）

- **A（TBS，5 把）**：P2-060／066／067／070／077。**同一個開關**：機器要有 TBS 服務。
  有 TBS 之後，這五把是「既有重放結果的再切分」，不是新讀取路徑。
- **B（ring0，4 把）**：★P2-036／086／087／088（P2-053 還要加上規格原文）。
  同一個開關：驅動載入 + 讀得到快閃。
- **C（沒有 API，3 把）**：P2-082／089／090（**只有這三把**——P2-081 曾經被歸在這裡，但它的 C 成分是「讀不到 dbx」，那是缺陷不是平台限制）。**已於第 261 輪收口**：
  三把都以 `NotSupported` 陳述「Windows 沒有列舉 EFI 變數的公開 API」，
  矩陣標成 `ok:`（收口也是完成——留著 todo 只會讓帳本宣稱它們在排隊等實作）。
  這一群沒有「還沒解鎖」的列了，所以它從下面的建議順序裡消失。
- **D（規格原文，3 把）**：P2-008／029／098（P2-053 的 MSR 半邊）。**解鎖條件是離線規格**。
- **E（知識包，3 把）**：P2-074／081／097（P2-081 原本另有「讀不到 dbx」的成分，**那已於 2026-10-18 修掉**）。**解鎖條件是資料**。
- **F（規則，6 把）**：P2-099～104。**解鎖條件是決定**，不是能力。

---

## 四、本機實測（2026-10-18，這一輪真的跑過的）

```
TBS 服務：Get-Service TBS → 不存在（不是停止，是沒有這個服務）
ACPI 表列：DBGP MCFG FACP SRAT MSCT SLIT DMAR APIC HPET FPDT FIDT MIGT PCAT PCCT
           RASF SVOS WDDT OEM4 OEM1 SSDT NITR SSDT LPIT WSMT SSDT DBG2 BGRT WPBT TPM2
           → 沒有 HMAT（也沒有 NFIT／PPTT）
EFI 變數：GetFirmwareEnvironmentVariableExW("BootOrder") → 0 bytes，GetLastError 1314
           （ERROR_PRIVILEGE_NOT_HELD）——而該行程是 elevated
ring0：服務清單沒有 XinSpect／WinRing0 項目；XinSpect.sys 只存在於舊的 publish 目錄
規格原文：uefi.org 在本機被 DNS 解析到 198.18.0.12（保留位址），DMTF PDF 也取不到
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
2. **P2-098 `pdev.fw.updatable`**——只缺兩張有出處的欄位表（NVMe 韌體槽、ATA word 69），
   而且它與既有的 `pdev.fw.version` 是同一個來源家族。
3. ~~C 群（3 把）的「結案」~~——**第 261 輪已做完**（`FirmwareVariableSurfaceFactsService`）。

---

## 六、這份文件不是承諾

它是一份「下一個人可以直接照著解鎖」的清單。任何一列解鎖後，
**請把該列從本文件移除，並在 `GATE-LOG-*.md` 記下出處**——
文件留著已完成的項目，就會變成另一種「看起來還在等工作」的假訊號。