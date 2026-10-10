# XinSpect 深層暫存器・20 輪迭代後交接（Z Code → 下一個 AI，2026-10-02）

> 自足移交文件：讀完這份就能無上下文接手。所有路徑相對 `C:\Users\Administrator\XinSpect`。
> 本檔放 repo 根、**未追蹤**（同既有 HANDOFF-*.md 慣例）；請勿 `git add .`，只 add 明確路徑。
> 前一份移交 `HANDOFF-DEEP-REGISTER-2026-10-02.md`（同在 repo 根、未追蹤）是本檔的上游，兩份都讀。

## 0. TL;DR
XinSpect（曦覽，WPF 硬體監控，作者 Xinglanclever）的深層暫存器計畫：把驗機推到「讀到沒人讀的那一層」（MSR/PCI/MMIO/ACPI），同時守住誠實原則——**讀不到就說讀不到，絕不以 0／0xFF／典型值／舊值填補**。
P0–P4 骨幹＋XsRegProbe 白名單唯讀驅動源碼＋豁免開關已全數落地；**20 輪迭代（18 提交，含交接檢查補的手冊修訂）已完成**：驅動 IOCTL 後端與驗證器、契約鏡像同步測試、IA32 安全 MSR 事實、BIOS 寫入面綜合裁決、P4 原始快照收集／揮發遮罩／SHA-256 持久化、韌體安全頁警示色與分組、報告區塊、MCHBAR 基底、flaky 緩解、ACL 加固。**全套 2644 綠**。
剩餘主線：MCHBAR 時序解碼（需對準 datasheet）、P4 的 UI 與存檔整合、驅動 MSR 批次讀取消費者、READ_PCICFG、退役 WinRing0。**編譯簽章載入 .sys 是使用者的刻意步驟，不自動做**。

## 1. 現況快照（本檔寫作時）
- 分支 `main`，版本 **2.1.0（Everest 系列，版號刻意不升）**，FileVersion **2.1.0.5**。
- 已 push 至 origin：`f05353c`。**未 push：18 個提交**（`c3c77e2`→`164f924`，見 §3；最後一個是本交接檢查時補的 BUILD 手冊 .sys 部署步驟）。push／Release 必等使用者明說。
- 全套測試 **2644/2644 綠**（基準線只該往上走；變少先查清楚）。
- Release v2.1.0（GitHub）雙資產已是 2.1.0.5 版建置（`XinSpect.v2.1.Everest.exe`＋`BlueSquadronBridge.v2.1.Everest.exe`）。
- **工作樹有守護代理的未提交 Everest 文案檔**（BrandBadge/MainWindow/AppInfo/DeviceIcons/ChangelogCatalog/MainViewModel/AboutView/IconGallery/README EOL、未追蹤 Tests/AppInfoTests.cs・TempGalleryShot.cs・verify-shots-210/・.orphaned-tests/）：**勿碰、勿 stage、勿當垃圾清掉**。已發佈的 exe 內含這批（測試全綠），changelog 未列。
- 驅動現況：`XsRegProbe/Driver/`（C 源碼＋.inf＋.vcxproj）＋`XsRegProbe/BUILD-給使用者.md`（編譯/窄路簽章/載入手冊）＋`XsRegProbe/Verifier/`（心跳檢查 console）。**.sys 尚未編譯、未簽章、未載入**——那是使用者的步驟。載入前所有 MMIO 事實三態；載入後啟動序列的 `DriverMmioReader` 自動翻真值（無需再改 managed 碼）。

## 2. 身分與鐵則
- **鐵則一（身分）**：鎖「驗機/證據＋韌體安全稽核」。效能 profiler（PMU/PEBS/PT）延後；超頻三後端（AMD CPU/GPU、Intel GPU）未動工且涉及硬體寫入，需使用者個別明示同意。
- **鐵則二（誠實）**：每一項宣告讀不到什麼。三態 `FactAvailability { Present, NotSupported, InsufficientPrivilege, ReadError, NotApplicable }`＋`UnavailableReason`；讀不到的事實**不得帶 numericValue**；UI/報告顯示原因不顯示空白。假結論比沒有結論更糟。
- 使用者偏好：繁中台灣用語、簡潔不客套、一次成型節奏、**貼方向文件＝同意開工**、「一次性搞好別問我」授權不含核心驅動載入與硬體寫入、駁回選單式提問。
- 「最強的方向走。迭代20次」的迭代節奏已跑完一輪：**一輪一提交、TDD、全套綠才算數**。

