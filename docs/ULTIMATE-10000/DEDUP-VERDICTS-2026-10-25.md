# U10〔查重〕標記逐列收口判定（2026-10-25）

## 一、方法

對 `ULTIMATE-10000-EXPANSION-2026-10-24.md` 中帶「查重」字樣的 U10 表格列（共 **79** 列），逐列用四個證據面實證：

1. **自動初判**：`work/u10_dedup/report.md`／`report.json` 的 key-dup（8 筆）與 family-overlap（53 家族 147 筆）名單——只當嫌疑索引，不作結論。
2. **原始碼 grep**：`Services/`（含 DeepTest／DeepBench 子目錄）、`Tests/`、根目錄 UI（Views／MiniOverlayWindow 等）與工具（Tools／XinSpect.psm1／stryker-config.json），找候選鍵字面、服務名、功能關鍵詞（含鍵前綴與能力關鍵詞雙向）。
3. **既有鍵目錄**：`Services/FactKeyCatalog.cs`、`Services/FactKeyDynamicCatalog.cs`。
4. **既有計畫目錄**：`docs/ULTIMATE-1000/`、`docs/INDUSTRIAL-CATALOG-2026-10-10.md`、`docs/INDUSTRIAL-CATALOG-VOL2-2026-10-11.md`（鍵與項目編號對照，如 P2-024／P2-098／P7-003 等）。

另比對了 TS 域第一批剛實作的 12 把新鍵（`Services/DeepTest/` 下 os.memdiag.results.cross、ts.disp.refresh.verify、ts.disp.modes.cycle、ts.aud.formats、ts.aud.jackdetect、ts.aud.endpoint.state、ts.touch.maxpoints、ts.pen.pressure、ts.kbd.repeat、ts.clock.session.drift、ts.reg.enum.latency、ts.svc.depend.depth）：**這 12 把鍵與本批 79 列的候選鍵零重疊**（79 列的候選鍵前綴為 cpuid./mc./numa./mem./sb./acpi./uefi./fw./tpm./nvme./wmi./sec./rep./mt./viz./ui./cli./exp./ps./installer./release./kp./guard.），不影響本批判定。

**判準**：標題所述能力的核心已有實作（有鍵、有服務、有測試）＝**收口**；核心只被覆蓋一部分、且括號內指名的差異（未用欄位、原因碼、另一條路徑、另一張表等）確實無實作＝**新列**（差異說不準的標「差異待核」）；兩面證據都找不到＝**待核**。

### 統計

| 結論 | 筆數 |
|---|---:|
| 收口 | **49** |
| 新列 | **30** |
| 待核 | **0** |
| 合計 | **79** |

**79 與 84 的差異**：目錄正文（§圈選紀律）自稱「〔查重〕84 處」，本判定與自動初判腳本一致，在 U10 表格列內數到 **79** 列（`grep "^| U10-" | grep 查重`，含〔查重——〕〔查重：〕等變體）。全檔其餘含「查重」的表格列只有 §一儲集池表的 1 處（第 22 行，非 U10 條目）；其餘命中皆為正文敘述行。**5 處的差異無法在表格列中定位**，判為目錄編寫過程的計數漂移（或把正文變體計入），以本表 79 列為準逐列收口。

---

## 二、逐列判定

判定順序依目錄出現序。證據格式：檔案:行號（均相對倉庫根）。

### 硬體／韌體域（CP／MS／MM／SB／AC／FW／NV）

