using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// 驅動相依全組事實的<b>單一組合點</b>（V7 WP32）：啟動序列與 CLI 模式共用同一套後端組合——
/// WinRing0（PCI／MSR／I/O 埠／SMBus）＋MMIO 裁決鏈＋Win32 ACPI。改後端組合只改這裡，
/// 兩條入口不會各自漂移。組合本身是特權層黏合，照慣例不在單測覆蓋（測試注入假件打服務層）。
/// </summary>
public static class EvidenceCollection
{
    public static void ReloadInto(EvidenceLabService svc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        WriteGate.Reset();   // 新收集輪開新帳：上一輪的寫入不冒充這一輪（v2.51 §5.3）
        bool failed = false;
        try
        {
            using var pci = new WinRing0PciConfigReader();
            using var msr = new WinRing0KernelMsrReader();
            using var io = new WinRing0IoPortAccess();
            using var smbusBridge = WinRing0Bridge.Create(); // SMBus 走 I/O 埠；與 readers 共用驅動會話（引用計數）
            ISmbusIo? smbusIo = smbusBridge.IoPortAvailable ? new WinRing0SmbusIo(smbusBridge) : null;
            var mmio = MmioBackendSelector.Select(); // WinRing0 主力 → XsRegProbe 備援 → 帶原因三態
            try
            {
                svc.ReloadDriverBackedFacts(pci, msr, mmio, new Win32AcpiTableSource(), io, smbusIo);
            }
            catch { failed = true; throw; } // 例外上拋給閘門吞噬；遙測記一次
            finally { (mmio as IDisposable)?.Dispose(); }
        }
        finally
        {
            // 自家可觀測性（V7 WP43／A54）：只記耗時與三態/例外計數——預設關閉、匿名、不上傳
            sw.Stop();
            try
            {
                var all = svc.AllFacts;
                SelfTelemetry.Session.RecordScan(sw.Elapsed, all.Count,
                    all.Count(f => f.Availability != FactAvailability.Present), failed ? 1 : 0);
            }
            catch { /* 遙測統計失敗不影響主流程 */ }
            // 寫入稽核跟著本輪翻頁（v2.55）：帳本在 ReloadInto 開頭 Reset，畫面同步成「本次執行」
            try { svc.RaiseWriteAuditChanged(); }
            catch { /* 通知失敗不影響主流程 */ }
        }
    }

    /// <summary>
    /// usermode（不需驅動）事實的<b>單一組合點</b>：儲存可靠性計數器、驅動靜態檢視、裝置安裝時間線、
    /// .etl 讀回、ESP 檔案層掃描、音訊端點、開機計時、網路卸載、本地安全審計、
    /// 自我完整性、CVE 離線對照、伺服器摘要、磁碟用量排行、當機事件面、SBOM（皆為唯讀來源）。
    /// </summary>
    /// <remarks>
    /// <b>為什麼要另外開一個入口，而不是塞進 <see cref="ReloadInto"/>：</b>
    /// <see cref="ReloadInto"/> 是驅動後端的組合點，而它在啟動路徑上被
    /// <c>DriverEvidenceGate</c> 擋著（已有載入在跑就整批跳過）。把一條**不需要驅動**
    /// 的來源掛進去，等於讓它的有無取決於一個跟它無關的閘門。
    /// <para>
    /// <b>為什麼要有這個入口：</b>這一組事實原本接進了 <c>AllFacts</c> 與報告匯出，
    /// 卻<b>沒有任何地方呼叫載入</b>——所以在真實的 App 與 CLI 裡它永遠是空的，
    /// 而服務層的單元測試（注入假來源）全綠。單元測試測得到服務，測不到「有沒有人接線」；
    /// 把接線本身做成一個有名字的入口，就是為了讓兩條入口（UI／CLI）共用它、不再各自漂移。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 跑一個收集步驟並記下它花多久。例外的處理與原本的 try／catch 相同：記下失敗、不中斷收集。
    /// </summary>
    /// <remarks>
    /// 這是<b>步驟級</b>代價，不是逐鍵：一個步驟可能生產數十把鍵，拆不出來也不該假裝拆得出來（U10-MT-0004，第 311 輪）。
    /// </remarks>
    private static void Measure(EvidenceLabService svc, string name, Action action)
    {
        long start = Stopwatch.GetTimestamp();
        bool ok = true;
        try { action(); }
        catch { ok = false; }
        svc.RecordStep(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds, ok);
    }

