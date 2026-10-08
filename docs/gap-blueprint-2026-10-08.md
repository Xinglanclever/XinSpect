# 曦覽功能擴展藍圖 × 實際程式碼 落差分析

- 分析日期：2026-10-08
- 基準程式碼：`C:\Users\Administrator\XinSpect`，分支 `main`，HEAD `2adc165`
  （工作區另有未提交變更：`MainWindow.xaml.cs`、`Models/EraCalendar.cs`，不影響本分析）
- 對照文件：`C:\Users\Administrator\Downloads\曦覽功能擴展藍圖.md`
- 方法：檔案清單 + 關鍵字 grep + README／docs 規格交叉核對。每一條狀態都有落地檔案為證

> 版本號註記：README §17 最新記到 v2.2.0 Olympus，但 git log 另有「版號 2.25」「2.3.0」
> 提交、`publish-2.5.0/` `publish-2.25/` 目錄、記憶檔記 v2.1.0。repo 內版本號不一致；
> 本分析一律以工作區現況程式碼為準，不看版號。

## 狀態定義

| 標記 | 意義 |
|---|---|
| ✅ 已實現 | 有對應實作，且有 README／測試／SpecRef 佐證 |
| 🟡 部分 | 有基礎實作，但缺藍圖要求的子項或深度 |
| ⬜ 未做 | 程式碼中查無對應實作 |
| ⛔ 刻意不做 | 專案明文裁決不做（與五原則衝突或風險考量） |

---

## 零、五原則檢核（藍圖自訂的准入條件）

藍圖判斷標準＝加了之後五原則是否仍成立：三態誠實／來源可稽核／非自造驗證／同意閘門／零網路 API。

| 藍圖條目 | 與原則的張力 | 判定 |
|---|---|---|
| 時間戳服務 RFC 3161 | 需網路 | ⚠️ 藍圖自己已標「需網路」；與零網路 API 衝突，須走使用者主動觸發白名單 |
| 時間 NTP／PTP／GPS | NTP 需網路 | ⚠️（藍圖已標） |
| 模擬器 M9／M10 | — | ⛔ `docs/spec/LIMITATIONS.md` 明文「SDK/模擬器（M9/M10）刻意不做」 |
| 規則市集、二手平台 API | — | ⛔ 同上明文不做 |
| UEFI Shell 版本 | 新增獨立 build target | 不違反五原則，但工程量與維護面極大（見第三章） |
| 企業生態整合（SIEM/ITSM…） | 全部走本機 CLI/API，不掃網 | 不違反；但目前尚無任何一家整合 |
| 事實時序資料庫 | 內建本機儲存 | 不違反；但目前無查詢/異常偵測/預測層 |

---

## 一、補齊硬體覆蓋

### 1.1 AMD 深度支援

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| SMU mailbox | ✅ | `Services/AmdSmuService.cs`（SMN→PCI index/data，平台白名單位址，讀 PackagePowerW/TempC/逐核 freq+voltage） |
| PSP 平台安全處理器 | ✅ | `Services/AmdSecurityFactsService.cs`（PCI 0x1022＋class 0x10800；標「未在本機驗證」） |
| SEV / SEV-ES / SNP | ✅ | `Services/AmdSecurityFactsService.cs`（CPUID 0x8000001F EAX + MSR 0xC0010131 啟用位） |
| MSR 對等（PState/CState/電壓/溫度） | 🟡 | 電壓/溫度有（SMU）；C-state 駐留目前走 Intel MSR 0x60D/0x3FC/0x3F9/0x3FA；AMD 逐核 P-state/EPP 未見 |
| PMU 計數器 | 🟡 | `PmuCapabilityFactsService.cs`、`UncorePmuService.cs`、`PmuProgrammingService.cs` 以 Intel 為主 |
| PBO / Curve Optimizer | ⬜ | 超頻走 Intel XTU 橋接（`Overclock/XtuOcEngine.cs`、`Bridge/`），無 AMD PBO/CO |

### 1.2 非 NVIDIA GPU

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| Intel Arc（Level Zero） | 🟡 | `Services/Gpu/LevelZeroFactsService.cs`、`LevelZeroInterop.cs` |
| AMD Radeon（ADL／ROCm） | 🟡 | `Services/Gpu/AmdAdlFactsService.cs`、`AmdAdlInterop.cs`；ROCm 未見 |
| 退休頁／ECC／風扇／功耗／時脈偏移對等 | 🟡 | NVIDIA 端完整（`Gpu/NvmlInterop.cs`、`GpuOcService.cs`）；AMD/Intel 端不完整 |

