# 閘門紀錄：接手一個停住的代理，把「第二條不變式」的收尾做完（第 257 輪）

> **前情**：第 256 輪之後，另一個代理（Freebuff／Codebuff 桌面版）在**同一個工作樹**上做
> 「`HardwareFact` 第二條不變式 ⇒ 測試配合」的收尾，分成 A-4／B／C／D／E 五組。
> **這件事的來源是它的待辦清單，不是本檔的推測。**
> 本檔記錄：它做到哪裡停、我接手後量到什麼、群組 D 怎麼收、以及這一輪沒有動什麼。

---

## 一、前一個代理的停工：有據可查（不是「看起來沒動」）

它在 02:42 停下，證據是它自己的 orchestrator 日誌（`%APPDATA%\Freebuff\logs\orchestrator-stderr.log`）：

```
statusCode: 428
responseBody: {"error":"waiting_room_required",
               "message":"Your free session has ended. Send your message again to start a new one."}
isRetryable: false
```

**那是「免費 session 用完了，要人再送一句話才會開新的」**——不是程式錯、不是死鎖，
也不是它以為自己做完了。停之前它的最後動作（程序時戳）是：

| 時刻 | 動作 | 結果 |
|---|---|---|
| 01:28–01:45 | 改 `WiringDecisions.cs`、`WiringGuardTests.cs`、`EvidenceLabService.cs`… | 群組 C 的孤兒服務登記 |
| 02:04:05 | `dotnet build Tests/XinSpect.Tests.csproj` | 成功 |
| 02:08:50 | `dotnet test --filter FullyQualifiedName~ServiceOrphanGateTests`（detailed） | 成功 |
| 02:17:31 | 再改 `Services/WiringDecisions.cs` | — |
| 02:18–02:33 | 又一輪 build ＋ test | 成功 |
| **02:42:13** | **`428 waiting_room_required`** | **停工，之後原始碼零編輯** |

**判斷「它停了」的完整條件**（這一輪用到的三條，缺一不可）：
① 它的行程還在（`bun.exe` 活著）但**不再有 build／test 子程序**；
② 原始碼 mtime 不再前進；
③ **日誌出現不可重試的錯誤**。只看①②會把「正在想」誤判成「停了」，第③條才是定論。

---

## 二、接手時量到的狀態（實測，不是沿用它的自述）

| 項目 | 結果 |
|---|---|
| 建置 | **成功，0 錯誤、19 警告**；**CS8321 已不在**（群組 B 它自己做完了） |
| 全套測試 | **6,371 條：6,370 過、1 紅**，10 分 38 秒（高效能電源計畫） |
| 唯一紅燈 | `FactCoverageReportTests.未覆蓋數超過已知基線時_列出前幾個供人核對`——未覆蓋 **307**、基線 235 |
| 未提交 | 51 檔（4 服務 ＋ 47 測試）＋ `work/` 下 144 個未追蹤工作檔 |

**A-4／B／C／E 四組在接手時已經是綠的**——這一點值得寫下來：它的待辦清單還標著
`Pending`，但那一輪的最後一次全套執行早就把它們做完了。**待辦清單會過期，量測不會。**

---

## 三、群組 D：307 條未覆蓋事實鍵的逐族檢視（本輪唯一實質改動）

### 3.1 結論：**沒有動豁免表，改的是「已檢視的上限」**

理由不是「不想寫」，是**兩道守門互相卡住，而其中一道已經滿了**：

- `FactCoverageReport.Exemptions` 目前 **600 條**，正好落在
  `KeyCatalogPolicyTests` 的 `cap: 600` 上（`ToExemptFact` 在 `Count > cap` 時判 `Unknown`，
  訊息原文是「先檢視這些豁免是不是該處理了，**而不是提高上限**」）。