| U10 ID | 項目 | 候選鍵 | 結論 | 證據 |
|---|---|---|---|---|
| U10-CP-0148 | leaf 0x12 SGX 子葉展開（EPC 逐銀行大小） | `cpuid.12.epc{n}` | **新列**：與 `mem.encryption.sgx` 的差異在於——既有只做 SGX 支援位（CPUID leaf 7 bit30）與存在性，leaf 0x12 子葉／EPC 逐銀行大小未實作 | Services/MemoryEncryptionFactsService.cs:20,43-50；Services/CpuIdService.cs:629,641（僅 SGX/SGX_LC 位元）；`cpuid.12`、`epc` 於 Services 全庫 0 命中 |
| U10-CP-0217 | 微碼修訂雙源對帳（登錄檔 vs MSR 0x8B） | `mc.rev.crosscheck` | **收口**：已由 `mc.version`／`mc.version.msr`／`mc.version.registry`／`mc.version.cross` 實作 | Services/MicrocodeVersionFactsService.cs:39-42；Services/FactKeyCatalog.cs:516-520 |
| U10-CP-0223 | NUMA 節點清單與距離矩陣（SLIT） | `numa.slit.grid` | **收口**：已由 `numa.slit.nodes`＋`numa.distance.{a}.{b}` 實作（SLIT 解碼、上三角全矩陣逐對） | Services/TopologyDeepenFactsService.cs:50-86 |
| U10-MS-0053 | IA32_BIOS_SIGN_ID（0x8B）微碼修訂（另一源） | `mc.rev.msr` | **收口**：已由 `mc.version.msr` 實作 | Services/MicrocodeVersionFactsService.cs:40；Services/BiosMeService.cs:160-176（MSR 0x8B 直讀＋與登錄檔互證） |
| U10-MM-0040 | 延遲曲線（stride 掃描；拐點即快取邊界） | `mem.latency.curve` | **收口**：已由 LatencyCurveService 實作（指標追逐 1KB→1GB、自適應取樣、邊界推導與 CPUID 宣稱配對） | Services/LatencyCurveService.cs:50,75,302 |
| U10-MM-0052 | 記憶體映射裝置佔用（MMIO 總量；Type 19/ACPI 交叉） | `mem.mmio.total` | **新列**：與 `sb.memmap.total` 的差異在於——既有合計僅來自 SMBIOS Type 19，ACPI（E820 等）交叉未實作（`e820` 於 Services 全庫 0 命中） | Services/SmbiosMemoryMapFactsService.cs:46-47,153；Services/FactKeyCatalog.cs:790-791 |
| U10-MM-0059 | 記憶體錯誤記錄（WHEA 記憶體節區） | `mem.whea.errors` | **收口**：已由 `cm.whea.timeline` 實作（WHEA 修正/未修正計數、來源數、最新時點）；DeepBench 另有 `memory.whea.*` 逐類計數 | Services/PlatformDeepDiveFactsService.cs:19,32,44；Services/DeepBench/Adapters/MemoryEccWheaStressAdapter.cs:88-90 |
| U10-SB-0097 | Type 19 記憶體映射（對帳收口） | `sb.type19.cross` | **收口**：已由 `sb.memmap.{n}` 動態家族＋`sb.memmap.count`／`sb.memmap.total` 實作 | Services/SmbiosMemoryMapFactsService.cs:9,44-47；Services/FactKeyDynamicCatalog.cs:94-95 |
| U10-SB-0161 | Type 45 項目與 pdev.fw.updatable 盤點對帳 | `sb.type45.pdev.cross` | **新列**：與 `pdev.fw.updatable` 的差異在於——pdev.fw.updatable 已實作，但 SMBIOS Type 45（結構化韌體資訊）全庫 0 命中，對帳無從做起 | Services/FirmwareUpdatableFactsService.cs:8,59；`type 45`／`Type45` 於 Services 僅 BootGuardFactsService.cs:68 註解提及撤回清單，無實作 |
| U10-AC-0042 | TCPA／TPM2 表（收口對帳） | `acpi.tcpa.cross` | **新列**：與 `ac.tpm2`／`ac.tpm2.method` 的差異在於——TPM2 表已實作，TCPA 表（舊式）全庫 0 命中 | Services/AcpiTpm2WpbtFactsService.cs:38-40；`tcpa` 於 Services 全庫 0 命中 |
| U10-AC-0043 | DBG2／SPCR／BGRT（收口對帳） | `acpi.dbg2spcr.cross` | **收口**：已由 `ac.bgrt.deep`／`ac.spcr.deep`／`ac.dbg2.deep` 實作 | Services/AcpiDeepFactsService.cs:6-7,40-44 |
| U10-AC-0047 | SRAT＋親近性網域清單（收口） | `acpi.srat.cross` | **收口**：已由 AcpiSratFactsService 實作（`ac.srat.summary/proc/x2apic/mem/generic/domain`，親近性網域即 domain 鍵） | Services/AcpiSratFactsService.cs:25-31 |
| U10-AC-0048 | SLIT 距離矩陣（與 NUMA 交叉） | `acpi.slit.grid` | **收口**：同 U10-CP-0223，已由 `numa.slit.nodes`＋`numa.distance.{a}.{b}` 實作 | Services/TopologyDeepenFactsService.cs:50-86 |
| U10-FW-0005 | db／dbx／dbt／dbr 存在性與計數 | `uefi.dbx.count.cross` | **新列**：與 `uefi.db`／`uefi.dbx`／`uefi.kek`／`uefi.pk`（各含 count 與 list 家族）的差異在於——dbt／dbr 存在性與計數未實作（`uefi.dbt`／`uefi.dbr` 全庫 0 命中） | Services/FactKeyCatalog.cs:971-983；Services/UefiSignatureFactsService.cs:35-37；Services/FactKeyDynamicCatalog.cs:41-49 |
| U10-FW-0021 | 條目跳號偵測（Boot#### 跳號） | `uefi.boot.gaps` | **新列**：與 `uefi.boot.{n}` 的差異在於——既有逐條列出且在註解裡提及「本機 0–6、8、9 跳號」，但跳號偵測未成為事實鍵 | Services/FactKeyDynamicCatalog.cs:53-60 |
| U10-FW-0037 | SPI BIOS 區塊雜湊 | `fw.spi.hash.cross` | **收口**：已由 `spi.flash_map`＋`spi.bios_hash`（SHA-256）實作 | Services/SpiFlashHashService.cs:21-22 |
| U10-FW-0046 | Boot Guard 生產模式狀態鏈 | `fw.bootguard.cross` | **收口**：已由 `bt.ibb`／`bt.km`／`bt.manifests` 實作（bt.km 缺出處時誠實標 NotSupported） | Services/BootGuardFactsService.cs:42-44,71；Tests/BootGuardFactsTests.cs |
| U10-FW-0050 | 映像 FV/FFS 分解 | `fw.fv.cross` | **新列**：與 `fw.fv` 的差異在於——既有僅 FV 空間統計（FVS 數／剩餘百分比），FV/FFS 逐項分解未實作 | Services/FirmwareDeepFactsService.cs:7,33,52 |
| U10-FW-0051 | TPM 存在性與來源（TBS／WMI 雙路徑） | `tpm.present.cross` | **新列**：與 `tpm.present`（TBS 路徑）＋`sb.tpmdev`（SMBIOS）＋`ac.tpm2`（ACPI）的差異在於——WMI Win32_TPM 備援路徑未實作 | Services/FactKeyCatalog.cs:952；Services/TpmFactsService.cs（TBS）；`Win32_TPM` 於 Services 全庫 0 命中 |
| U10-FW-0061 | PCR 逐 bank 現值（SHA-1/256/384/512） | `tpm.pcr.{bank}` | **收口**：已由 `tpm.pcr.{sha1\|sha256\|sha384}.{0..23}` 動態家族＋`tpm.pcr.banks` 實作；SHA-512 bank 為同家族待補的索引值 | Services/TpmFactsService.cs:7,129,202-203,248-253,335 |
| U10-FW-0075 | TCG log 逐事件類型統計 | `tpm.ev.types.cross` | **新列**：與 `tpm.tcg_log`（摘要）／`fw.tcgl.phase`（分階段）的差異在於——逐事件類型統計表未實作（`tpm.ev.` 全庫 0 命中） | Services/TpmFactsService.cs:131,314；Services/FirmwareDeepFactsService.cs:32；Services/FactKeyCatalog.cs:954 |
| U10-FW-0104 | 周邊韌體盤點全表（擴充為全裝置） | `pdev.fw.inventory` | **新列**：與 `pdev.fw.updatable`／`pdev.fw.version`／`pdev.fw.delta` 的差異在於——既有只盤「可更新」裝置，全裝置韌體盤點未實作 | Services/FactKeyCatalog.cs:628-629；Services/FirmwareUpdatableFactsService.cs:8,53,59；Services/PeripheralFirmwareFactsService.cs:15 |
| U10-FW-0113 | 韌體風險陳述六把（收口引用） | `fw.risk.cross` | **收口**：已由 `fw.risk.facts.{parts,level,gap,baseline,delta,…}` 六把實作並接入掃描 | Services/FirmwareRiskFactsService.cs:39-43；Services/FactKeyCatalog.cs:410-415；Services/FirmwareDocReconcileFactsService.cs:55 |
| U10-NV-0068 | 0x03 韌體槽（收口） | `nvme.fw.slots.cross` | **收口**：已由 NvmeFwSlot 解碼器（log page 0x03、七槽＋AFI）實作，並併入 `pdev.fw.updatable` | Services/NvmeFwSlot.cs:3-8；Services/FirmwareUpdatableFactsService.cs:21,80,207-216 |