### 1.3 DDR5 完整支援（藍圖稱「README 明說不支援」）

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| DDR5 SPD 讀取 | ✅ | `Services/SpdReader.cs`：SPD5118 hub MR0=0x51/MR1=0x18、1024B/8 頁、`Ddr5EepromBase=0x80`；與 Linux `spd5118.c` 交叉 |
| DDR5 SPD 解碼 | ✅ | `XinSpect.Decoders/SpdDecoder5.cs`（JEDEC JESD400-5，key type 0x12，CRC16） |
| PMIC | ⬜ | 僅知識層提及（`Services/DimmReferenceService.cs`）；無 PMIC 暫存器讀取 |
| On-DIMM 溫度感測器 | 🟡 | 有 DDR4 TSOD（0x18–0x1F，TSE2004）；DDR5 on-DIMM 未見 |
| 子通道架構 | ⬜ | 查無 |

> ⚠️ `SpdDecoder5.cs` 自述「未在本機驗證」：本機是 DDR4 平台，金標向量為手算合成資料。
> 藍圖稱「README 明說不支援」已過時——DDR5 讀取/解碼已在程式碼中，但尚未有實機對帳。

### 1.4 儲存介面

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| RAID：MegaRAID | 🟡 | `XinSpect.Decoders/MegaRaidDecoder.cs` 只解 MFI 訊框標頭；BBU/VD/PD 未驗證不解（LIMITATIONS #4） |
| SAS / SATA 擴充器 | ⬜ | 查無 |
| Intel VROC / AMD RAIDXpert | ⬜ | 查無 |
| UFS / eMMC / SD | ⬜ | 查無 |
| USB 外接碟 SMART（UASP） | ⬜ | SMART 通路限 disk.sys 代理的 `SMART_RCV_DRIVE_DATA` |

### 1.5 網路與無線

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 網卡錯誤/丟棄、卸載、RSS | ✅ | `Services/NicHealthFactsService.cs`、`NetOffloadFactsService.cs`、`NetAdapterDecoder.cs` |
| 2.5G/5G/10G 鏈路協商、EEE、Flow Control | 🟡 | 有卸載/RSS；協商/EEE/flow control 未見 |
| RDMA / RoCE / InfiniBand | ⬜ | 查無 |
| Wi-Fi 6E：6 GHz | 🟡 | `XinSpect.Decoders/WifiBss.cs` 已解 6 GHz 頻道換算（Annex E） |
| Wi-Fi 7：MLO / 320 MHz / 世代 | ⬜ | 查無 |
| 藍牙：外設電量 | ✅ | `Services/BluetoothBatteryService.cs` |
| 藍牙：版本 / 編解碼器 / 藍牙 6 | ⬜ | 查無 |

### 1.6 電源與散熱

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| PSU 數位電源（PMBus） | ✅ | `Services/PsuPmbusFactsService.cs` + `XinSpect.Decoders/PmbusDecoder.cs` |
| UPS（電池/負載/市電） | ✅ | `Services/UpsFactsService.cs`（WMI Win32_Battery） |
| AIO 水泵（轉速/溫度） | ⬜ | 查無 |
| Super I/O 擴展（廠商） | 🟡 | `XinSpect.Decoders/SuperIoKnowledge.cs` 收 ITE / Nuvoton（含 Winbond NCT67xx）/ Fintek |
| VRM 控制器 | ✅ | `Services/VrmControllerService.cs`（PMBus 0x40–0x4F，LLC 讀寫，⚠ 寫入有風險） |

### 1.7 主機板與機箱

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| SMBIOS Type 3（機箱）/ Type 17（記憶體） | ✅ | `Services/ChassisAndHpaFactsService.cs`、`Services/SmbiosFacts.cs` |
| SMBIOS Type 9（System Slot） | ✅ | `Services/SmbiosService.cs:269`（Type 9 → 插槽列）、`Services/M2AnalysisService.cs:7`（自插槽資訊判 M.2/U.2，並標「未列出不代表沒有」） |
| SMBIOS Type 8（Port Connector） | ⬜ | 全域查無 Port Connector 解析 |
| RGB 控制器（OpenRGB 式唯讀） | ⬜ | 查無 |
| 機箱入侵更多來源（Super I/O、GPIO） | 🟡 | 目前僅 SMBIOS Type 3（`ChassisFactsService`）；Super I/O/GPIO 未見 |