## 3. 20 輪迭代提交清單（未 push）
`c3c77e2` 契約鏡像＋.h 同步測試 → `9b70057` DriverMmioReader（IOCTL 後端＋能力協商對帳）→ `c816dc0` 驗證器＋SDK Path 歧義修正＋csproj 檔案集排除 → `db25fd2` DeepAccess 握手探測 → `ceea044` IA32_FEATURE_CONTROL/DEBUG_INTERFACE 三態事實 → `bf4ad54` BIOS 寫入面綜合裁決 → `8da3b29` 韌體安全頁警示色＋分組 → `28cbb47` 報告韌體安全區塊 → `8fd4a00` 原始暫存器收集器 → `fcb6b47` 揮發遮罩＋收集差分全迴路 → `05f0352` canonical JSON＋SHA-256 信封持久化 → `3186b50` MCHBAR 基底探索 → `1f9b422` flaky 電源取樣緩解 → `5e70b3d` PFX/密碼檔 ACL 加固 → `f8a77e6` MCFG 多條目＋多 segment → `ef502ab` HelpDot＋Help 條目 → `2d6d8e8` 終審修復（同名表來源鍵重複、驅動控制代碼共用）→ `164f924` BUILD 手冊 .sys 部署步驟（交接檢查時補）。

## 4. 架構（照此延續）
- **三態是全域契約**：任何新 reader 讀不到都用 `FactAvailability`≠Present＋原因。
- **可插拔 seam**（新增讀取來源照這個形）：`IPciConfigReader`（PCI 設定空間）、`IAcpiTableSource`（ACPI 表）、`IMmioReader`（MMIO 唯讀；**`LastFailReason` 預設介面實作**，失敗細節上拋給服務層；`NotLoadedMmioReader` 是「未載入」語義的固定三態件，測試大量使用）、`IKernelMsrReader`（核心 MSR，真實後端 `WinRing0KernelMsrReader`）、`IRawDeviceChannel`（原始裝置 IOCTL 通道，測試注入假件）、`ICertTrustStore`／`IDriverServiceControl`（豁免開關）。真實後端：WinRing0／Win32／`DriverMmioReader`（XsRegProbe；啟動序列單一實例共用，握手結果快取在實例內）；**測試一律注入假件，測試不碰核心**。
- **慣例：驗證力量放純解碼器**（給 bytes 回結構、完整單測：`SpiFlash`、`PcieAer`、`AcpiTable`、`PlatformSecurity`、`ChipsetSecurity`），特權層極薄。
- **契約鏡像**：`Services/XsRegProbeContract.cs` 與 `XsRegProbe/Driver/XrpContract.h` 由測試逐項鎖死（解析 .h 比對）；**加允許範圍＝兩邊各改一處，測試強制一致**。驅動端：資料驅動清單、版本化 IOCTL（QUERY_INFO 能力協商）、METHOD_BUFFERED、SDDL 僅管理員、退出即卸載、無寫入路徑。
- **雙快照管線並存**：語義事實 `HardwareFact → HardwareSnapshotService`（版本化/匿名 id/SHA-256/canonical）；原始位元組 `RawRegisterRegion → RawRegisterCollectService`（收集：PCI 2 區＋MSR 3 區＋ACPI 整表＋SPIBAR 0x88＋MCHBAR 0x100）→ `RawRegisterSnapshotService.Diff`（揮發遮罩逐位元組）→ `RawRegisterSnapshotStore`（canonical JSON＋SHA-256 信封、竄改拒載）。**原始快照不做匿名化**（誠實界線，存檔是使用者主動行為）。
- **豁免開關** `DeepAccessService`：開＝自簽 CA（RSA-4096，存 `%ProgramData%\XinSpect\Driver`，PFX/密碼檔 ACL 限 SYSTEM/Administrators）→ 裝 Root+TrustedPublisher → .sys 在才裝/啟服務；關＝停刪服務→只移自己 CA thumbprint；非提權拒做；握手探測把「服務執行中」與「裝置連線」分開回報。
- **IOCTL**：`IOCTL_XRP_QUERY_INFO 0x83386000`／`READ_MSR_LIST 0x83386004`／`READ_MMIO 0x83386008`（CTL_CODE(0x8338, fn, METHOD_BUFFERED, FILE_READ_DATA)）。