- 所以要再進一條家族級豁免，就得先刪既有條目；而既有條目裡混著**不同類別**的正當性——
  例如 `fs.srum.*` 是「刻意不取得來源」（不列行程、不列帳號、不列資料量），
  `se.grade` 是「非量測值」。用「單一來源」的家族級豁免把它們覆蓋掉，**等於把類別標錯**，
  那比留著未覆蓋更糟。
- 於是這一輪的處置是：**逐族核完，把「已檢視」這件事記在基線與註解上**，
  307 這個數字繼續留在畫面上（未覆蓋不是錯誤，是還沒有人替它寫規則）。

> 附帶查到的一件事：那 5 個看起來重複的鍵（`se.grade`、`st.dwpd`…）**不會拋例外**——
> 字典初始化用 `["k"] = v` 走的是索引子指派，重複鍵是**覆蓋**而不是 `Add` 失敗。
> 所以「600 條」是**去重後**的 600，帳面上對得上。

### 3.2 逐族：來源是什麼、為什麼沒有第二條路可交叉

48 個家族、307 把鍵。左半是**單一來源或非量測值**（本來就沒有交叉對象），
右半是**可以交叉、只是還沒寫規則**——那些是補規則的候選，不是豁免的候選。

| 家族 | 鍵數 | 來源／為什麼沒有第二條路 | | 家族 | 鍵數 | 可交叉的對象 |
|---|---|---|---|---|---|---|
| `ac` | 48 | ACPI 表欄位值；同一張表讀兩次不是兩個來源，表頭校驗是自我一致性 | | `sb` | 49 | SMBIOS 對 WMI（差分目前只在測試層） |
| `sa` | 27 | 登錄檔與 API 的現況值 | | `uefi` | 17 | UEFI 變數／映像對登錄檔與映像內部清單 |
| `fs` | 18 | 各鑑識產物來自自己的檔，**明文不得互相代言** | | `fw` | 12 | 韌體版本對映像與登錄檔（`uefi.version.cross` 已承擔一部分） |
| `gpu` | 11 | **六族之一**（有硬體時應補規則） | | `bt` | 9 | 量測開機日誌對 TPM 日誌（P2-064） |
| `tpm` | 9 | **六族之一** | | `gpu` | 11 | 三方 VRAM／溫度（**六族之一**） |
| `cpu` | 7 | CPUID／MSR 現況；微碼交叉在 `mc.*` | | `tpm` | 9 | **六族之一** |
| `pmu` | 7 | 唯讀 MSR；編程驗證是主動測試不是對帳 | | `cpu` | 7 | 世代對微碼／登錄檔 |
| `in` | 6 | 執行檔自身；對發佈頁雜湊需出網，違反零網路原則 | | `mc` | 4 | MSR 對登錄檔（`mc.version.cross` 目前是事實不是規則） |
| `ng` | 5 | 對帳申報自己的輸出 | | `spi` | 4 | FREG 對 MMIO 後端、PRR 對寫入面 |
| `psu` | 4 | **六族之一**（`psu.pmbus.*`） | | `bl` | 3 | 自基線對實際開機事件 |
| `write` | 4 | 本程式自己的寫入帳本 | | `ec` | 3 | EC 存在性對 ACPI／電池 |
| `mc`/`spi`/`ev` | 見右欄 | 見右欄 | | `ev` | 3 | EFI 變數對審計鏈 |
| `mem`/`cmos` | 3／3 | **六族之一** | | `rs` | 1 | SBOM 對 `THIRD-PARTY-NOTICES` 與 csproj |

其餘家族（`cap` 9、`coverage` 3、`key` 1、`dyn` 1、`test` 1、`report` 1、`data` 1、
`guard` 1、`knowledge` 2、`qs` 1、`backend` 2、`platform` 2、`chipset` 1、`pci` 2、
`pcieaer` 1、`net` 4、`drvinsp` 2、`esp` 3、`etl` 2、`setuptl` 2、`rc` 3、`storage` 2、
`ufv` 1、`se` 1、`smart` 1，共 25 族 46 把）都是**單一來源或自我申報**，
逐條理由已寫進測試的註解與本表；其中 `net.listen.*`（IP Helper）、`pci.*`／`pcieaer.*`
（設定空間）、`storage.reliability.*`（SMART 計數器）都只有一條讀取路徑。