### 1.8 音訊

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 裝置拓撲（端點/取樣率） | 🟡 | `Services/AudioEndpointFactsService.cs`（端點名稱＋格式） |
| 延遲量測（WASAPI） | 🟡 | `Services/LoopbackSignalService.cs`（WASAPI loopback）、`AudioSpectrumService.cs` |
| 延遲量測（ASIO） | ⬜ | 查無 |
| 音質（THD+N、頻率響應，需迴路） | ⬜ | 查無 |

---

## 二、深化現有領域

### 2.A 韌體與安全

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| Intel Boot Guard / AMD PSB | ⬜ | 全域 grep `boot guard|platform secure boot|PSB` 無命中 |
| TPM 2.0 PCR 0–23 | ✅ | `Services/TpmFactsService.cs`（經 TBS，PcrCount=24，多 bank） |
| TPM 2.0 Event Log 解析 | ✅ | `XinSpect.Decoders/Tpm2.cs` `WalkTcgLog`（TCG_PCR_EVENT2） |
| TPM NV 索引 / 金鑰階層 | ⬜ | 查無 |
| UEFI：Secure Boot / SetupMode / AuditMode / DeployedMode / BootOrder | ✅ | `Services/UefiBootFactsService.cs` |
| UEFI：db / dbx / KEK / PK / Boot#### | ✅ | `Services/UefiSignatureFactsService.cs` |
| UEFI：BootNext | 🟡 | 有 BootOrder 計數；BootNext 未見 |
| 韌體更新歷史 / capsule update | ⬜ | 查無（`FirmwareService.cs` 無 capsule/ESRT） |
| BMC / IPMI（KCS/SSIF） | 🟡/⛔ | `XinSpect.Decoders/IpmiDecoder.cs`、`Services/OobFactsService.cs` 只做訊息解碼；KCS/SSIF「本機無硬體驗證不出貨」（LIMITATIONS #3） |
| ME / PSP 更深 | 🟡 | `Services/BiosMeService.cs`（HFSTS1，先驗 PCI 0:16.0）、`AmdSecurityFactsService.cs`；韌體版本深度有限 |
| PCH 世代名稱 | ⛔ | LIMITATIONS #6「未對準出處不出值」 |
| 側通道（Spectre/Meltdown 緩解） | 🟡 | `Services/CpuSecurityService.cs`（IA32_ARCH_CAPABILITIES 0x10A＋SPEC_CTRL 0x48，IBRS/STIBP/SSBD） |
| RDRAND / RDSEED 統計檢定 | 🟡 | 有吞吐量測（DeepBench `cpu.rdrand-rdseed`）；統計檢定（如 NIST STS）未見 |

### 2.B 系統與軟體層

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 驅動：簽章稽核 | ✅ | `Services/DriverAuditService.cs`（Win32_PnPSignedDriver IsSigned）、`DriverAnalysisService.cs` |
| 驅動：Driver Verifier / WHQL / 記憶體足跡 | ⬜ | 查無 |
| 服務盤點 | ✅ | `Services/ServiceInventoryService.cs`（WMI Win32_Service） |
| 服務依存關係圖（視覺化） | 🟡 | 有盤點無依存圖 |
| 排程工作審計 | 🟡 | `Services/ScheduledTaskStartup.cs` |
| 啟動項完整（Run/RunOnce/資料夾/排程） | ✅ | `Services/StartupService.cs`（Run32/folder/StartupApproved＋排程背景掃描） |
| WDAC / AppLocker | 🟡 | BlueSquadron 有 Code Integrity/VBS；WDAC/AppLocker 專門列舉未見 |
| 群組原則完整（Registry.pol 解析） | ⛔ | LIMITATIONS #7「只回報存在與時間指紋」 |
| 事件記錄完整（Application/Security/Setup） | 🟡 | System 摘要（`EventLogSummaryService.cs`）＋Security 1102（`SecurityAuditFactsService.cs`）＋Application（`ReliabilityHistoryService.cs:132`）；Setup log 未見 |
| 效能計數器完整（磁碟/網路/GPU） | 🟡 | 有自telemetry/歷史；完整 per-counter 未見 |
| ETW 更深 | 🟡 | `Services/EtwTraceService.cs`（DPC/幀時間/執行緒遷移/隱形停頓） |
| 記憶體 dump（full/kernel） | 🟡 | `Services/BsodService.cs` 只掃 `%SystemRoot%\Minidump`（小型傾印） |
| 虛擬化狀態（WSL/容器/Hyper-V/沙箱） | ✅ | `Services/OptionalFeatureService.cs`（四目標 InstallState 口徑） |