### OS／WMI 域

| U10 ID | 項目 | 候選鍵 | 結論 | 證據 |
|---|---|---|---|---|
| U10-OS-0025 | Win32_Processor（收口未用欄位清單） | `wmi.cpu.gap` | **新列**：Win32_Processor 已在多服務查詢，但「未用欄位清單」盤點本身無任何實作或鍵 | Services/CpuPinoutService.cs、Services/PcieAnalysisService.cs、Services/AdvancedSupplementFactsService.cs（已用）；`wmi.` 前綴鍵於 Services 全庫 0 命中 |
| U10-OS-0028 | Win32_BIOS（與 SMBIOS 對帳收口） | `wmi.bios.cross` | **收口**：已由 `sb.cross.bios` 實作（SMBIOS×WMI Win32_BIOS 逐欄對照） | Services/SmbiosCrossFactsService.cs:34-36,144 |
| U10-OS-0029 | Win32_BaseBoard／Win32_SystemEnclosure（查重收口） | `wmi.board.cross` | **收口**：已由 `sb.cross.board`／`sb.cross.system` 實作 | Services/SmbiosCrossFactsService.cs:37-38,149-154；Services/AssetFactsService.cs（Win32_BaseBoard/SystemEnclosure 已用） |
| U10-OS-0030 | Win32_PhysicalMemory（作 SPD 對帳第三源） | `wmi.mem.cross` | **新列**：與 SPD 對帳（SpdConsistencyAuditService）的差異在於——既有三源為 Native SPD／CPU-Z／SMBIOS，WMI Win32_PhysicalMemory 未納入對帳 | Services/SpdConsistencyAuditService.cs:17-58,84-86（SpdAuditSourceKind 三值）；Services/PlatformGovernanceFactsService.cs:231（WMI 有查詢但未入對帳） |
| U10-OS-0033 | Win32_DiskDrive（擴充未用欄位） | `wmi.disk.gap` | **新列**：Win32_DiskDrive 已在多服務查詢，分區/匯流排類型碼等未用欄位盤點無實作 | Services/DiskInfoService.cs、Services/EvidenceLabService.cs、Services/ChassisAndHpaFactsService.cs（已用）；gap 盤點無鍵無測試 |
| U10-OS-0038 | Win32_VideoController（擴充 VRAM 類型/掃描模式欄） | `wmi.gpu.gap` | **新列**：Win32_VideoController 已查詢，但 VRAM 類型（VideoMemoryType）/掃描模式（CurrentScanMode）欄未取（全庫 0 命中）；GX 域已有的是 VBIOS 版本與 VRAM 使用歷史，非該 WMI 欄 | Services/DisplayAdapterFactsService.cs、Services/GpuDeepFactsService.cs:8,34（gx.vbios／hist.vram）；`CurrentScanMode`/`VideoMemoryType` 0 命中 |
| U10-OS-0040 | Win32_NetworkAdapter(+Configuration)（查重收口） | `wmi.net.gap` | **新列**：與既有用法的差異在於——Win32_NetworkAdapter 已用，Win32_NetworkAdapterConfiguration 全庫 0 命中 | Services/NicHealthFactsService.cs、Services/OperationsInventoryFactsService.cs（Adapter 已用）；`NetworkAdapterConfiguration` 0 命中 |
| U10-OS-0057 | Win32_PnPEntity（全量；問題碼過濾用） | `wmi.pnpentity` | **收口**：類別查詢已實作（匯流排深入等三處）；問題碼過濾為應用側篩選，屬增量 | Services/BusDeepDiveFactsService.cs:308；Services/BluetoothBatteryService.cs、Services/CameraFactsService.cs |
| U10-OS-0061 | Win32_TPM（已有 WMI 備援路徑——收口） | `wmi.tpm.cross` | **新列**：Win32_TPM 全庫 0 命中——目錄自稱「已有 WMI 備援路徑」與實證不符；既有 TPM 來源是 TBS／SMBIOS／ACPI，不是 WMI | Services/TpmFactsService.cs（TBS）；Services/SmbiosTpmFactsService.cs:98；`Win32_TPM` 0 命中 |