## 5. 剩餘工作（建議下一批迭代，接手者照序做）
0. **事實刷新路徑（載入驅動後的 UX 空缺）**：事實只在啟動序列載入一次，`DriverMmioReader` 握手結果快取在實例內——**使用者在韌體安全頁按「開啟」啟用深層存取後，SPI/AER/MCHBAR 事實仍三態，要重啟程式才翻真值**。下一批該做：DeepAccess 啟用成功後回呼重載證據（或頁面加「重新擷取」）。
1. **驅動 MSR 批次讀取消費者**：`DriverMsrReader : IKernelMsrReader`（IOCTL_XRP_READ_MSR_LIST，槽位 in/out 同塊；清單外槽位回 STATUS_ACCESS_DENIED → 上層三態）；平台安全事實在驅動連線時可切自家後端（**目前 `WinRing0KernelMsrReader` 走 WinRing0，與驅動無關，今天就能讀**）。
2. **P4 的 UI 與存檔整合**：證據實驗室時間膠囊旁加「原始快照」存檔（`RawRegisterSnapshotStore.Save`）、載入＋逐位元組差分呈現（ChangedOffsets 已有）；報告加 raw 摘要行。
3. **MCHBAR 時序解碼**：tCL/tRCD/tRP/tRAS、MAD_* ——**世代相依極高，務必對準 Intel datasheet／CHIPSEC 再出貨**（`MchbarService` 已標「刻意未實作」，那是誠實的佔位不是待辦忘記）。
4. **READ_PCICFG**：BUS_INTERFACE_STANDARD 查詢 PCI 匯流排介面——**需開發機先驗載入風險**，這是退役 WinRing0 的第一步。
5. **EcamAerService 掃描擴大**：bus 0 之外（MCFG EndBus 內），考慮 IOCTL 批次或驅動端列舉，控制呼叫次數。
6. **服務層對帳深化**：SPIBAR/ECAM 實際值 vs 驅動允許清單不符時的事實文字已帶原因（`DriverMmioReader.LastFailReason`），可再加專門事實。
7. **退役 WinRing0 讀取**：驅動就緒後逐一遷移（PCI config 仍走 WinRing0 直到 4 完成）。
8. **發佈**：等使用者明說（慣例見 §8）。

**❌ 不自動做**：編 .sys(WDK) → 建服務載入 → 驗 MMIO；任何硬體寫入（MSR 寫、超頻）。本機是生產機（跑 MC server）。

## 6. 驅動閘門與本機狀態
- Secure Boot=關、HVCI=關、testsigning=關；WDK km 標頭在（10.0.28000.0）。窄路簽章＝自簽 CA 進 LocalMachine Root+TrustedPublisher，只放行這張（I219-V 自簽網卡驅動先例）。完整步驟在 `XsRegProbe/BUILD-給使用者.md`，載入後用 `dotnet run --project XsRegProbe/Verifier/DriverVerifier.csproj -c Release` 做三項心跳（QUERY_INFO 對帳／讀 HSFSTS／清單外必須被拒）。
- **驗證器語義（已實測）**：驅動未載時優雅退場——印「驅動不可用：驅動裝置不存在」並 **exit 1**（腳本化時非零是預期）。若 `dotnet run` 撞建置鎖（守護代理在編），改隔離建置再跑：`dotnet build XsRegProbe/Verifier/DriverVerifier.csproj -c Release -p:BaseOutputPath=obj/_verify_verifier/` 然後執行產出 exe。
- **.sys 部署路徑（兩條路已收斂）**：程式內建開關（DeepAccess）只認 `%ProgramData%\XinSpect\Driver\XsRegProbe.sys`（SysPresent 檢查＋Install 都用它）；BUILD 手冊 §4 已補「複製 .sys 到該目錄」步驟。照手冊手動 `sc create` 用建置輸出路徑也能跑（狀態按服務名查），但狀態列會說「.sys 未部署」——**建議一律複製到 ProgramData 再用程式內開關**。
- 允許清單現況：35 條逐列 MSR＋MCA 0x400-0x4FF；MMIO 兩條＝SPIBAR 0xFED10000-0xFED10087、ECAM 0xE0000000-0xE7FFFFFF（managed 端以 PCI BAR／MCFG 對帳，不符即三態）。

