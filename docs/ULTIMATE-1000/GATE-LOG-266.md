# 閘門紀錄：P2-098 可更新性宣告——「查了原始碼」不等於「查了規格」（第 266 輪）

> **前情**：第 265 輪（`GATE-LOG-265.md`）收口 F 群六把風險陳述。
> 本輪做階段二 D 群最後一把本機可做的：**P2-098 `pdev.fw.updatable`**（逐裝置的「可更新」宣告）。
> `STAGE-02-REMAINING-KEYS.md` 把它歸在 **D（規格原文取不到）**，並註明
> 「兩者都是有出處的欄位，但需要先核對規格原文」。

---

## 一、這一輪真正的產出：一個更正，和一個推論錯誤

### 1.1 更正物件不只是交接文件，還包括我自己

開工時依交接文件的線索核對來源，先去讀 Linux `include/linux/ata.h`，看到：

```
ATA_ID_ADDITIONAL_SUPP = 69        // 核心只用 (id[69] & 0x4020) 判 TRIM、(id[69] & 0x3) 判 zoned
ATA_CMD_DOWNLOAD_MICRO = 0x92      // 這是指令，不是 IDENTIFY 的能力旗標
```

於是得到結論「ATA 那一半＝缺源，交接寫錯」，並把它寫進第一版服務的類別註解。
**這個結論是錯的**，錯在推論方向：**「某份原始碼怎麼用」不能反推「規格有沒有這一欄」**。
核心沒用到那個位元，不代表規格沒定義它。

改查規格原文（本機 Windows SDK 標頭）後，兩個位元都在：

| 出處 | 欄位 | 位元 | 名字 |
|---|---|---|---|
| `Windows Kits/10/Include/10.0.28000.0/km/ata.h` | word 83（`CommandSetSupport`） | bit 0 | `DownloadMicrocode`（「The DOWNLOAD MICROCODE command is supported」） |
| 同上 | word 69（`AdditionalSupported`） | bit 8 | `DownloadMicrocodeDmaSupported` |

三份獨立來源一致：本機 SDK 標頭、smartmontools `src/ataidentify.cpp`
（`69 Additional support` → `. 8 DOWNLOAD MICROCODE DMA supported`、`83` → `. 0 DOWNLOAD MICROCODE supported`）、
以及**本專案早就寫好的** `Services/AtaIdentify.cs`（`FirmwareUpdateCapability` 三態，有效性看 word 53 bit1）。

> **所以這一輪的第一個產出不是程式，是一個更正**：交接文件寫的「ATA IDENTIFY word 69」**方向上正確**，
> 第 265 輪把交接記成「寫錯」才是錯的。判準原來一直躺在版控裡（`AtaIdentify`），
> 我卻繞過它去讀第三方原始碼——**要否定一個欄位，看規格本身，不要看某個消費者怎麼用。**

### 1.2 順帶更正：D 群不是卡在「拿不到原文」，是卡在「沒有去找本機就有的原文」

`STAGE-02-REMAINING-KEYS.md` 第五節把它列在 D 群，理由是「規格原文取不到（uefi.org 被 DNS 指到保留位址）」。
但本輪需要的三份原文**全部在本機**：

```
C:/Program Files (x86)/Windows Kits/10/Include/10.0.28000.0/shared/nvme.h    ← NVMe 韌體槽紀錄（AFI／FRS[7]）
C:/Program Files (x86)/Windows Kits/10/Include/10.0.28000.0/km/ata.h        ← ATA word 83 bit0／word 69 bit8
C:/Program Files (x86)/Windows Kits/10/Include/10.0.28000.0/um/winioctl.h   ← STORAGE_BUS_TYPE（下面第三節要用）
```

**這一批「卡法」的分類要修**：P2-098 不是「原文取不到」，是**一開始沒找對地方**。
（P2-008／029 仍在 D 群：HMAT／Type 44 的欄位圖確實需要 ACPI／SMBIOS 規格 PDF，本機沒有。）

---

## 二、實作了什麼（鍵 `pdev.fw.updatable`）