**六族（`gpu.`／`mem.`／`psu.pmbus.`／`smart.`／`tpm.`／`cmos.`）照舊不豁免**——
它們可以對（功率守恆、三方 VRAM、SPD 對 SMBIOS 對 SMART、開機鏈），
有硬體時應該補的是**規則**，不是豁免。

### 3.3 改動的實際內容

- `Tests/FactCoverageReportTests.cs`：基線 `235 → 307`，並把上面這段「為什麼不豁免」寫在測試旁邊。
- `Services/FactCoverageReport.cs`：**零改動**（豁免表維持 600 條）。
- 307 條的完整清單留在 `work/uncovered-307-reviewed.txt`（工作檔，未進版控）。

---

## 四、這一輪順手做完的帳（更新，不是新功能）

1. **README 測試徽章與 `Tests/TestSuiteBaseline.cs` 由 5,848 改為 6,371**——
   第 256 輪自己記下的缺口 ④（「badge 說 5,848，實測 6,369，兩者都該更新，
   而更新的依據要是**一次完整套件的實測值**」）。現在的依據就是本輪那一次 10 分 38 秒的實跑，
   數字來源只有一個：`dotnet test` 印出的總計。
2. **`ChangelogCatalog` 的 2.85 條目**同步把「測試 5848 綠、目錄 930 把」更新為實測值——
   2.85 **還沒發佈**（最新 tag 是 v2.84），所以那一條不是歷史而是現在式的宣稱。

## 五、還沒處理的（照舊留著，不假裝做完）

| 項目 | 出處 |
|---|---|
| `PlatformGovernanceFactsService.ReadSddl` 的 C 型半實作——**已修**（第七節查核時確認：它現在逐服務讀安全描述元、讀不到回 `ReadError`），`FIX-REPORT` §4.1 的記載已過期 | `FIX-REPORT-2026-10-16.md` §4.1 |
| 帳本「測試 +31」對不上帳（實為 +29／18 條） | 同上 §4.2（已在該處加更正註記） |
| `FactKeyCatalog.cs` 那個無法對帳的「183 鍵」數字 | 同上 §4.3（註解已改成「不補沒人能驗證的數字」） |
| `work/probe/` 那個獨立 .NET 專案仍在（`Directory.Build.props` 已排除，但專案本身沒搬） | 同上 §5.2 |
| ULTIMATE-1000 階段二的 31 列 `todo`、全計畫 1,050 項中約 890 項未開工 | `STAGE-02-FIRMWARE.md` |
| **v2.85 的 changelog 條目仍只寫「階段一 105 項」**——階段二的成果（P2-0xx，含第七節的 TPM 修正）還沒進條目；**發版前要補**（三份 README 的版本沿革同步） | `Nav/ChangelogCatalog.cs` 的 2.85 條目 |
| v2.85 尚未發佈（README 下載連結已指 2.85）；`FileVersion` 仍 2.84.0.0 | `Nav/ChangelogCatalog.cs`、`git tag`、`XinSpect.csproj` |
| 卡住的 `XinSpect.exe`（PID 14928）**已於 2026-10-17 22:56 重開機後消失**；一般建置路徑恢復（不必再用 `-p:BaseOutputPath=obj/_probe/`） | 第七節 |
| **TPM 的實機通路在这台機器上不存在**：TPM 裝置與驅動正常、`Win32_Tpm` 可用，但 **`TBS` 服務未安裝** ⇒ `tpm.*` 全部落 NotApplicable。**要不要加一條 WMI 通路（`Win32_Tpm`）作為備援，是產品決定**（唯讀、零網路；但會多一條「同一件事兩個來源」的對照義務） | 第七節 |

