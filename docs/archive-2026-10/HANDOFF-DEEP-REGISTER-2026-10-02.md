# XinSpect 深層暫存器計畫 — 移交 Z Code（2026-10-02）

> 自足移交文件：讀完這份就能無上下文接手。所有檔案路徑相對 `C:\Users\Administrator\XinSpect`。
> 本檔放在 repo 根、**未追蹤**（像既有 HANDOFF-*.md）；請勿 `git add .`，只 add 明確路徑。

## 0. TL;DR
把 XinSpect 推到「讀到沒人讀的那一層」(MSR/PCI/MMIO/ACPI/晶片組安全暫存器)，**同時守住誠實原則：讀不到就說讀不到，絕不以 0／0xFF／舊值填補**。
分五階段 P0–P4。**P0、P1 已完成並上線；P4 的北極星核心(逐位元組差分)與 P3 的 AER 解碼器已完成**。全程嚴格 TDD，**全套測試 2546 綠**。剩餘主要卡在 **P2 自家核心驅動**——那一步要在生產機載入未驗證核心碼，**不自動做**，留給使用者/開發機。

> **2026-10-02 傍晚更新**：P0–P5 managed 側全數落地（SPI/ECAM AER 三態、IMmioReader seam、XsRegProbe 驅動源碼＋窄路簽章手冊、豁免開關），並完成 **20 輪迭代**（`c3c77e2` 起，每輪一提交，詳見 §5）：驅動 IOCTL 後端與驗證器、握手探測、IA32_FEATURE_CONTROL／DEBUG_INTERFACE 事實、BIOS 寫入面綜合裁決、韌體安全頁警示色與分組、報告區塊、P4 原始快照收集／揮發遮罩／SHA-256 信封持久化、MCHBAR 基底探索、flaky 修復、PFX ACL 加固、MCFG 多 segment、Help 條目。全套 **2635+ 綠**。下一步＝使用者依 `XsRegProbe/BUILD-給使用者.md` 編譯簽章載入（repo 附 `XsRegProbe/Verifier` 心跳檢查），載入後三態事實自動翻真值。
> **坑**：SDK 10.0.400 起 WPF code-behind 裸 `Path` 與 System.IO.Path 歧義（三處已全名限定＋csproj `<Using Remove="System.IO">`）；repo 根 csproj 預設 glob 會吞 `XsRegProbe/**`（已加排除）。

## 1. 背景與兩條鐵則
緣起：使用者要的是「工程師的深度」——MSR 逐核頻率/降頻因果、PCIe AER、晶片組安全(BIOS 寫入保護)、ACPI 錯誤表、MMIO 記憶體控制器真實時序、全機逐位元組快照差分。
**鐵則一（身分）**：鎖「驗機/證據 + 韌體安全稽核」。效能 profiler(PMU/PEBS/PT)延後。
**鐵則二（誠實）**：每一項都要宣告讀不到什麼。`BIOS_CNTL` 讀不到就寫「讀不到(缺 ring0)」，不准寫「寫入保護：已啟用」。**假結論比沒有結論更糟**（沿用 `WinRing0BridgeTests` 的立場）。

## 2. 已鎖決策（使用者拍板）
1. **驅動**：放棄 WinRing0 的「通用任意讀寫 gadget」，改自家**白名單受限讀取**驅動 + **窄路簽章**（自簽 CA 進信任庫、只信我們這張；**不開全機 test-signing**）。建議從 BlueSquadron 骨架**分出獨立迷你兄弟驅動**（退出即卸載，不像 EDR 常駐）。
2. **快照**：新增**真正的原始逐位元組轉儲層**，與既有語義快照(`HardwareSnapshot`)並存。
3. **豁免**：做成**程式內「深層核心存取：開/關」+ 關閉鈕**（開=裝憑證信任+載驅動；關=移信任+卸載）。可逆、admin-gated、只信我們這張憑證。
4. **R-FW-01 驗機規則不做**：BIOS 寫入保護是「安全姿態」非「宣稱 vs 實際」對帳，塞 `VerifyRules` 語義錯配；改以獨立韌體安全視圖呈現(已做)。

## 3. 已完成（全驗證，2546 綠）