    public static void LoadUsermodeFacts(EvidenceLabService svc, IStorageReliabilitySource? storageReliability = null)
    {
        svc.ClearSteps();  // 每次收集重新計時：步驟代價是「這一輪」的，不是累計
        Measure(svc, "LoadStorageReliability", () => svc.LoadStorageReliability(storageReliability ?? new StorageReliabilityWmiSource()));  // 附加功能：讀不到由 StorageReliabilityFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadDriverInspection", () => svc.LoadDriverInspection());  // 附加功能：讀不到由 DriverInspectionFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadSetupTimeline", () => svc.LoadSetupTimeline());  // 附加功能：讀不到由 SetupApiTimelineService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEtlReadback", () => svc.LoadEtlReadback());  // 附加功能：讀不到由 EtlReadbackService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEtwFilterView", () => svc.LoadEtwFilterView());  // 附加功能：讀不到由 EtwFilterViewService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadFsMetadataLatency", () => svc.LoadFsMetadataLatency());  // 附加功能：讀不到由 FsMetadataLatencyService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadPresentObservation", () => svc.LoadPresentObservation());  // 附加功能：讀不到由 PresentObservationFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadSrumReadback", () => svc.LoadSrumReadback());  // 附加功能：讀不到由 SrumReadbackService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadAmcache", () => svc.LoadAmcache());  // 附加功能：讀不到由 AmcacheFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEspScan", () => svc.LoadEspScan());  // 附加功能：讀不到由 EspScanService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadAudioEndpoints", () => svc.LoadAudioEndpoints());  // 附加功能：讀不到由 AudioEndpointFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadBootTiming", () => svc.LoadBootTiming());  // 附加功能：讀不到由 BootTimingFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadNetOffload", () => svc.LoadNetOffload());  // 附加功能：讀不到由 NetOffloadFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadListenPorts", () => svc.LoadListenPorts());  // 附加功能：讀不到由 ListenPortFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadLocalSecurityAudit", () => svc.LoadLocalSecurityAudit());  // 附加功能：讀不到由 LocalSecurityAuditService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadDeepTest", () => svc.LoadDeepTest());  // 附加功能：讀不到由 DeepTest 各服務標三態，不在這裡中斷啟動
        // 第 274 輪：ata.smart.attr.lookup 的真值（SMART 廠商屬性對照）。
        // **只能在這裡讀、不能在 AllFacts（getter）裡讀**——這條用專屬執行緒＋6 秒外層預算
        // 讀 ATA SMART 屬性表；放進 getter 等於每次「看一下事實集」都可能付一次碟 I/O。
        // NVMe 一律不讀（第 263 輪實證：本機 PhysicalDrive 2／4 會卡在核心不可中斷等待）。
        Measure(svc, "LoadSmartLookupStats", () => svc.LoadSmartLookupStats());  // 附加功能：讀不到由 SmartLookupProbe／ToSmartFact 標三態，不在這裡中斷啟動
        // 第 283 輪：內建知識表清冊（kp.builtin.*）。條數由各表數出來；
        // 其中 PCI 登錄檔那張要解壓 219 KB 的內嵌資料檔，所以放在收集鏈而不是事實集 getter。
        Measure(svc, "LoadBuiltinKnowledge", () => svc.LoadBuiltinKnowledge());  // 附加功能：讀不到由 BuiltinKnowledgeInventory 標三態，不在這裡中斷啟動
        // 第 280 輪：事件 ID 的事實（os.ev.*）。十九次事件記錄查詢，唯讀、零特權；
        // 放在有名字的收集步驟而不是事實集 getter——理由同 LoadSmartLookupStats。
        Measure(svc, "LoadEventIdEvidence", () => svc.LoadEventIdEvidence());  // 附加功能：讀不到由 EventIdEvidenceFactsService 標三態，不在這裡中斷啟動
        // 第 309 輪：逐裝置的問題碼／狀態旗標／驅動證據（os.dev.*）。唯讀 SetupAPI 列舉，
        // 幾百毫秒級；同樣放在有名字的收集步驟而不是事實集 getter。
        Measure(svc, "LoadDeviceTruth", () => svc.LoadDeviceTruth());
        // 第 312 輪：逐鍵重複性（mt.repeat.*）。連量五輪便宜的即時值，總預算 3 秒。
        Measure(svc, "LoadMeasurementRepeat", () => svc.LoadMeasurementRepeat());
        // BCD 開機順序（BD-0001／BD-0003）：一次 bcdedit /enum，5 秒上限；唯讀
        Measure(svc, "LoadBootOrder", () => svc.LoadBootOrder());
        // 讀不到由 MeasurementRepeatFactsService 標三態，不在這裡中斷啟動
  // 附加功能：列舉失敗由 DeviceTruthFactsService 記成 ReadError，不在這裡中斷啟動
        // 第 276 輪：EDID CTA-861 擴充塊（edid.cta.*）。唯讀登錄檔、零特權；
        // 讀不到（沒有 EDID／沒有 CTA 擴充塊／沒有某個資料塊）由服務寫成三態。
        Measure(svc, "LoadEdidCta", () => svc.LoadEdidCta());  // 附加功能：讀不到由 EdidCtaFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadVbsState", () => svc.LoadVbsState());  // 附加功能：讀不到由 VbsStateFactsService 標三態，不在這裡中斷啟動
        // Vol 2 批次（2026-10-11）：自我完整性／CVE 離線對照／伺服器摘要／情境診斷／SBOM——皆唯讀、
        // 且都便宜（各毫秒至百毫秒級）；「磁碟為什麼滿」（SG-002）預設只掃暫存與傾印根，
        // 不含使用者設定檔（整份遞迴要數十秒，屬明示觸發）——理由寫在 DiskFullFactsService.DefaultRoots。
        Measure(svc, "LoadSelfIntegrity", () => svc.LoadSelfIntegrity());  // 附加功能：讀不到由 SelfIntegrityFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadCveOffline", () => svc.LoadCveOffline());  // 附加功能：讀不到由 CveOfflineFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadServerCompliance", () => svc.LoadServerCompliance());  // 附加功能：沒有角色事實時由服務標「尚未讀取」，不在這裡中斷啟動
        Measure(svc, "LoadServerRoleDeep", () => svc.LoadServerRoleDeep());  // 附加功能：讀不到由 ServerRoleDeepFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadServerRoleConditional", () => svc.LoadServerRoleConditional());  // 附加功能：讀不到由 ServerRoleConditionalFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadServerRoleFilePrint", () => svc.LoadServerRoleFilePrint());  // 附加功能：讀不到由 ServerRoleFilePrintFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadDiskFull", () => svc.LoadDiskFull());  // 附加功能：讀不到由 DiskFullFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadFreezeDiagnosis", () => svc.LoadFreezeDiagnosis());  // 附加功能：讀不到由 FreezeDiagnosisFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadMaintenance", () => svc.LoadMaintenance());  // 附加功能：讀不到由 MaintenanceFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEnvironmentRuntime", () => svc.LoadEnvironmentRuntime());  // 附加功能：讀不到由 EnvironmentRuntimeFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadClosing", () => svc.LoadClosing());  // 附加功能：讀不到由 ClosingFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadQueryInsight", () => svc.LoadQueryInsight());  // 附加功能：讀不到由 QueryInsightFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadChecklist", () => svc.LoadChecklist());  // 附加功能：沒有清單檔時由 ChecklistFactsService 標不適用，不在這裡中斷啟動
        Measure(svc, "LoadTrustOps", () => svc.LoadTrustOps());  // 附加功能：讀不到由 TrustOpsFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadZeroTrust", () => svc.LoadZeroTrust());  // 附加功能：讀不到由 ZeroTrustFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadDataSecurity", () => svc.LoadDataSecurity());  // 附加功能：讀不到由 DataSecurityFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEndpointPolicy", () => svc.LoadEndpointPolicy());  // 附加功能：讀不到由 EndpointPolicyFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadScenarios", () => svc.LoadScenarios());  // 附加功能：讀不到由 ScenarioGuidanceFactsService／ScenarioSecondWaveFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadMultimedia", () => svc.LoadMultimedia());  // 附加功能：讀不到由 MultimediaFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadMultimedia", () => svc.LoadMultimedia());  // 附加功能：讀不到由 MultimediaFactsService／MediaProtectionFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadExportCache", () => svc.LoadExportCache());  // 附加功能：讀不到由 JsonlExportService／QueryResultCache 標三態，不在這裡中斷啟動
        Measure(svc, "LoadMaintenanceDashboard", () => svc.LoadMaintenanceDashboard());  // 附加功能：讀不到由 MaintenanceDashboardFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadVolumeEncryption", () => svc.LoadVolumeEncryption());  // 附加功能：讀不到由 VolumeEncryptionFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadQueryGovernance", () => svc.LoadQueryGovernance());  // 附加功能：讀不到由 QueryGovernanceFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadInterchangeReports", () => svc.LoadInterchangeReports());  // 附加功能：讀不到由 InterchangeAndReportFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadAutomationGuidance", () => svc.LoadAutomationGuidance());  // 附加功能：讀不到由 AutomationAndGuidanceFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadRecoveryPowerInterop", () => svc.LoadRecoveryPowerInterop());  // 附加功能：讀不到由 RecoveryPowerAndInteropFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEnvironmentDiagnostics", () => svc.LoadEnvironmentDiagnostics());  // 附加功能：讀不到由 EnvironmentDiagnosticsFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadAdvancedSupplement", () => svc.LoadAdvancedSupplement());  // 附加功能：讀不到由 AdvancedSupplementFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadOperationsInventory", () => svc.LoadOperationsInventory());  // 附加功能：讀不到由 OperationsInventoryFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadNetworkSystemInventory", () => svc.LoadNetworkSystemInventory());  // 附加功能：讀不到由 NetworkSystemInventoryFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadInventoryGovernance", () => svc.LoadInventoryGovernance());  // 附加功能：讀不到由 InventoryGovernanceFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadNetworkSecurityInventory", () => svc.LoadNetworkSecurityInventory());  // 附加功能：讀不到由 NetworkSecurityInventoryFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadFinalInventory", () => svc.LoadFinalInventory());  // 附加功能：讀不到由 FinalInventoryFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadRecoveryInventory", () => svc.LoadRecoveryInventory());  // 附加功能：讀不到由 RecoveryInventoryFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadRecoveryDisplay", () => svc.LoadRecoveryDisplay());  // 附加功能：讀不到由 RecoveryDisplayFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadSecurityAuditSupplement", () => svc.LoadSecurityAuditSupplement());  // 附加功能：讀不到由 SecurityAuditSupplementFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadLifecycleMaintenance", () => svc.LoadLifecycleMaintenance());  // 附加功能：讀不到由 LifecycleMaintenanceFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadFirmwareAudit", () => svc.LoadFirmwareAudit());  // 附加功能：讀不到由 FirmwareAuditFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadPlatformGovernance", () => svc.LoadPlatformGovernance());  // 附加功能：讀不到由 PlatformGovernanceFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadPlatformDeepDive", () => svc.LoadPlatformDeepDive());  // 附加功能：讀不到由 PlatformDeepDiveFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadPlatformExpansion", () => svc.LoadPlatformExpansion());  // 附加功能：讀不到由 PlatformExpansionFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadPlatformReconciliation", () => svc.LoadPlatformReconciliation());  // 附加功能：讀不到由 PlatformReconciliationFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadStorageScenarioReconciliation", () => svc.LoadStorageScenarioReconciliation());  // 附加功能：讀不到由 StorageAndScenarioReconciliationFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadPerformanceObservation", () => svc.LoadPerformanceObservation());  // 附加功能：讀不到由 PerformanceObservationFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadDeepBenchSecondWave", () => svc.LoadDeepBenchSecondWave());  // 附加功能：讀不到由 DeepBenchSecondWaveFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadGovernanceKnowledge", () => svc.LoadGovernanceKnowledge());  // 附加功能：讀不到由 GovernanceKnowledgeFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadBusDeepDive", () => svc.LoadBusDeepDive());  // 附加功能：讀不到由 BusDeepDiveFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadBusCapability", () => svc.LoadBusCapability());  // 附加功能：讀不到由 BusCapabilityFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEngineeringGovernance", () => svc.LoadEngineeringGovernance());  // 附加功能：讀不到由 EngineeringGovernanceFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadVisualization", () => svc.LoadVisualization());  // 附加功能：讀不到由 VisualizationFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadOfflineAnalysis", () => svc.LoadOfflineAnalysis());  // 附加功能：讀不到由 OfflineAnalysisFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadProductPlatform", () => svc.LoadProductPlatform());  // 附加功能：讀不到由 ProductAndPlatformFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadMeasurementMethodology", () => svc.LoadMeasurementMethodology());  // 附加功能：讀不到由 MeasurementMethodologyFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadLifecycleSecurity", () => svc.LoadLifecycleSecurity());  // 附加功能：讀不到由 LifecycleSecurityFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadDeepBenchMethodology2", () => svc.LoadDeepBenchMethodology2());  // 附加功能：讀不到由 DeepBenchMethodology2FactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEvidenceExport", () => svc.LoadEvidenceExport());  // 附加功能：讀不到由 EvidenceExportFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadExportUx", () => svc.LoadExportUx());  // 附加功能：讀不到由 ExportUxFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadUserExperience2", () => svc.LoadUserExperience2());  // 附加功能：讀不到由 UserExperience2FactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadGpuDeep", () => svc.LoadGpuDeep());  // 附加功能：讀不到由 GpuDeepFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadFirmwareDeep", () => svc.LoadFirmwareDeep());  // 附加功能：讀不到由 FirmwareDeepFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadEngineeringGovernance2", () => svc.LoadEngineeringGovernance2());  // 附加功能：讀不到由 EngineeringGovernance2FactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadStorageDeep", () => svc.LoadStorageDeep());  // 附加功能：讀不到由 StorageDeepFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadFileSystemVolume", () => svc.LoadFileSystemVolume());  // 附加功能：讀不到由 FileSystemVolumeFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadIscsiSession", () => svc.LoadIscsiSession());  // 附加功能：讀不到由 IscsiSessionFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadHyperVInventory", () => svc.LoadHyperVInventory());  // 附加功能：Hyper-V 不在時由 HyperVInventoryFactsService 標不適用，不在這裡中斷啟動
        Measure(svc, "LoadBaselineForecast", () => svc.LoadBaselineForecast());  // 附加功能：讀不到由 BaselineForecastFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadDevDiagCorrelation", () => svc.LoadDevDiagCorrelation());  // 附加功能：讀不到由 DevDiagAndCorrelationFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadSecurityAuditDeep", () => svc.LoadSecurityAuditDeep());  // 附加功能：讀不到由 SecurityAuditDeepFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadBitLockerRecovery", () => svc.LoadBitLockerRecovery());  // 附加功能：讀不到由 BitLockerRecoveryFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadAcpiDeep", () => svc.LoadAcpiDeep());  // 附加功能：讀不到由 AcpiDeepFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadCppcCapability", () => svc.LoadCppcCapability());  // 附加功能：讀不到由 CppcCapabilityFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadMousePollRate", () => svc.LoadMousePollRate());  // 附加功能：未取樣時由 MousePollRateFactsService 標不適用，不在這裡中斷啟動
        Measure(svc, "LoadDataSecurityDeep", () => svc.LoadDataSecurityDeep());  // 附加功能：讀不到由 DataSecurityDeepFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadScenarioThirdWave", () => svc.LoadScenarioThirdWave());  // 附加功能：讀不到由 ScenarioThirdWaveFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadSbom", () => svc.LoadSbom());  // 附加功能：讀不到由 SbomService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadLifecycleReports", () => svc.LoadLifecycleReports());  // 附加功能：讀不到由 LifecycleAndReportsFactsService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadGovernanceExtra", () => svc.LoadGovernanceExtra());  // 附加功能：讀不到由 GovernanceExtraFactsService 標三態，不在這裡中斷啟動
        // Vol 2 批次 B（2026-10-11）：檔案鑑識主線——Shimcache（登錄值，毫秒級）＋MountedDevices（登錄列舉，
        // 毫秒級）＋Prefetch 檔名層（每檔只讀前 80 位元組，目錄級毫秒）；Amcache（NT hive 解析）與
        // Prefetch 壓縮內含解壓屬明示觸發範圍，不在啟動路徑上。
        Measure(svc, "LoadKnowledgePack", () => svc.LoadKnowledgePack());  // 附加功能：讀不到由 KnowledgePackService 標三態，不在這裡中斷啟動
        Measure(svc, "LoadFsForensics", () => svc.LoadFsForensics());  // 附加功能：讀不到由 ShimcacheFactsService／MountedDevicesFactsService／PrefetchFactsService 標三態，不在這裡中斷啟動
        // 平台拓撲與資產（SMBIOS 表層深化 sb.*／asset.*／role.*／monitor.*／numa.*…）：
        // 這一組原本只有 GUI 啟動序列在載入，CLI 拿不到——同一份程式的兩條入口事實集合不同，
        // 匯出與報告就會少一截，而畫面上一切正常。改成兩條入口共用這一個組合點。
        Measure(svc, "LoadPlatformFacts", () => svc.LoadPlatformFacts());  // 附加功能：讀不到由各服務標三態，不在這裡中斷啟動
        // ACPI 表清單與表層深化（ac.*／acpi.*，usermode 以 GetSystemFirmwareTable 列舉，免驅動）：
        // 同樣原本只有 GUI 在載入——CLI 的匯出與 ng.coverage 因此在數另一份事實集合。
        Measure(svc, "LoadAcpi", () => svc.LoadAcpi(new Win32AcpiTableSource()));  // 附加功能：讀不到由 AcpiService 標三態，不在這裡中斷啟動
    }

    /// <summary>BIOS 區 vs 參考映像的比對（UI 與 CLI 共用入口）：組合後端後跑一次比對，回結果事實。</summary>
    public static HardwareFact CompareFlashWithReference(byte[] reference)
    {
        using var pci = new WinRing0PciConfigReader();
        var mmio = MmioBackendSelector.Select();
        try
        {
            return SpiFlashCompareService.Compare(pci, mmio, reference, DateTimeOffset.UtcNow);
        }
        finally { (mmio as IDisposable)?.Dispose(); }
    }
}