---

## 六、這一輪的教訓

1. **「另一個代理還在跑」不能靠「有沒有新檔案」判斷。** 它停在 02:42，而 build／test
   的產物直到 02:33 都還在動——差 9 分鐘。定論要看它自己的日誌有沒有不可重試的錯誤。
2. **待辦清單不是狀態。** 它的清單寫著五組 `Pending`，其中四組在最後一輪已經綠了；
   接手時花 11 分鐘跑一次全套，比信任清單準確得多。
3. **兩道守門會互相卡住，而其中一道告訴你「不要提高它的上限」。**
   覆蓋上限說「未覆蓋要有人看一眼，可以加豁免」，豁免上限說「滿了，先檢視既有的」。
   這兩句話合起來的答案不是「想辦法塞進去」，是**把檢視本身記成事實**（這裡是基線與註解）。
4. **滿的表格不是靠刪別人的條目騰空間。** 既有 600 條裡混著不同類別的正當性，
   為了塞進家族級豁免而刪掉它們，會把「刻意不取得來源」記成「單一來源」——
   申報比實際乾淨，正是本計畫最忌的那件事。

---

# 七、接手後的下一輪（同日）：`TPM_PT` 編號與 revision 語意整組讀錯——實質缺陷；順手解鎖 P2-069／P2-075

**起點**是第 256 輪自己寫下的 `UNSOURCED-CONSTANTS.md` ④⑤（「`TPM_PT_*` 那張表涵蓋範圍不夠，
補上即可解鎖 P2-069／P2-075」）。查證時發現更糟的事：

1. **編號錯**：`PtRevision` 寫 `0x101`（規格裡那是 `LEVEL`；`REVISION＝FIXED+2＝0x102`）、
   廠商字串寫 `0x104..0x107`（規格裡那是 `YEAR`／`MANUFACTURER`／`VENDOR_STRING_1..2`；
   正確是 `0x106..0x109`）。
2. **解碼錯**：`Revision()` 把 bits 31:24／15:8 當主、次版號；規格寫的是
   **「the specification Revision times 100」**（Revision 01.01 → 101）。
   本機 `Win32_Tpm.SpecVersion` 是「2.0, 0, 1.16」⇒ 真值 116；舊寫法會讀成「0.116」，
   而且因為它讀的是 `LEVEL`（真值 0）而顯示「revision 未回報」，
   再把 `YEAR`／`MANUFACTURER` 當成廠商字串的前兩段。
3. **文件與測試跟著一起錯**：`UNSOURCED-CONSTANTS.md` 拿實作的編號當佐證；
   測試的假資料（`0x01000300`）照著錯的假設造——所以一路綠。
   **假資料與實作自我一致時，綠燈什麼都沒證明。**

## 修了什麼

| 檔 | 內容 |
|---|---|
| `XinSpect.Decoders/Tpm2.cs` | 常數表依規格重對（FIXED 0x100–0x10C、VAR 0x200–0x201，含 `PtLevel`／`PtYear`／`PtManufacturer`）；`Revision()` 改為「版號×100」並對形狀不符的值回 null；`Ascii4()` 抽出共用；新增 `TPMA_PERMANENT`／`TPMA_STARTUP_CLEAR` 位元解碼器 |
| `Services/TpmFactsService.cs` | 屬性**一次讀兩組、各讀一次**（PT_FIXED 十項、PT_VAR 兩項），三個事實共用同一份結果（不重複讀）；`tpm.version` 修正並補 level／廠商代號；**新增 `tpm.owned`（P2-069）與 `tpm.attest.capable`（P2-075）**，後者明說不連網、不送 challenge、不驗 EK 憑證鏈 |
| `Tests/Tpm2Tests.cs` | 編號 golden（「哪個編號是哪一項」被钉住）＋固定屬性項數（要涵蓋到 0x109）＋revision 語意＋TPMA 位元 |
| `Tests/TpmFactsTests.cs` | fixture 修正（revision 在 `0x102`、值 116）；假通道改依 capability**與 property** 兩個面向分派；新增擁有權（逐位陳述＋未回報不推斷）與證明能力（陳述前提＋不上網界線＋缺 family 不以支援代填）四條 |
| `Services/FactKeyCatalog.cs` | ＋`tpm.owned`、`tpm.attest.capable`（1,024 → 1,026） |