### 2.C 量測與深測（DeepBench 38 項）

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 運算：y-cruncher 式 | ✅ | `Services/StressBridgeService.cs`、`StressBridge/YCruncherParser.cs` |
| 運算：NPU 推論 / 算力 | ✅ | `Services/NpuComputeBenchService.cs`、`NpuDetectionService.cs` |
| 運算：Cinebench 式渲染 / Blender 式 3D | ⬜ | `GpuRenderTestService.cs` 為 WPF/D3D11 填充，非渲染器 benchmark |
| 運算：7-Zip 式壓縮 / HandBrake 式轉碼 | ⬜ | 有 MediaFoundation H.264，非轉碼/壓縮 benchmark |
| GPU：編解碼能力（含 HEVC 多 profile） | ✅ | `Services/GpuCodecService.cs`（GUID 表）／`GpuCodecThroughputService.cs` |
| GPU：光線追蹤 / DLSS·FSR·XeSS / Vulkan·DX12U | ⬜ | 深測以 D3D11 為主 |
| 儲存：4K QD32 / 混合讀寫 | ✅ | `Services/DeepBench/DiskIoMatrixService.cs`、`storage.qd-ladder` |
| 儲存：持久/寫入完整性 | ✅ | `StorageFlushDurabilityService.cs`、`StorageWriteIntegrityService.cs` |
| 儲存：耐久度（寫入放大） | 🟡 | `Services/VerifyThresholds.cs` 有寫入速率常數；放大率量測未見 |
| 儲存：TRIM 驗證 | 🟡 | ATA IDENTIFY 已回報「支援 TRIM」旗標（`StorageSmartService.cs:787`）；實際下發／驗證 TRIM 未見 |
| 網路：iperf 式吞吐 | ✅ | `Services/NetworkSpeedService.cs` |
| 網路：緩衝膨脹 / Jitter / 丟包 | 🟡 | `Services/Bufferbloat.cs` ✅；jitter/丟包獨立量測部分 |
| 記憶體：MemTest 式 | ✅ | `Services/MemoryTestService.cs`、`MemoryBandwidthService.cs` |
| 記憶體：位址/資料線測試、ECC 錯誤注入 | ⬜/🟡 | Rowhammer 探測（`RowhammerProbeService.cs`）為壓力非施測；ECC 注入查無 |
| 電源：功耗牆 / 電流牆 | ✅ | `Services/CeilingService.cs`、`FrequencyTruthService.cs`、`PowerDeliveryService.cs` |
| 電源：電池放電曲線 | ⬜ | `Services/BatteryService.cs` 有健康/循環；放電曲線未見 |
| 散熱：風扇曲線 | ✅ | `Services/FanCurveService.cs`、`FanControlRow.cs`、`ThermalStickyService.cs` |
| 顯示：HDR | ✅ | `Services/HdrCapabilityService.cs` |
| 顯示：色域 | ✅ | `Services/EdidService.cs`（CIE 1931→sRGB/AdobeRGB/DCI-P3 覆蓋率） |
| 顯示：壞點/漏光/均勻度（主觀色卡） | ✅ | `Views/ScreenTestWindow.xaml`、`Views/HardwareTestView.xaml` |
| 顯示：反應時間 / 儀器級均勻度 | ⬜ | 查無（需儀器） |

### 2.D 特殊量測

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 聲學（風扇/硬碟噪音、共振、頻譜） | 🟡 | `AudioSpectrumService.cs` 為音訊頻譜，非噪音/振動 |
| 震動、熱成像、EMI | ⬜ | 查無（皆需外部硬體） |
| 環境（溫濕度/氣壓/空氣品質） | ⬜ | 查無 |
| 時間：HPET / PM timer / QPC 漂移 | ✅ | `Services/TimeSyncFactsService.cs`（QPC vs 牆鐘 ppm 漂移） |
| 時間：NTP 校正值 / PTP / GPS / 晶振 / PLL | ⬜/⚠️ | NTP 同步偏移未查詢（白名單外）；PTP/GPS/晶振未見 |