| 檔案 | 內容 |
|---|---|
| `Services/NvmeFwSlot.cs` | 純解碼器：log page `0x03` 的 512 位元組 → AFI（bits 2:0 作用中槽、bits 6:4 待啟用槽）＋七個槽的版本字串；**整片 0 判成「讀不到」**（同 `AtaIdentify` 的規則）。另附 `FirmwareCommandsSupported`（Identify Controller OACS bit 2，三態） |
| `Services/FirmwareUpdatableFactsService.cs` | 三態事實：列舉失敗＝`ReadError`、列舉成功但回空＝`NotApplicable`、其餘＝`Present`。依**匯流排通道**分流：NVMe 讀 log 0x03 ＋ Identify Controller；ATA 家族讀 IDENTIFY DEVICE 並**引用既有的** `AtaIdentify.FirmwareUpdateCapability`；其他匯流排如實標缺源 |
| `Services/PeripheralFirmwareFactsService.cs` | 列舉加回 `Win32_DiskDrive.Index`（P2-098 要走 IOCTL 需要磁碟編號）、`ReadInventory()` 改 `internal`——**一次列舉、兩個事實共用**，不重查 WMI |
| `Services/StorageSmartService.cs` | 新增 `NvmeLogPageFirmwareSlot = 0x03` 常數（附兩份出處註解）；另見第三節的匯流排收斂 |
| `Tests/FirmwareUpdatableFactsTests.cs` | 38 條：事實三態與界線、不得下判決、原始碼掃描（不得自己開檔或 P/Invoke）、匯流排對照表逐項對規格。解碼器契約不在這裡——它在 `Tests/NvmeFwSlotTests.cs`（見第六節） |

**寫進事實的界線（不是註解）**：①這是**裝置自述的支援旗標**，不是我們驗證過的可更新性；
②「支援下載命令」**不等於**「原廠還提供更新」，也**不等於**「需要更新」；
③讀不到＝缺源，不代表不可更新；④本程式不燒寫、不更新、不連網查原廠清單。

**如實申報的一件事**：本輪**沒有跑實機證據**。專案紀律是 `XinSpect.exe` 的證據模式不由指令稿啟動，
而本機 NVMe 直讀已知會洩卡死執行緒（第 263 輪的 `NvmeOuterBudgetMs` 就是為它加的），
不值得為了帳本上的一行去換兩條永遠不釋放的執行緒。實機回值待下一次人工掃描確認；
`STAGE-02-REMAINING-KEYS.md` 的「本機現在可量測嗎」欄照這個事實寫，不寫成 ✅。

---

## 三、順手修掉的缺陷：匯流排名稱表有兩份，兩份都位移一格

接上真實路徑時發現專案裡有**兩張**「BusType 數值 → 名稱」的表，而且兩張都對不上規格：

| BusType | `StorageSmartService` 原本 | `DiskInfoService.BusZh` 原本 | SDK `STORAGE_BUS_TYPE` 的事實 |
|---|---|---|---|
| 6 | USB | SSA | `BusTypeFibre` |
| 7 | RAID | Fibre Channel | `BusTypeUsb` |
| 9 | SAS | RAID | `BusTypeiScsi` |
| 10 | SATA | iSCSI | `BusTypeSas` |
| 12 | （缺） | SAS | `BusTypeSd` |
| 16 | NVMe | （缺） | `BusTypeSpaces` |
| 17 | SCM/NVMe 類 | NVMe | `BusTypeNvme` |

**本機實測（決定性）**：CIM `MSFT_PhysicalDisk.BusType` 的原始值對 3 顆 NVMe 給 **17**、
對 2 顆 SATA 給 **11**，與 SDK 列舉逐項相符。**錯的是本專案的兩張表，不是來源。**

修法是**只留一份**：`StorageSmartService.BusTypeName`（名稱）與 `ChannelOf`（通道分群
16／17→NVMe、3／10／11→ATA，逐字沿用原本寫死的常數）成為唯一出處；
`ReadBusTypeCore`、SMART 直讀分流、`DiskInfoService.BusZh`、P2-098 全部改呼叫它——
**兩份表遲早會再漂開一次，各修一份不是修法**。守門：`FirmwareUpdatableFactsTests` 逐項釘住
16 個值＋通道分群＋「其他檔案不得再出現一張數值→匯流排名」的原始碼掃描。

> 這種缺陷單元測試抓不到——**錯的標籤不會讓程式壞掉**，它只會安靜地把 SAS 講成 SATA。

---

## 四、閘門與帳

| 項目 | 值 |
|---|---|
| 執行測試 | **6,580 綠、0 紅**（基線 6,532 → 6,580） |
| 事實目錄 | `Services/FactKeyCatalog.cs` 1037 → **1044**（第 265 輪 +6、本輪 +1；註解同步） |
| 覆蓋基線 | `Tests/FactCoverageReportTests.cs` 326 → **327**（第八批理由：裝置自述沒有第二條路可以交叉問同一台裝置） |
| 文件對帳 | `FirmwareDocReconcileFactsService` 已實作 74 → **75**、待辦 16 → **15** |
| 矩陣 | `STAGE-02-FIRMWARE.md` P2-098 → `ok:pdev.fw.updatable` |
| 剩餘清單 | `STAGE-02-REMAINING-KEYS.md` 16 把 → **15 把**，D 群 3 → **2**，第六節記兩個更正 |