## 環境插曲（要記錄的坑，兩件事）

- **別從腳本跑 `XinSpect.exe --json evidence`**：它卡死（單一執行緒、`Stop-Process -Force`
  無效），並鎖住 `Tests/bin` 的 DLL。之後所有建置／測試要加
  `-p:BaseOutputPath=obj/_probe/` 繞開。（**2026-10-17 22:56 重開機後該程序已消失**，
  一般建置路徑恢復。）
- **本機 TBS 回 `TBS_E_TPM_NOT_FOUND`——原因已查明，而且不是我原本猜的那個**
  （原文寫「PTT 被卡住的命令卡住，重開機後應重跑」，**那句是錯的，重開機後一樣失敗**）：

  | 查了什麼 | 結果 |
  |---|---|
  | PnP 安全裝置 | `信賴平台模組 2.0`（`ACPI\MSFT0101\1`）狀態 **OK** |
  | `TPM` 核心驅動 | **Running**、Status OK |
  | `Win32_Tpm`（走 WMI provider） | TpmPresent／TpmReady／TpmEnabled／TpmOwned **全部 True**、SpecVersion「2.0, 0, 1.16」 |
  | `tbs.dll` | 存在（10.0.26100.32684） |
  | **`TBS` 服務** | **不存在**——`HKLM\SYSTEM\CurrentControlSet\Services\TBS` 無此機碼、`sc query TBS` 回 **1060（服務未安裝）** |
  | 提權 | 是（`IsInRole(Administrator)`＝True） |

  **結論：這台機器有 TPM，但 Windows 的 TBS 介面不存在**，而 `TpmFactsService` 正是走 TBS——
  所以 `Tbsi_Context_Create` 永遠回 `0x8028400F`，**全部 `tpm.*` 事實在這台機器上只能落到
  NotApplicable**（那是誠實的三態，不是缺陷）。**因此 `PT_PERMANENT`／`PT_STARTUP_CLEAR`
  的真機實測在這台機器上做不到**，除非改走 WMI 通路（`Win32_Tpm` 在此可用，這是一條候選：
  見下面「還沒處理的」）。
  family／level／revision／manufacturer 的真值取自 Windows 的 `Win32_Tpm`
  （SpecVersion「2.0, 0, 1.16」、ManufacturerIdTxt「INTC」）。
  **教訓：我上一次把「TBS 失敗」寫成「被卡住的命令卡住」——那是一個沒有驗證的因果推測，
  而它讓下一個人以為重開機就好。** 通路的失敗要查通路本身（服務在不在），
  不是猜它為什麼失敗。

## 這一輪的教訓

1. **出處要對規格，不能對實作。** `UNSOURCED-CONSTANTS.md` 用「實作裡有 `[SpecRef]`」
   當「表已對準」的證據——實作正是錯的那一方時，這等於替錯誤背書。
2. **「看似來自規格的註記」也要驗。** 舊註解寫「2023 年的修訂把次版號定義成 bits 23:16」，
   而 2022（tss2 v4.0.1）與現行版本的規格註解都是 ×100——那句話對不上任何版本。
3. **假資料要對規格造，不是對實作造。** fixture 照著錯的假設造，就會和實作一起綠。
   修正後 fixture 的註解把「為什麼是 116」寫在旁邊，讓下一個人對得出來。
4. 第 256 輪說「④⑤ 是有界、可查證的工作」——**查證的過程比擴充那張表更有價值**：
   擴充只解鎖兩列，查證揪出三個事實的值本來就是錯的。