### 2.E 證據與審計

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| SHA-256 信封 | ✅ | `Services/FileHash.cs`、`EvidenceCollection.cs`、`RawRegisterSnapshotService.cs` |
| 更多雜湊（SHA-3 / BLAKE3） | ⬜ | FileHash 只做 SHA256 |
| 報告格式：HTML | ✅ | `Services/HtmlReportService.cs`、`ReportService.cs`（自足單檔＋`Verify()`） |
| 報告格式：Markdown / 純文字 | ✅ | `Services/ReportService.cs` |
| 報告格式：PDF / XML 輸出 | 🟡 | PDF 靠瀏覽器列印（非原生產生）；XML 僅輸入（`ExternalReportService.cs` 讀 AIDA64） |
| 簽章報告（自簽 CA 簽） | ⬜ | 自簽 CA 只用於驅動信任（`DeepAccessService`），非報告簽章 |
| RFC 3161 時間戳 | ⛔/⚠️ | 需網路，與零網路 API 衝突 |
| 證據鏈（差分/匯出/批次比對） | ✅ | `EvidenceTimelineService.cs`、`AssetChangeDetector.cs`、`HistoryStore.cs`、`AuditLogService.cs`（雜湊鏈） |

### 2.F 平台與新硬體

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| ARM64 Windows / Snapdragon | ⬜ | csproj 為 x64（`NpuDetectionService` 有 Snapdragon 型號表，但非 ARM 執行） |
| Windows Server | 🟡 | 對缺頻道（Diagnostics-Performance/Reliability）有三態處理 |
| 虛擬機事實 | 🟡 | `Services/PlatformTrustService.cs` hypervisor 存在位＋廠商簽章（0x40000000）；VMware/Hyper-V/VirtualBox 細節未見 |
| RISC-V / LoongArch | ⬜ | 查無 |
| PCIe 鏈路 / 世代 | ✅ | `Services/PcieLinkService.cs`、`PcieLinkDecoder.cs`（現行 speed/width） |
| CXL（CEDT） | ✅ | `Services/CxlFactsService.cs`、`XinSpect.Decoders/CxlCedt.cs` |
| PCIe 6.0 / HBM4 | ⬜ | 未見 |
| USB4 / Thunderbolt | 🟡 | `Services/TbUsb4Service.cs`（偵測）、`UsbLinkService.cs` |
| USB4 v2 / TB5 / HDMI 2.1 / DP 2.1 / NVMe 2.0 | ⬜ | 版本細節未見 |
| 液冷 / 浸沒式冷卻 | ⬜ | 查無 |
| NPU | ✅ | `Services/NpuDetectionService.cs`、`NpuComputeBenchService.cs` |

---

## 三、新維度：從工具到事實平台

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 事實關聯 / 交叉對帳引擎 | ✅ | `Services/FactRelationEngine.cs`、`FactQuery.cs`、`Rules/builtin.json`（26 條）、`RuleLoader/Validator` |
| 知識圖譜 UI（關聯圖/因果鏈/矛盾地圖/來源樹/影響分析） | 🟡/⬜ | 引擎有；視覺化互動（滑鼠高亮、因果鏈、矛盾地圖）未見專屬頁 |
| 離線硬體百科：知識表 | 🟡 | `XinSpect.Decoders/PciKnowledge.cs`、`SuperIoKnowledge.cs`、`OuiKnowledge.cs`、`SpecRefAttribute.cs`、`Models/MicroarchProfile.cs`、`EraCalendar.cs` |
| 離線硬體百科：事實字典/暫存器百科/故障百科/判讀指南 | ⬜ | 有 SpecRef 與類別知識；逐 key 字典/故障百科未見 |
| 事實時序資料庫：儲存與回放 | 🟡 | `Services/HistoryStore.cs`、`SensorLogService.cs`、`WheaTimelineStore.cs`、`EvidenceTimelineService.cs`（歷史回放） |
| 事實時序：查詢 / 趨勢 / 異常偵測 / 預測 | ⬜ | grep trend/anomaly/predict/outlier/drift 於時序來源無命中 |
| UEFI Shell 版本 | ⬜ | 目前僅 Windows x64 |
| 供應鏈：假容量驗證 | ✅ | `Services/FakeCapacityTestService.cs`（H2testw 式，同意閘門） |
| 供應鏈：HPA 隱藏容量 | ✅ | `Services/ChassisAndHpaFactsService.cs`（IDENTIFY LBA vs OS 可見） |
| 供應鏈：機齡推估 | ✅ | `Services/MachineAgeService.cs`/`MachineAgeDecoder.cs` |
| 供應鏈：SPD 一致性稽核 | ✅ | `Services/SpdConsistencyAuditService.cs`、`Models/SpdAuditModels.cs` |
| 供應鏈：批次/供應商推斷、可簽章供應鏈報告 | ⬜ | 查無 |