## 7. 鐵律與陷阱（務必遵守）
- **建置/測試**：`cd /c/Users/Administrator/XinSpect && dotnet test Tests/XinSpect.Tests.csproj -c Debug --nologo -p:BaseOutputPath=obj/_verify_driver/`（Bash cwd 會重置，指令前綴 cd）。**絕不 taskkill**（卡 IOCTL 的 testhost 殺不掉要重開機）。發佈用 `-p:BaseOutputPath=obj/_pub/`。
- **絕不 `git add .`**；只 add 明確路徑。同檔混守護代理 hunk 時：`git diff <file>` → 篩自己的 hunk → `git apply --cached`（本案多次驗證可行）。
- **測試紅絕不提交**：不要把 `dotnet test … && git commit` 串在一行——grep 有輸出就會讓 && 過關，紅測試照樣提交（本案中鏢一次靠 amend 救回）。**提交後必看 `git log -1` 訊息對不對**：`obj/_msg.txt` 會殘留前一 session 的訊息，Write 撞已存在檔案時尤其危險。
- **SDK 10.0.400 起 WPF code-behind 裸 `Path` 與 System.IO.Path 歧義**（HistoryGraph/FanCurveEditor/AnalogVoltMeter 已全名限定；csproj 已 `<Using Remove="System.IO">`）。**本庫慣例：檔案明確寫 `using System.IO;`**——新檔用到 File/Path/Directory 忘了 using 就會 CS0103。
- **repo 根 csproj 的預設 glob 會吞新子目錄的 .cs**——新增子專案目錄（如 XsRegProbe/）記得在 XinSpect.csproj 加 `<Compile Remove="…\**\*.cs" />` 排除（比照 Bridge/BlueSquadron/XsRegProbe 既有區塊）。
- 已知 flaky 已**緩解**（電源取樣 `1f9b422`：相位窗放大＋三次嘗試——逾衝型失敗機率大幅降低，非數學根除）；仍偶發時單獨重跑即過、別當回歸；新 flaky 先單獨重跑確認再查。
- **事實 key 規則**：`^[a-z0-9](?:[a-z0-9._\-\[\]]{0,159})$`；同簽章多張表/同名多區要加 occurrence 後綴（`AcpiService`、`RawRegisterCollectService` 都有前數法範例）——**差分按來源鍵 ToDictionary，鍵重複直接炸**。
- **schema 相容**：新增事實欄位維持 additive-optional＋omit-when-default，別動 `HardwareSnapshotSchema.CurrentVersion`。
- WPF 頁驗證靠 `EvidenceLabIntegrationTests` 的 STA 冒煙測試（Measure/Arrange），無法逐像素看畫面；寫死色碼有專測（`HardcodedColorTests`），警示色一律 `{DynamicResource WarningBrush}`。
- `EvidenceFactRow` 沒有 Key 屬性（斷言用 Name/Category）。

## 8. 發佈慣例（等使用者明說才做）
- **現狀提醒：20 輪迭代內容尚未折疊進 changelog**——2.1.0 條目只含到 2.1.0.5 發佈時的內容；發佈前必須把 20 輪的新事實（FEATURE_CONTROL/DEBUG_INTERFACE、BIOS 寫入面綜合裁決、P4 原始快照收集/持久化、MCHBAR、Help 條目…）再折疊進去，FileVersion .5→.6。
- **Everest 系列：版號維持 2.1.0 不升**（`Tests/DeepBenchIntegrationTests.Changelog折疊進既有Everest項目且不升版本號` 釘住）；新內容**折疊進既有 2.1.0 條目**；**FileVersion 第四段遞增**；三處版號同步測試抓 csproj/README/AboutView（AboutView 的版號行在守護代理未提交 hunk 裡，發佈時要小心只 stage 自己的改動）。
- `gh` keyring token 失效不可用；用 `python C:\Users\Administrator\.agents\skills\xinspect-release\scripts\publish_release.py --repo Xinglanclever/XinSpect --tag v2.1.0 --title "XinSpect v2.1 Everest" --notes-file … --asset … --asset-name "XinSpect.v2.1.Everest.exe" --replace-asset`（同 tag 重跑沿用既有 Release；**body 更新要另做 PATCH**，`ensure_ascii=True`，冪等判斷防重複）。發佈說明用繁中分節（照 v2.1.0 notes 體例）。token 一律不回顯（過 sed 遮罩）。
- publish 旗標：`-c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:BaseOutputPath=obj/_pub/ -o publish-2.1.0`（主程式＋`BlueSquadron/BlueSquadronBridge.csproj` 同款）。單檔量級：主程式 ~22.7 MB、bridge ~6.5 MB。
- 坑：publish 會把工作樹狀態（含守護代理未提交檔）編進 exe；版戳是 ProductVersion `2.1.0+<SHA>`。