### P0 — 三態誠實骨幹（前置，無驅動）
- `Models/HardwareSnapshotModels.cs`：新增 `enum FactAvailability { Present, NotSupported, InsufficientPrivilege, ReadError, NotApplicable }`；`HardwareFact` 與 `HardwareSnapshotFact` 加 `Availability`(預設 Present) + `UnavailableReason`。後者用 `[JsonIgnore(WhenWritingDefault/WhenWritingNull)]` **omit-when-default** → 舊 `.xinsnapshot` 序列化逐位元組不變、SHA-256 完整性照舊通過，故 **schema 保持 v1、不需遷移**。
- `Services/HardwareSnapshotService.cs`：`ValidateFact` 放行「讀不到」事實(值可空、須帶原因、**不得帶 numericValue**)；`Diff` 的 `Equivalent` 納入 availability(轉不可用=Changed 非 Removed)；`ToPublicFact` 與 UI 入口轉換都帶上新欄位。
- `Services/EvidenceLabService.cs`：`EvidenceFactRow.ValueText` 與 `EvidenceChangeRow` 讀不到時顯示「讀不到／不支援／讀取失敗／不適用：原因」。
- 測試：`Tests/HardwareSnapshotServiceTests.cs`(+3)、`Tests/EvidenceRowTests.cs`(新,4)。

### P1 — usermode 深層（已上線）
- `Services/ChipsetSecurityService.cs`（新）：
  - 純解碼器 `ChipsetSecurity.DecodeBiosCntl`(BLE/BIOSWE/SMM_BWP)、`DecodeSmramc`(D_LCK/D_OPEN/D_CLS)、`DecodeHfs`(ME HFSTS1，依 coreboot `me_hfs`：working_state[3:0]、fw_init bit9、operation_mode[19:16])。
  - `IPciConfigReader` seam + `WinRing0PciConfigReader`(現用，Phase 3 換自家驅動)。
  - `ChipsetSecurityService.Collect(reader, at)` 產三態事實；HFS **先驗 0:16.0 vendor=0x8086** 才解讀，否則 NotApplicable。
- `Services/AcpiTableService.cs`（新）：`AcpiTable.TryParseHeader`(36-byte 表頭+校驗和)、`HestErrorSourceCount`、`BertBootErrorRegionLength`(BERT 錯誤記錄在實體位址→標三態需 ring0)；`IAcpiTableSource` + `Win32AcpiTableSource`(usermode EnumSystemFirmwareTables，provider 0x41435049)；`AcpiService.Collect`。
- `Views/FirmwareSecurityView.xaml(.cs)`（新）+ `Nav/PageRegistry.cs` 的 `firmware-security` 項(GSecurity 群組)；綁 `EvidenceLab.FirmwareSecurityRows`。
- 生產線：`ViewModels/StartupSequence.cs` 的 `LoadDeepSpecsCore` 內 `LoadChipsetSecurity(new WinRing0PciConfigReader())` + `LoadAcpi(new Win32AcpiTableSource())`。
- 事實進快照：`EvidenceLabService.Collect` 內 `f.AddRange(vm.EvidenceLab.ChipsetFacts/AcpiFacts)`。
- 測試：`Tests/ChipsetSecurityTests.cs`(14)、`Tests/AcpiTableTests.cs`(9)、`Tests/EvidenceLabIntegrationTests.cs`(+晶片組/ACPI 接線 + 韌體安全頁冒煙)。

### P4 核心 — 北極星
- `Services/RawRegisterSnapshotService.cs`（新）：`RawRegisterRegion{Source,Bytes?,Availability,UnavailableReason,VolatilityMask?}`、`RawRegisterDiff`、`Diff(before,after)` **逐來源揮發遮罩逐位元組差分**(只報非遮罩且真的變的偏移；可用性轉移標 AvailabilityChanged)。測試 `Tests/RawRegisterDiffTests.cs`(6)。**對現有可讀來源(ACPI/PCI/MSR/SMBIOS) today 可跑**。