---

## 四、企業與產業價值

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 法規合規自動化（等保2.0/ISO27001/CIS/NIST/GDPR/HIPAA/PCI） | ⬜ | grep 這些框架名於 Services 無命中；只有原始事實（`SecurityAuditFactsService.cs`、`AuditPolicyService.cs`） |
| 企業生態整合（SIEM/ITSM/CMDB/SOAR/端點管理/取證工具） | ⬜ | 只有 `Services/LocalApiHandler.cs`（loopback 唯讀）、`CliService.cs`、`XinSpect.psm1`；無任何一家整合 |
| 碳足跡 / 能源審計 / 生命週期評估 | ⬜ | grep carbon/ESG/kWh 無命中（`AssetChangeDetector.cs` 的「生命週期」是資產變更，非碳） |
| 垂直領域套件（伺服器/工作站/電競/創作者/企業/維修/回收/二手/車載/工業/醫療/國防） | ⬜ | 單一 UI，無套件化 |

---

## 五、生態與標準

| 子項 | 狀態 | 證據 / 備註 |
|---|---|---|
| 模擬器（M9/M10） | ⛔ | LIMITATIONS 明文不做 |
| 事實模型 / SpecRef / 查詢語言 / 對帳規則 / 審計雜湊鏈 | ✅ | `docs/spec/snapshot.schema.v1.json`、`query-language.v1.md`、`SpecRefAttribute`、`Rules/builtin.json`、`AuditLogService` |
| 規則市集 / 二手平台 API / SDK | ⛔ | LIMITATIONS 明文不做 |

---

## 總結：落差分佈與建議

**已完成比例高**：藍圖第一章與第二章的「深化」項，多數在 v2.1→v2.2 期間已落地（DDR5、AMD SEV/SNP、TPM PCR+EventLog、UEFI 變數審計、BYOVD、事件清除偵測、Wi-Fi 頻道、NPU、CXL、HPA、假容量、機齡…）。藍圖對這些的描述部分已過時。

**真缺口（程式碼查無、不違反五原則、工程可行）**——建議優先：
1. Setup log 完整化（System 摘要、Security 1102、Application 已有，缺 Setup）
2. 更多雜湊（SHA-3／BLAKE3 進入報告信封）
3. TRIM 實際下發／驗證、藍牙版本/編解碼器、AIO 水泵、SMBIOS Type 8（Port Connector）、BootNext、TPM NV、驅動 Verifier/WHQL
4. 網卡 EEE/Flow Control/鏈路協商、Wi-Fi 世代/MLO/320MHz
5. NTP 同步偏移（白名單網路功能）、電池放電曲線、儲存耐久度（寫入放大）
6. 事實時序層的趨勢/異常偵測/預測；知識圖譜的視覺化互動

**需先有硬體才能誠實驗證（不可先寫死值）**：
- DDR5 實機對帳（`SpdDecoder5` 目前是合成金標）
- AMD 平台 MSR/C-state、非 NVIDIA GPU 的退休頁/ECC/風扇
- RAID BBU/VD/PD 佈局（LIMITATIONS #4）、IPMI KCS/SSIF（#3）

**大工程 / 需立項**：
- UEFI Shell 版本、事實時序資料庫、知識圖譜 UI、法規合規框架對應、企業生態整合、ARM64 支援

**與原則衝突或已明文裁決不做**：模擬器（M9/M10）、規則市集、二手平台 API、SDK、Linux、RFC 3161 時間戳（需網路）