## 9. 權威暫存器參考（已用於實作，接手沿用/核對）
- **BIOS_CNTL**：PCI `0:1F.0` +0xDC。bit0 BIOSWE、bit1 BLE、bit5 SMM_BWP。
- **SMRAMC**：PCI `0:0.0` +0x88。bit4 D_LCK、bit5 D_CLS、bit6 D_OPEN。
- **HFSTS1**：HECI1 `0:16.0` +0x40（先驗 vendor=0x8086）。coreboot `me_hfs` 佈局：working_state[3:0]、fw_init bit9、operation_mode[19:16]（0=Normal/2=Debug/3=SoftDisable/4=OverrideJumper/5=OverrideMei/其他=Other+raw）。
- **HSFSTS1**：SPIBAR+0x04。bit0 FDONE、bit1 FCERR、bit2 AEL、bit11 WRSDIS、bit13 FDOPSS（**只報位元值不判決**——各世代文件極性表述不一）、bit15 FLOCKDN。SPIBAR 由 `0:1F.5+0x10` BAR 取得（&0xFFFFF000）。
- **FRAP**：+0x50。bits[3:0] BRWA（bit1=BIOS 區 host 可寫）、bits[7:4] BRRA。**FREGx**：+0x54 起 4 bytes——bits[14:0] 基底、bits[30:16] 上限（4KB 單位；0x7FF 編碼是 0x07FF0000 高位）。**PRx**：+0x74 起——bit15 WPE、bit31 RPE、基底/上限同 FREG。
- **IA32_FEATURE_CONTROL** 0x3A：bit0 Lock、bit1 VMX-in-SMX、bit2 VMX-outside-SMX（SDM Vol.4）。raw=0 時「未實作 vs 未啟用」無法區分，文字明說。
- **IA32_DEBUG_INTERFACE** 0xC80：bit0 ENABLE、bit30 LOCK、bit31 DEBUG_OCCURRED（鑑識線索）。部分平台未實作→null→ReadError。
- **MCHBAR**：PCI `0:0.0` +0x48（64-bit BAR，bit0 enable、基底 32KiB 對齊 mask 0xFFFF8000）＋+0x4C 高位。**暫存器佈局世代相依，刻意未解碼**。
- **PCIe AER**：擴充能力鏈從 0x100 起，表頭＝CapID[15:0]|Ver[19:16]|Next[31:20]，AER=0x0001；UncorrStatus@+0x04、CorrStatus@+0x10。需 ECAM——**MCFG**：標頭36+保留8後每條目 16 bytes（基底 u64、群組 u16、起訖 bus u8）；managed 端 `AcpiTable.McfgEntries` 全解、掃 segment 0，多 segment 明說未掃。
- **XsRegProbe 契約**：`XRP_DRIVER_INFO{Magic'XRP1',IoctlVersion1,FeatureMask,MsrExactCount,MsrRangeCount,MmioRangeCount}`；`XRP_MSR_SLOT{Msr,Status,Value}` 同槽往返；MMIO 請求 {PA u64,Len u32}、回覆 {Status,Len,Data[4096]}。**加範圍＝.h 與 managed 鏡像各改一處**。
- 交叉核對工具：CHIPSEC、coreboot intelmetool。

## 10. 驗證
```bash
cd /c/Users/Administrator/XinSpect && dotnet test Tests/XinSpect.Tests.csproj -c Debug --nologo -p:BaseOutputPath=obj/_verify_driver/
```
綠線基準 **2644**。本批新增測試檔：`XsRegProbeContractTests`、`DriverMmioReaderTests`、`PlatformSecurityTests`、`RawRegisterCollectTests`（含遮罩×差分全迴路）、`RawRegisterSnapshotStoreTests`、`MchbarTests`，及 `SpiFlashTests`（綜合裁決）、`EcamAerTests`（多 segment）、`DeepAccessTests`（握手/ACL）、`EvidenceRowTests`（警示前綴）、`EvidenceLabIntegrationTests`（報告區塊）增補。已知 flaky 已**緩解**（非根除）——再遇到先單獨重跑確認、別當回歸。

## 11. 建議下一步順序
`事實刷新（啟用深層存取後免重啟即翻真值）→ 驅動 MSR 批次讀取消費者 → P4 存檔/載入/差分 UI → MCHBAR 時序（對準 datasheet）→ READ_PCICFG（開發機先驗）→ 掃描擴大 → 退役 WinRing0 →（使用者明說後）changelog 折疊＋發佈`。

每一步照慣例：純解碼器先 TDD、服務層經 seam 注入假件、讀不到標三態、一輪一提交、全套綠才算數。有疑問照「誠實優先、特權層極薄、測試不碰核心」三原則判斷。

---
_交接脈絡：上游 `HANDOFF-DEEP-REGISTER-2026-10-02.md`；計畫檔 `C:\Users\Administrator\.claude\plans\1-cheeky-popcorn.md`；主線 `main`（2.1.0/Everest）。作者 Xinglanclever 會直接指出錯處——照著查病根重寫，比小修小補快。_