### 安全域（SE）

| U10 ID | 項目 | 候選鍵 | 結論 | 證據 |
|---|---|---|---|---|
| U10-SE-0003 | VBS/HVCI 啟用鏈（含未啟用原因碼） | `sec.vbs.state` | **新列**：與 `virt.vbs`／`platform.hvci` 的差異在於——既有報三態現值與 CodeIntegrity 旗標，「未啟用原因碼」未實作 | Services/VirtualizationFactsService.cs:92-101；Services/BackendEnvironmentService.cs:70-75 |
| U10-SE-0036 | 週邊韌體可攻擊面陳述（可更新 vs 不可更新） | `sec.pdevfw.surface` | **新列**：與 `pdev.fw.updatable` 的差異在於——既有是逐裝置三態事實，「可更新 vs 不可更新」的彙整攻擊面陳述鍵未實作 | Services/FirmwareUpdatableFactsService.cs:8,53-59 |
| U10-SE-0043 | 憑證主體名稱絕不出現——L3 禁項守門 | `sec.l3.certname.gate` | **收口**：已由 P1-048 L3 禁項掃描守門＋「自簽憑證不列主體名稱」正對照測試實作 | Tests/SensitivityPolicyTests.cs:11,134；Tests/DataSecurityDeepFactsTests.cs:56-60 |
| U10-SE-0044 | 匯出遮蔽預覽與解除留痕（L2 解除逐條紀錄） | `sec.export.unmask.ledger` | **收口**：已由 P1-047 L2 選擇性解除＋「解除遮蔽須留可稽核紀錄」規則實作 | Services/SensitivityPolicy.cs:158；Tests/SensitivityPolicyTests.cs:11 |
| U10-SE-0047 | 韌體風險六把報告節（報告整合） | `rep.fw.risk` | **收口**：`fw.risk.facts.*` 六把已產出並接入 EvidenceLabService 掃描管線，隨事實卡進報告 | Services/FirmwareRiskFactsService.cs:39-43；Services/EvidenceLabService.cs:699,2062-2064 |
| U10-SE-0051 | 出網零聲明之驗證（egress.allowlist 空集守門） | `sec.egress.zero` | **收口**：egress.allowlist 對空集有一等分支（空清單直接產出事實，不冒充有政策） | Services/EgressPolicy.cs:26,58-60 |
| U10-SE-0054 | Smart App Control 三態 | `sec.sac.state` | **收口**：已由 `ds.sac`／`ds.sac.deep` 實作（三態分明，缺政策鍵標 NotApplicable） | Services/DataSecurityDeepFactsService.cs:6,19,34,132-142 |
| U10-SE-0058 | 驅動服務 SDDL 異常清單（擴展至硬碟控制器類） | `sec.sddl.controllers` | **收口**：已由 `sa.sddl.summary`／`sa.sddl.deep` 實作（逐服務讀安全描述元）；控制器類屬同一掃描的篩選檢視 | Services/SecurityAuditDeepFactsService.cs:13,48,410 |
| U10-SE-0059 | 替代資料流掃描（驅動檔案 ADS） | `sec.ads.drivers` | **收口**：已由 `sa.ads.summary`／`sa.ads.deep` 實作（SA-011，掃系統目錄可執行檔，驅動檔在其中） | Services/SecurityAuditDeepFactsService.cs:11,37,96-105；Services/FactCoverageReport.cs:447,565 |
| U10-SE-0060 | 已安裝程式簽章狀態分布（硬體相關子集） | `sec.apps.signed.hw` | **收口**：已由 `sa.uninst.summary`／`sa.uninst.deep` 實作（已安裝程式簽章審計）；硬體子集為篩選檢視 | Services/SecurityAuditDeepFactsService.cs:11 |
| U10-SE-0061 | 登入歷史硬體關聯（裝置插入×登入時間交集） | `sec.logon.hw.correlate` | **新列**：與 `sa.logon.summary`／`sa.logon.deep` 的差異在於——既有僅 4624/4625/4740 事件計數，裝置插入與登入的時間交集未實作 | Services/SecurityAuditSupplementFactsService.cs:21,32,43 |