### P3 開工 — MMIO 解碼器
- `Services/PcieAerDecoder.cs`（新）：`PcieAer.FindAerCapOffset`(走擴充能力鏈結串列,防環防越界)、`DecodeAer`(Uncorrectable@+0x04 / Correctable@+0x10 錯誤狀態)。AER 在擴充組態(>0xFF)→**需 ECAM/MMIO(驅動)才讀得到**，解碼器已備。測試 `Tests/PcieAerTests.cs`(4)。

## 4. 架構（留根基，照此延續）
- **三態是全域契約**：任何新 reader 讀不到都用 `FactAvailability`≠Present + `UnavailableReason`，絕不省略/補值。
- **可插拔 seam**：`IPciConfigReader`（讀 PCI config）、`IAcpiTableSource`（取 ACPI 表）；將加 `IMmioReader`（MMIO 唯讀）。真實後端現為 WinRing0/Win32，Phase 3 換成自家驅動**只換這一層**；呼叫端不知底下是誰。
- **純解碼器 vs 服務層**：所有位元解讀是純函式(給 bytes 回結構)、完整單測；實際硬體讀取在服務層經 seam，讀不到標三態。**慣例：驗證力量放純解碼器，特權層極薄。**
- **測試不碰核心**：成功路徑的 `Ring0.Open()` 會真的裝核心驅動，有副作用。所以生產讀取只在 `StartupSequence.LoadDeepSpecsCore`（真機啟動）發生，**測試一律注入假 reader**。
- **快照管線**：語義事實走 `HardwareFact → HardwareSnapshotService.Create/Save/Diff`（版本化、匿名機器 id、SHA-256 信封、canonical JSON）；原始位元組走 `RawRegisterRegion → RawRegisterSnapshotService.Diff`。兩者並存。
- **IOCTL 契約(未來)**：驅動要**版本化 + 能力協商 + 資料驅動允許清單**，加一條新 MSR/MMIO 範圍 = 改清單表不改派遣碼。

## 5. 剩餘工作（managed 可續，核心載入留給使用者）

> **2026-10-02 傍晚更新（Z Code 接手 session）**：以下 1、3、4、5 已完成並提交——
> `91850cc`＝收編上一 session 未提交的 P0/P1/P3/P4；`86cbb93`＝SPI 快閃安全（`SpiFlash` 解碼器＋`SpiFlashService`）、`EcamAerService`（MCFG→ECAM 掃 bus 0）、`IMmioReader` seam＋`NotLoadedMmioReader`；`5c2e4c4`＝**XsRegProbe 驅動 C 源碼**（`XsRegProbe/Driver/`，白名單唯讀：35 條 MSR＋MCA 範圍＋SPIBAR/ECAM 兩條 MMIO、QUERY_INFO 能力協商、SDDL admin-only、退出即卸載；**READ_PCICFG 刻意未納**——BUS_INTERFACE_STANDARD 載入風險須開發機先驗，PCI 設定空間現階段續用 WinRing0）＋`XsRegProbe/BUILD-給使用者.md` 窄路簽章手冊；豁免開關 `DeepAccessService`（`ICertTrustStore`/`IDriverServiceControl` seam＋真實 X509/advapi32 後端＋韌體安全頁開關 UI）。
> 全套 **2585 綠**。真正剩餘＝下面 2、6、7＋驅動編譯簽章載入（使用者）＋載入後的 `DriverMmioReader` 接線。

1. ~~**SPI HSFSTS 解碼器**~~（已完成：`Services/SpiFlashService.cs`，HSFSTS/FRAP/FREG/PR；FDOPSS 只報位元值不判決——各世代文件極性表述不一）。
2. **MCHBAR 真實時序解碼器**：tCL/tRCD/tRP/tRAS、MAD_* 交錯 —— **世代相依極高，務必對準權威來源(Intel datasheet / CHIPSEC)再出貨，否則違反誠實原則**（比照 HFS 的作法）。
3. ~~**`IMmioReader` seam + MMIO 服務**~~（已完成：`Services/MmioAccess.cs`＋`SpiFlashService`＋`EcamAerService`；未載驅動時全標「缺自家核心驅動（未載入）」三態。`MchbarService` 待 2 對準規格後加）。
4. ~~**豁免開關**~~（已完成：`Services/DeepAccessService.cs`；真實後端 `X509TrustStore`＋`ScmDriverService`(advapi32)；非提權拒做；Disable 只移自己 CA 的 thumbprint，不掃庫誤刪）。
5. ~~**驅動 C 源碼**~~（已完成：`XsRegProbe/Driver/XrpContract.h`＋`XsRegProbe.c`＋.inf＋.vcxproj＋建置手冊）。
6. **P4 完整收集 + 持久化**：真的從各來源收原始位元組組成 `RawRegisterSnapshot`、沿用 `HardwareSnapshotService` 的 integrity/anon-id/canonical、填揮發遮罩、UI。
7. **退役 WinRing0 讀取**：MMIO 驅動就緒後把現有 WinRing0 讀取者逐一遷到 `IRegisterAccess`（含載入後的 `DriverMmioReader : IMmioReader` 接線）。