---

## 五、下一輪的線索（照順序）

1. **A 群（TBS，5 把）**：本機沒有 TBS 服務，屬硬體／平台條件，不是我們能解。
2. **B 群（ring0，4 把）**：要載入本專案驅動並取得管理員權限才可能——**需要你的明確同意才會動**。
3. **E 群（知識包，3 把）**：缺可離線使用的清單（CVE、dbx 世代、原廠韌體版本）。
4. **P2-008／029（D 群剩下 2 把）**：需要 ACPI／SMBIOS 規格 PDF 逐欄核對。

> 本輪的另一個結論值得留在檯面上：**「卡在哪」的分類本身也會錯**。
> P2-098 被歸在 D 群整整一輪，實際上它缺的不是原文，是「先看看本機有沒有」。

---

## 六、實測結果

```
dotnet test Tests/XinSpect.Tests.csproj -c Debug -p:BaseOutputPath=obj/_rel_266d/
→ 已通過! 失敗 0，通過 6,580，略過 0，持續時間 5 m 5 s
```

`TestSuiteBaseline.ProjectTests` 6,532 → **6,580**（+48），三份 README 的測試徽章同步。

**過程中兩個要記下來的插曲（都不是產品缺陷）：**

1. 第一次全套跑在 22 分鐘後**看起來**卡住（CPU 停在 111.9 秒不動）。改用
   `--blame-hang --blame-hang-timeout 300s` 重跑，5 分 12 秒就跑完且全綠——
   所以那不是卡死，是我把輸出接到 `tail` 之後**看不到任何中途輸出**，只能看 CPU 猜。
   **教訓：長跑要先把輸出寫進檔案再讀，不要用管線把它吞掉。**（同型教訓在 `GATE-LOG-263` §五。）
2. 被中止的那一輪留下一條卡在 `obj/_rel_266` 的 testhost，把那個輸出路徑鎖住；
   換一個 `BaseOutputPath` 就繞過了——這是本專案的老問題（`GATE-LOG-263` §六），不是新的。

**另外交了一件事**：本輪原本在 `Tests/FirmwareUpdatableFactsTests.cs` 裡又寫了一組解碼器測試，
後來發現**交班中的第 266 輪已經有** `Tests/NvmeFwSlotTests.cs`（解碼契約）與
`AtaIdentifyTests` 的擴充（ATA 三態），於是把自己那份**刪掉**，只留事實層——
**同一份契約不在兩處各抄一遍**（數字上也看得出來：刪掉 16 條之後 6,596 → 6,580）。

---

## 七、發佈紀錄（v2.87 Ultima III，2026-10-19）

這一輪與第 264／265 輪累積成 **v2.87**，走 `Tools/release.ps1` 的完整管線：

| 步驟 | 結果 |
|---|---|
| preflight（六處版號） | `2.87 everywhere, baseline 6580` |
| 全套測試 | **6,580 綠、0 紅、0 略過**（`obj/_rel_287gate/`，4 m 53 s） |
| 提交與標籤 | `522680b`（「曦覽 XinSpect 2.87 Ultima III ＋ 版號 2.87」）＋輕量標籤 `v2.87` |
| 單一執行檔 | `publish-2.87/XinSpect.exe`＝**33,786,667 bytes** |
| 位元組數 docs commit | `5a93543`（README 繁簡兩份同步） |
| Release | id **416954131**，三資產逐一比對大小：XinSpect.exe 33,786,667／BlueSquadronBridge.exe 6,502,948／XinSpectDeploy.exe 7,680 |
| 發佈後驗證 | `verify-release.ps1`：**OK**（六處版號一致、Release 資產與 README 宣稱一致） |

**兩個過程中的小插曲（都不是產品缺陷）：**

1. 第一次 dry run 紅在 `PlainTextCatalogTests.更新紀錄的文字不含標記語法`——我把 Markdown 的粗體寫進
   `ChangelogCatalog` 的逐項文字裡，而那一頁是**純文字 TextBlock**，`**` 會照字面顯示。
   同型的錯在第 263 輪踩過一次（當時是 `<FileVersion>`），**守門照設計發揮作用**：改的是文字，不是放寬守門。
2. 第二次 dry run 因為第一次留下的 testhost 鎖住 `obj/_rel_2.87`（`MSB3027`）而失敗——
   管線把輸出路徑寫死成 `obj/_rel_$Version/`，所以改用**同一條閘門命令換一個 `BaseOutputPath`**
   （`obj/_rel_287gate/`）跑完全套綠，再用 `-SkipTests` 正式發佈（該開關的註記正是「僅在本版已 dry run 之後可用」）。