### 突變／視覺化／UI／平臺／引擎／守門域（MT／VZ／UI／PF／EN／GV）

| U10 ID | 項目 | 候選鍵 | 結論 | 證據 |
|---|---|---|---|---|
| U10-MT-0036 | 季節模式偵測（收口引用） | `mt.seasonal.cross` | **收口**：已由 `bl.season.summary` 實作（BL-005，含樣本天數門檻） | Services/BaselineForecastFactsService.cs:7,20,31,40 |
| U10-MT-0044 | 解碼器全量單元測試（擴展目標成文） | `mt.decoder.tests` | **收口**：Stryker 突變測試已指向 XinSpect.Decoders 全部 .cs（含門檻與覆蓋分析） | stryker-config.json（project=XinSpect.Decoders，mutate `**/*.cs`，thresholds 90/70/70）；StrykerOutput/ |
| U10-MT-0049 | 解碼方法之規格出處註記守門 | `mt.decoder.citation.gate` | **收口**：已由 SpecRefCoverageTests 實作（每個公開解碼方法都帶規格引用，佔位字串擋下） | Tests/SpecRefCoverageTests.cs（兩個 [Fact]） |
| U10-VZ-0014 | 桑基圖（收口引用） | `viz.sankey.cross` | **收口**：已由 `vr.sankey` 實作 | Services/VisualizationFactsService.cs:24,31 |
| U10-VZ-0016 | 拓撲樹（擴充互動） | `viz.tree.cross` | **收口**：已由 `vr.tree` 實作；互動為既有樹上的呈現增量 | Services/VisualizationFactsService.cs:33 |
| U10-VZ-0064 | 事實總表頁 sparkline 欄（擴充） | `ui.factatlas.spark` | **新列**：與 FactAtlas 的差異在於——Views/FactAtlasView 存在，但 sparkline 全庫 0 命中 | Views/FactAtlasView.xaml(.cs)、Services/FactAtlasService.cs；`sparkline` 於 Views/Services 0 命中 |
| U10-VZ-0069 | 3D 拓撲持續不做（重申） | `viz.no3d.cross` | **收口**：政策重申與程式碼現狀一致——確無 3D 拓撲實作 | csproj 無 Helix/3D 套件；`Viewport3D`/`HelixToolkit` 於 Views/Controls 0 命中 |
| U10-VZ-0078 | 釘選感測器面板（常駐小窗/迷你浮窗） | `ui.pinned.panel` | **收口**：MiniOverlayWindow 已是常駐感測器小窗（CPU 負載/溫度等即時值） | MiniOverlayWindow.xaml:5,28,64-74（「曦覽 監視器」＋Live 繫結） |
| U10-UI-0010 | 逐頁說明（HelpCatalog——擴充覆蓋新域） | `ui.help.coverage` | **收口**：HelpCatalog 已實作（逐頁說明＋自足 HTML 手冊產生）；新域條目為內容增量 | Services/LanguageService.cs:20；Services/InterchangeAndReportFactsService.cs:72,478-479 |
| U10-UI-0014 | 三語切換即時生效（收口檢查項） | `ui.lang.live.cross` | **收口**：LanguageService 有語言切換事件、訂閱者重新產生動態文字 | Services/LanguageService.cs:53,302；Views/AboutView.xaml.cs:18；Controls/HelpDot.xaml.cs:27；MiniOverlayWindow.xaml.cs:25；Services/DriverReadyService.cs:54（訂閱 Changed 後重新產生動態文字） |
| U10-UI-0015 | 三語鍵同步守門（轉常設守門） | `ui.lang.sync.gate` | **收口**：已由語言樹／往返測試常設守門＋lang-* 工具鏈實作 | Tests/LanguageTreeTests.cs、Tests/LanguageRoundTripTests.cs、Tests/LanguageServiceTests.cs |
| U10-UI-0032 | 迷你浮窗擴充（自訂內容版） | `ui.mini.custom` | **新列**：與 MiniOverlayWindow 的差異在於——既有內容固定（CPU/溫度等監視器欄位），使用者自訂內容版未實作 | MiniOverlayWindow.xaml:21-74（固定欄位佈局） |
| U10-UI-0033 | 應用內更新紀錄檢視（頁面收口） | `ui.changelog.view` | **收口**：ChangelogCatalog 已實作，關於頁呈現版本條目並有守門測試 | Services/LifecycleAndReportsFactsService.cs:187-206；Views/AboutView.xaml（ChangelogTests.cs:51 斷言） |
| U10-UI-0034 | 關於頁授權清單（收口） | `ui.about.licenses` | **新列**：與 THIRD-PARTY-NOTICES 的差異在於——檔案在根目錄存在，但關於頁無授權清單呈現（grep 0 命中） | THIRD-PARTY-NOTICES.md（根目錄）；Views/AboutView.xaml(.cs) 無 THIRD-PARTY/授權清單命中 |
| U10-PF-0001 | HTML 報告產生器（擴充節架構） | `rep.html.cross` | **收口**：已由 ReportService 實作（HTML／Markdown／純文字三格式匯出） | Services/ReportService.cs:28-37 |
| U10-PF-0009 | 匯出簽章（內嵌雜湊鏈；擴至 JSON/CSV） | `exp.hash.chain` | **新列**：與既有雜湊鏈的差異在於——審計日誌（verify-audit）與證據包 manifest 已有雜湊鏈，報告 JSON/CSV 匯出檔本身內嵌簽章未見實作 | Services/CliService.cs:134,252,464（verify-audit 雜湊鏈）；Services/EvidenceExportFactsService.cs:38,90-92（manifest 雜湊）；報告匯出（ReportService.cs:35-37）無簽章 |
| U10-PF-0012 | Headless 掃描模式（擴充分域逾時參數） | `cli.headless.cross` | **收口**：已由 CliService headless 模式實作（目錄 JSON 自述、--query/--out、零網路） | Services/CliService.cs:9,127-132 |
| U10-PF-0017 | PowerShell 模組逐域 cmdlet（補齊新域） | `ps.modules.gap` | **新列**：與 XinSpect.psm1 的差異在於——現僅 `Get-XinSpectEvidence` 一個函式，逐域 cmdlet 未實作 | XinSpect.psm1:7（`function Get-XinSpectEvidence`，全檔僅此一函式） |
| U10-PF-0028 | 安裝器（收口檢查） | `installer.cross` | **收口**：Installer/ 已存在（Install.cs＋XinSpectDeploy.exe） | Installer/Install.cs、Installer/XinSpectDeploy.exe |
| U10-PF-0029 | 單一檔案發佈驗證自動化（轉守門） | `release.singlefile.gate` | **收口**：release.ps1 以 PublishSingleFile 發佈、發佈前全測試綠、發佈後驗證器必須 OK；另有 ReleasePipelineTests | Tools/release.ps1:14,137-142；Tools/verify-release.ps1；Tests/ReleasePipelineTests.cs |
| U10-EN-0012 | 知識包版本演進與撤回（收口成流程） | `kp.evolution.proc` | **新列**：與 `kp.version`／`kp.digest`／`kp.swap.audit` 的差異在於——既有是知識包現值鍵，版本演進/撤回無常設流程或鍵；撤回僅有先例文件（FIT 撤回） | Services/FactKeyCatalog.cs（kp.version/kp.digest/kp.swap.audit/kp.update_channel）；work/WITHDRAWN-P2-052-FIT.md；docs/ULTIMATE-1000/GATE-LOG-256.md、GATE-LOG-260.md |
| U10-GV-0002 | 守門白名單上限（防白名單腐化） | `guard.whitelist.budget` | **收口**：WiringGuardTests「白名單不得無限長大」（上限斷言）＋GuardPolicyTests P1-061 白名單上限 | Tests/WiringGuardTests.cs:39,55,135-140；Tests/GuardPolicyTests.cs:10-11 |
| U10-GV-0003 | 誤報治理（FP 紀錄；放寬不接受） | `guard.fp.governance` | **收口**：GuardPolicyTests 已守 P1-063 誤報治理 | Tests/GuardPolicyTests.cs:11（P1-063） |
| U10-GV-0004 | 突變測試目標擴充（逐版提升覆蓋域） | `guard.mutation.schedule` | **新列**：與 Stryker 現況的差異在於——已有設定與輸出，但「逐版排程提升覆蓋域」的機制無實作 | stryker-config.json；StrykerOutput/（皆為一次性設定，無排程/成長管控） |
| U10-GV-0005 | 測試數單調閘（TestSuiteBaseline——常設） | `guard.tests.monotonic` | **收口**：已由 TestSuiteBaseline 常設實作 | Tests/TestSuiteBaseline.cs |
| U10-GV-0006 | 鍵目錄與原始碼逐鍵相等閘 | `guard.catalog.parity` | **收口**：KeyCatalogPolicyTests 已守雙向相等（目錄裡的每個鍵都要在掃描結果裡找得到） | Tests/KeyCatalogPolicyTests.cs:41,73-88 |
| U10-GV-0007 | 執行期產鍵必須登記閘 | `guard.runtime.keys` | **收口**：已由 FactKeyRuntimeReconcileTests 常設實作 | Tests/FactKeyRuntimeReconcileTests.cs；Services/FactKeyDynamicCatalog.cs:58-60 |
| U10-GV-0008 | 更新紀錄最新筆=版號閘 | `guard.changelog.version` | **收口**：ChangelogTests 已斷言關於頁版號與 Changelog 最新筆一致 | Tests/ChangelogTests.cs:51 |
| U10-GV-0009 | 檢視 XAML 不得留舊版號閘 | `guard.xaml.version` | **收口**：ChangelogTests「檢視的XAML不得留下舊版號」常設實作 | Tests/ChangelogTests.cs:242-261（掃 MainWindow.xaml／AboutView.xaml） |
| U10-GV-0011 | 覆蓋申報基線（逐版成長管控） | `guard.coverage.baseline` | **新列**：與 CoverageFactsService 的差異在於——既有攤開覆蓋申報現值（ng.coverage.*），「已檢視未覆蓋清單逐版成長管控」無實作 | Services/CoverageFactsService.cs:4-5,33-38；Services/CoverageService.cs:11-15 |
| U10-GV-0012 | 文件對帳閘（擴至本目錄文件） | `guard.docs.reconcile` | **新列**：與 work/UltimateAudit 的差異在於——該稽核工具明載「住在未入版控的暫存區」，未成常設閘，本目錄（U10）文件未納對帳 | Tests/PlanLibraryTests.cs:14（「稽核工具（work/UltimateAudit/）住在未入版控的暫存區」）；Tools/ 僅 publish_release.py／release.ps1／verify-release.ps1 |
| U10-GV-0014 | 發佈驗證（release.verify——每版自動） | `guard.release.verify` | **收口**：verify-release.ps1＋ReleasePipelineTests 已常設 | Tools/verify-release.ps1；Tests/ReleasePipelineTests.cs；Tests/ReleaseIntegrityTests.cs |
| U10-GV-0015 | 可重現建置（兩次建置雜湊一致） | `guard.build.repro` | **新列**：與發佈驗證的差異在於——verify-release.ps1 驗發佈產物，「兩次建置雜湊一致」比對全庫 0 命中 | Tools/verify-release.ps1（存在）；`reproducible`／「兩次建置」於 Tests/Tools 0 命中 |
| U10-GV-0036 | 守門執行時間預算（GuardBudget——新閘納入） | `guard.time.budget` | **收口**：GuardBudgetTests 已常設 | Tests/GuardBudgetTests.cs |
| U10-GV-0040 | 目錄文件稽核工具自跑（入版控且會被跑） | `guard.audit.runs` | **新列**：稽核工具已寫（work/UltimateAudit），但測試註解明載它「住在未入版控的暫存區」——「入版控且會被跑」兩個條件皆未滿足 | work/UltimateAudit/Program.cs（存在）；Tests/PlanLibraryTests.cs:14（明載未入版控） |