**❌ 不自動做（使用者的刻意步驟）**：`編 .sys(WDK) → 建服務載入 → 驗 MMIO`。見 §6。

## 6. 核心驅動閘門（為何不自動做）
- **本機狀態（已實測）**：Secure Boot = **關**、HVCI/記憶體完整性 = **關**、testsigning = **關**。WDK km 標頭**在**（`C:\Program Files (x86)\Windows Kits\10\Include\10.0.28000.0\km\ntddk.h`）→ 寫+編核心驅動**可行**。
- **窄路（建議）**：自建碼簽根憑證 → 裝進 LocalMachine `Trusted Root` + `Trusted Publishers`，驅動用它簽；Secure Boot 已關，KMCS 就信這張、載得進，**只放行我們這一張**（不是全機 test-signing）。本機既有的 I219-V 自簽網卡驅動就是走這條「no test mode needed」。
- **為何不自動 load**：這台是正式機（跑 MC server 等）。**在生產機載入現寫、未驗證的核心 MMIO 驅動，映射寫錯一個就 BSOD／可能毀資料**，而核心碼要靠 load-崩潰-重開 迭代。使用者已同意：managed 全建，**`.sys` 編譯+載入+MMIO 驗證**由他在開發機或刻意執行。
- **BlueSquadron 現況**：`DriverManager.TryLoad` 建 `SERVICE_KERNEL_DRIVER` 載 `BlueSquadron.sys`，**失敗則 fail-soft 降級 user-mode**；repo 內**無已簽章 .sys、無憑證**，只有 `BlueSquadron/Driver/BlueSquadron.inf`。我們的 register 驅動要**退出即卸載**（跟 BlueSquadron「留著保護」相反），故用獨立兄弟驅動。

## 7. 鐵律與陷阱（務必遵守）
- **建置/測試指令**：`dotnet test Tests/XinSpect.Tests.csproj -c Debug --nologo -p:BaseOutputPath=obj/_verify_driver/`。**絕不 taskkill**（磁碟 IOCTL 卡死的 testhost 殺不掉要重開機）。發佈用 `-p:BaseOutputPath=obj/_pub/`。
- **已知 flaky**（非回歸）：`WindowsBoostRecoveryEngineTests.Windows電源取樣保留負載脈衝三段快照`（電源取樣計時型），偶發 1 次失敗、**單獨重跑即過**。別當成自己弄壞的。
- **守護代理共存**：`main` 領先 origin **47 提交未 push**；守護代理(Z Code 你自己)有 ~10 個活的未提交檔(BrandBadge/MainWindow/AppInfo/DeviceIcons/ChangelogCatalog/MainViewModel/AboutView…)。跨 session 同檔協作：`git diff <file>` → 按關鍵字篩 hunk → `git apply --cached` 只 stage 自己的；**絕不 `git add .`**。
- **誠實驗收**：缺 ring0／不支援／權限不足一律三態標原因；讀不到的事實**不得帶 numericValue**；UI/報告顯示原因不顯示空白或假值。
- **事實 key 規則**：`^[a-z0-9](?:[a-z0-9._\-\[\]]{0,159})$`；同簽章多張表(如多個 SSDT)要加 occurrence 後綴避免 key 重複(快照 NormalizeFacts 會拒重複 key)。
- **schema 相容**：新增事實欄位請維持 additive-optional + omit-when-default，別亂升 `HardwareSnapshotSchema.CurrentVersion`（會破壞舊檔完整性與 `Schema版本錯誤` 測試）。
- **Bash cwd 會重置**：某些呼叫後工作目錄回到 `C:\Users\Administrator`，指令前綴 `cd /c/Users/Administrator/XinSpect &&`。

## 8. 權威暫存器參考（已用於實作，接手請沿用/核對）
- **BIOS_CNTL**：PCI `0:31.0` +0xDC。bit0 BIOSWE、bit1 BLE、bit5 SMM_BWP。判讀：SMM_BWP→最強；BLE→有鎖；BLE=0→任何 ring0 可寫（未保護）。
- **SMRAMC**：PCI `0:0.0` +0x88。bit4 D_LCK、bit5 D_CLS、bit6 D_OPEN。D_LCK→已鎖；否則未保護(D_OPEN=對外開放)。
- **HFS/HFSTS1**：HECI1 PCI `0:22.0`(=0x16) +0x40。佈局依 **coreboot `util/intelmetool/me.h` `struct me_hfs`**：working_state[0:3]、mfg_mode[4]、fpt_bad[5]、operation_state[6:8]、fw_init_complete[9]、error_code[12:15]、**operation_mode[16:19]**、reserved[20:23]。operation_mode：0=Normal、2=Debug、3=SoftDisable、4=OverrideJumper、5=OverrideMei；未定義標 Other+raw。**先驗 0:22.0 +0x00 低 16 位 =0x8086 才解讀**。
- **ACPI 表頭(36B)**：sig[0:4]、length u32@4、revision@8、checksum@9、OEMID[10:16]、OEMTableID[16:24]、OEMRev u32@24。校驗和 = 整表(length 位元組)和 ≡ 0 (mod 256)。**HEST**：ErrorSourceCount u32@36。**BERT**：BootErrorRegionLength u32@36、BootErrorRegionAddress u64@40(記錄在實體位址→需 ring0)。
- **PCIe AER**：擴充能力鏈從 0x100 起；表頭 = CapID[15:0] | CapVer[19:16] | NextOffset[31:20]；AER CapID=0x0001；UncorrectableStatus@+0x04、CorrectableStatus@+0x10。**在擴充組態(>0xFF)→需 ECAM/MMIO**。
- 交叉核對工具：**CHIPSEC**（晶片組安全/SPI/IOMMU 的參考實作）、**coreboot intelmetool**（ME）。

## 9. 驗證
```bash
cd /c/Users/Administrator/XinSpect && dotnet test Tests/XinSpect.Tests.csproj -c Debug --nologo -p:BaseOutputPath=obj/_verify_driver/
```
綠線基準 **2585**（本計畫起點約 2503）。本計畫新增測試檔：`EvidenceRowTests`、`ChipsetSecurityTests`、`AcpiTableTests`、`RawRegisterDiffTests`、`PcieAerTests`、`SpiFlashTests`、`EcamAerTests`、`DeepAccessTests`，以及 `HardwareSnapshotServiceTests`／`EvidenceLabIntegrationTests` 的增補。WPF 頁驗證靠 `EvidenceLabIntegrationTests` 的冒煙測試(STA 執行緒 Measure/Arrange)——伺服器上無法逐像素看畫面。已知 flaky：`WindowsBoostRecoveryEngineTests.Windows電源取樣保留負載脈衝三段快照`（單獨重跑即過，本 session 又現蹤一次）。

## 10. 建議下一步順序
`SPI HSFSTS 解碼器 → IMmioReader seam + MMIO 服務(三態 gate) → EcamAerService(接 PcieAer) → 豁免開關(ICertTrustStore + UI) → 驅動 C 源碼 →（使用者）編譯+載入+驗 MMIO → MCHBAR 時序(對準規格) → P4 完整收集+持久化 → 退役 WinRing0 讀取`。

每一步照慣例：純解碼器先 TDD(紅→綠)、服務層經 seam 注入假 reader、讀不到標三態、全套綠才算數。

---
_接手脈絡：計畫檔 `C:\Users\Administrator\.claude\plans\1-cheeky-popcorn.md`；主線 `main`(2.1.0/Everest)。有疑問照「誠實優先、特權層極薄、測試不碰核心」三原則判斷。_