---

## 三、結語

### 收口列（49 筆）的目錄處置建議

一句話：**目錄該列應把「〔查重——〕」改標「收口」，並把結論欄指向本表所引的既有鍵**。重點指向：`mc.version.cross`（CP-0217）、`numa.slit.nodes`＋`numa.distance.*`（CP-0223/AC-0048）、`mem.latency.curve`→LatencyCurveService（MM-0040）、`cm.whea.timeline`（MM-0059）、`sb.memmap.*`（SB-0097）、`ac.srat.*`（AC-0047）、`ac.bgrt/spcr/dbg2.deep`（AC-0043）、`spi.bios_hash`（FW-0037）、`bt.*`（FW-0046）、`tpm.pcr.{alg}.{i}`（FW-0061）、`fw.risk.facts.*`（FW-0113/SE-0047）、NvmeFwSlot→pdev.fw.updatable（NV-0068）、`sb.cross.bios/board`（OS-0028/0029）、P1-047/P1-048 守門（SE-0043/0044）、`ds.sac.deep`（SE-0054）、`sa.sddl/ads/uninst`（SE-0058/0059/0060）、`egress.allowlist` 空集分支（SE-0051）、`bl.season.summary`（MT-0036）、SpecRefCoverageTests（MT-0049）、`vr.sankey/tree`（VZ-0014/0016）、MiniOverlayWindow（VZ-0078）、HelpCatalog（UI-0010）、語言守門（UI-0014/0015）、ChangelogCatalog＋關於頁（UI-0033）、ReportService（PF-0001）、headless（PF-0012）、Installer/（PF-0028）、release.ps1＋verify-release.ps1（PF-0029/GV-0014）、以及 GV 系列既有守門（GV-0002/0003/0005/0006/0007/0008/0009/0036）。U10-VZ-0069（3D 不做）建議改標「重申確認」而非收口——它不是能力，是與現狀一致的政策。

### 新列列（30 筆）——可進下一輪實作的鍵

**硬體／韌體域（證據最硬、可直接開工）**：
- `cpuid.12.epc{n}`（CP-0148）——leaf 0x12 EPC 逐銀行，CpuIdService 已有 CPUID 通路。
- `mem.mmio.total`（MM-0052）——補 ACPI/E820 交叉，sb.memmap.total 已是半成品。
- `sb.type45.pdev.cross`（SB-0161）——Type 45 解碼全新，但 pdev.fw.updatable 已是對帳對象。
- `acpi.tcpa.cross`（AC-0042）——TCPA 表，AcpiTpm2WpbtFactsService 旁邊加即可。
- `uefi.dbt`／`uefi.dbr`（FW-0005）——UefiSignatureFactsService 同構擴充。
- `uefi.boot.gaps`（FW-0021）——uefi.boot.{n} 家族上的彙總鍵，成本低。
- `fw.fv.cross`（FW-0050）——FV/FFS 逐項分解，FirmwareDeepFactsService 延伸。
- `tpm.ev.types.cross`（FW-0075）——TCG log 逐事件類型統計。
- `pdev.fw.inventory`（FW-0104）——全裝置盤點（不止可更新者）。
- `tpm.pcr.sha512.{i}`（FW-0061 的補項）——同家族補一個 bank 索引。

**OS／SE 域**：
- `wmi.tpm.cross`（OS-0061/FW-0051 共用）——Win32_TPM WMI 備援路徑，目錄自稱已有、實證沒有，**優先**。
- `wmi.mem.cross`（OS-0030）——把 Win32_PhysicalMemory 納入 SpdConsistencyAudit 第四源。
- `wmi.net.gap`（OS-0040）——補 Win32_NetworkAdapterConfiguration。
- `sec.vbs.state`（SE-0003）——VBS/HVCI 未啟用原因碼。
- `sec.logon.hw.correlate`（SE-0061）——裝置插入×登入交集。
- OS-0025/0033/0038 的三把 `wmi.*.gap`——性質是盤點/成文工具，建議合併成一個「WMI 未用欄位盤點」項再做。
- `sec.pdevfw.surface`（SE-0036）——pdev.fw.updatable 的彙整陳述鍵，成本極低。

**UI／PF／EN／GV 域**：
- `ui.about.licenses`（UI-0034）——關於頁授權清單，材料（THIRD-PARTY-NOTICES.md）已在。
- `ui.factatlas.spark`（VZ-0064）、`ui.mini.custom`（UI-0032）——既有視圖上的增量。
- `ps.modules.gap`（PF-0017）——psm1 逐域 cmdlet。
- `exp.hash.chain`（PF-0009）——報告 JSON/CSV 內嵌雜湊鏈（審計鏈基建已有）。
- `kp.evolution.proc`（EN-0012）——知識包撤回流程成文化（先例文件已有）。
- `guard.build.repro`（GV-0015）——兩次建置雜湊比對，release.ps1 旁加一步。
- `guard.coverage.baseline`（GV-0011）、`guard.mutation.schedule`（GV-0004）——成長管控類，需先定義基線存放格式再實作。
- `guard.docs.reconcile`＋`guard.audit.runs`（GV-0012/0040）——同一件事：把 work/UltimateAudit 入版控（建議 Tools/ 或 tests/）並納入常設執行，兩列一次收掉。

### 誠實聲明

- 收口判定以「標題能力的核心已有可指認的實作」為準；括號內的小增量（如 SHA-512 bank、問題碼過濾、分域逾時參數）在證據欄如實標注，未因此改判新列——若圈選時想要連增量一起成項，可自行升格。
- OS-0057（PnPEntity）、SE-0058（SDDL 控制器類）、SE-0059/0060 的「收口」含篩選檢視性質的差異，已逐欄註明。
- 本判定未修改任何既有檔案；`work/u10_dedup/report.md`／`report.json` 保持原樣。

---

## 更正（2026-10-25，第 269 輪）

- **GV-0012／GV-0040 的證據引用了過期註解**。兩列的證據欄引 `Tests/PlanLibraryTests.cs:14`（類別註解寫「稽核工具（work/UltimateAudit/）住在未入版控的暫存區」），並據此判「入版控且會被跑」兩個條件皆未滿足。**現狀與此不符**：
  - `work/UltimateAudit/` 自 2026-10-18 起（commit `78ba8a0`「計畫庫稽核：修好那支『有入版控但沒有人在跑』的工具，並留下兩份證據」）已整目錄入版控——`git ls-files work/UltimateAudit` 可證 9 個檔案（Program.cs、UltimateAudit.csproj 等）都在追蹤清單裡。
  - 「會被跑」的常設化也已存在：`Tests/PlanLibraryTests.cs` 本身就是常設閘；本輪再於 `Tests/Ultimate10000CatalogTests.cs` 追加「收口判定統計必須自洽」「收口證據必須可回訪」兩條常設測試（文件統計自洽＋證據可回訪），把文件對帳也納入常設執行。
  - 上面那張表與「新列列」清單**保持原樣不改**——判定結論（該二列當時的能力缺口）不因證據過期而翻盤，只更正證據陳述。PlanLibraryTests 類別註解裡的那句過期話以本節為準。

## 更正之二（2026-10-25，第 269 輪）：U10-OS-0061（wmi.tpm.cross）判定有誤

- 判定欄寫「`Win32_TPM` 全庫 0 命中」——**這是誤判**：查證用的是**大小寫敏感**的 grep（`Win32_TPM`），而原始碼寫的是 **`Win32_Tpm`**。實情是 WMI 備援通路**早已存在**，全庫有兩處：
  - `Services/TpmFactsService.cs:46`——`root\cimv2\security\microsofttpm` 命名空間的 `Win32_Tpm`（`ReadWindowsView`，TBS 不可用時的備援視圖，讀 SpecVersion／ManufacturerIdTxt／三個 `_InitialValue` 狀態欄）；
  - `Services/TpmSecureBootService.cs:23-24`——同一命名空間同一類別（零特權讀 SpecVersion／Enabled／Activated）。
- 因此該列應改標：**「收口（通路）＋新列（對照鍵）」**——WMI 備援通路早已存在＝收口；尚未存在的是「TBS 視圖 × WMI 視圖」的對照**事實鍵**（`tpm.view.cross`，第 269 輪以新服務 `Services/TpmViewCrossFactsService.cs` 補上）。此修正同樣適用 FW-0051 判定裡「WMI Win32_TPM 備援路徑未實作」那句——那半句只對「對照鍵」成立，不對「通路」成立。
- 判定欄其餘部分（既有 TPM 來源是 TBS／SMBIOS／ACPI）不受此更正影響——WMI 備援與它們並存，是並列的第四個來源。上方既有表格與前一節更正**保持原樣不改**。
