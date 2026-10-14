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
    public static void LoadUsermodeFacts(EvidenceLabService svc, IStorageReliabilitySource? storageReliability = null)
    {
        try { svc.LoadStorageReliability(storageReliability ?? new StorageReliabilityWmiSource()); }
        catch { /* 附加功能：讀不到由 StorageReliabilityFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDriverInspection(); }
        catch { /* 附加功能：讀不到由 DriverInspectionFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadSetupTimeline(); }
        catch { /* 附加功能：讀不到由 SetupApiTimelineService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEtlReadback(); }
        catch { /* 附加功能：讀不到由 EtlReadbackService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEspScan(); }
        catch { /* 附加功能：讀不到由 EspScanService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadAudioEndpoints(); }
        catch { /* 附加功能：讀不到由 AudioEndpointFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadBootTiming(); }
        catch { /* 附加功能：讀不到由 BootTimingFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadNetOffload(); }
        catch { /* 附加功能：讀不到由 NetOffloadFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadListenPorts(); }
        catch { /* 附加功能：讀不到由 ListenPortFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadLocalSecurityAudit(); }
        catch { /* 附加功能：讀不到由 LocalSecurityAuditService 標三態，不在這裡中斷啟動 */ }
        // Vol 2 批次（2026-10-11）：自我完整性／CVE 離線對照／伺服器摘要／情境診斷／SBOM——皆唯讀、
        // 且都便宜（各毫秒至百毫秒級）；「磁碟為什麼滿」（SG-002）預設只掃暫存與傾印根，
        // 不含使用者設定檔（整份遞迴要數十秒，屬明示觸發）——理由寫在 DiskFullFactsService.DefaultRoots。
        try { svc.LoadSelfIntegrity(); }
        catch { /* 附加功能：讀不到由 SelfIntegrityFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadCveOffline(); }
        catch { /* 附加功能：讀不到由 CveOfflineFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadServerCompliance(); }
        catch { /* 附加功能：沒有角色事實時由服務標「尚未讀取」，不在這裡中斷啟動 */ }
        try { svc.LoadServerRoleDeep(); }
        catch { /* 附加功能：讀不到由 ServerRoleDeepFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadServerRoleConditional(); }
        catch { /* 附加功能：讀不到由 ServerRoleConditionalFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDiskFull(); }
        catch { /* 附加功能：讀不到由 DiskFullFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadFreezeDiagnosis(); }
        catch { /* 附加功能：讀不到由 FreezeDiagnosisFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadMaintenance(); }
        catch { /* 附加功能：讀不到由 MaintenanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEnvironmentRuntime(); }
        catch { /* 附加功能：讀不到由 EnvironmentRuntimeFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadClosing(); }
        catch { /* 附加功能：讀不到由 ClosingFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadQueryInsight(); }
        catch { /* 附加功能：讀不到由 QueryInsightFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadTrustOps(); }
        catch { /* 附加功能：讀不到由 TrustOpsFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadZeroTrust(); }
        catch { /* 附加功能：讀不到由 ZeroTrustFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDataSecurity(); }
        catch { /* 附加功能：讀不到由 DataSecurityFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEndpointPolicy(); }
        catch { /* 附加功能：讀不到由 EndpointPolicyFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadScenarios(); }
        catch { /* 附加功能：讀不到由 ScenarioGuidanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadMultimedia(); }
        catch { /* 附加功能：讀不到由 MultimediaFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadExportCache(); }
        catch { /* 附加功能：讀不到由 JsonlExportService／QueryResultCache 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadMaintenanceDashboard(); }
        catch { /* 附加功能：讀不到由 MaintenanceDashboardFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadVolumeEncryption(); }
        catch { /* 附加功能：讀不到由 VolumeEncryptionFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadQueryGovernance(); }
        catch { /* 附加功能：讀不到由 QueryGovernanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadInterchangeReports(); }
        catch { /* 附加功能：讀不到由 InterchangeAndReportFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadAutomationGuidance(); }
        catch { /* 附加功能：讀不到由 AutomationAndGuidanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadRecoveryPowerInterop(); }
        catch { /* 附加功能：讀不到由 RecoveryPowerAndInteropFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEnvironmentDiagnostics(); }
        catch { /* 附加功能：讀不到由 EnvironmentDiagnosticsFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadAdvancedSupplement(); }
        catch { /* 附加功能：讀不到由 AdvancedSupplementFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadOperationsInventory(); }
        catch { /* 附加功能：讀不到由 OperationsInventoryFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadNetworkSystemInventory(); }
        catch { /* 附加功能：讀不到由 NetworkSystemInventoryFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadInventoryGovernance(); }
        catch { /* 附加功能：讀不到由 InventoryGovernanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadNetworkSecurityInventory(); }
        catch { /* 附加功能：讀不到由 NetworkSecurityInventoryFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadFinalInventory(); }
        catch { /* 附加功能：讀不到由 FinalInventoryFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadRecoveryInventory(); }
        catch { /* 附加功能：讀不到由 RecoveryInventoryFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadRecoveryDisplay(); }
        catch { /* 附加功能：讀不到由 RecoveryDisplayFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadSecurityAuditSupplement(); }
        catch { /* 附加功能：讀不到由 SecurityAuditSupplementFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadLifecycleMaintenance(); }
        catch { /* 附加功能：讀不到由 LifecycleMaintenanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadFirmwareAudit(); }
        catch { /* 附加功能：讀不到由 FirmwareAuditFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadPlatformGovernance(); }
        catch { /* 附加功能：讀不到由 PlatformGovernanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadPlatformDeepDive(); }
        catch { /* 附加功能：讀不到由 PlatformDeepDiveFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadPlatformExpansion(); }
        catch { /* 附加功能：讀不到由 PlatformExpansionFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadPlatformReconciliation(); }
        catch { /* 附加功能：讀不到由 PlatformReconciliationFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadStorageScenarioReconciliation(); }
        catch { /* 附加功能：讀不到由 StorageAndScenarioReconciliationFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadPerformanceObservation(); }
        catch { /* 附加功能：讀不到由 PerformanceObservationFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDeepBenchSecondWave(); }
        catch { /* 附加功能：讀不到由 DeepBenchSecondWaveFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadGovernanceKnowledge(); }
        catch { /* 附加功能：讀不到由 GovernanceKnowledgeFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadBusDeepDive(); }
        catch { /* 附加功能：讀不到由 BusDeepDiveFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEngineeringGovernance(); }
        catch { /* 附加功能：讀不到由 EngineeringGovernanceFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadVisualization(); }
        catch { /* 附加功能：讀不到由 VisualizationFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadOfflineAnalysis(); }
        catch { /* 附加功能：讀不到由 OfflineAnalysisFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadProductPlatform(); }
        catch { /* 附加功能：讀不到由 ProductAndPlatformFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadMeasurementMethodology(); }
        catch { /* 附加功能：讀不到由 MeasurementMethodologyFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadLifecycleSecurity(); }
        catch { /* 附加功能：讀不到由 LifecycleSecurityFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDeepBenchMethodology2(); }
        catch { /* 附加功能：讀不到由 DeepBenchMethodology2FactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEvidenceExport(); }
        catch { /* 附加功能：讀不到由 EvidenceExportFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadExportUx(); }
        catch { /* 附加功能：讀不到由 ExportUxFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadUserExperience2(); }
        catch { /* 附加功能：讀不到由 UserExperience2FactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadGpuDeep(); }
        catch { /* 附加功能：讀不到由 GpuDeepFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadFirmwareDeep(); }
        catch { /* 附加功能：讀不到由 FirmwareDeepFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEngineeringGovernance2(); }
        catch { /* 附加功能：讀不到由 EngineeringGovernance2FactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadStorageDeep(); }
        catch { /* 附加功能：讀不到由 StorageDeepFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadBaselineForecast(); }
        catch { /* 附加功能：讀不到由 BaselineForecastFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDevDiagCorrelation(); }
        catch { /* 附加功能：讀不到由 DevDiagAndCorrelationFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadSecurityAuditDeep(); }
        catch { /* 附加功能：讀不到由 SecurityAuditDeepFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadAcpiDeep(); }
        catch { /* 附加功能：讀不到由 AcpiDeepFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDataSecurityDeep(); }
        catch { /* 附加功能：讀不到由 DataSecurityDeepFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadSbom(); }
        catch { /* 附加功能：讀不到由 SbomService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadLifecycleReports(); }
        catch { /* 附加功能：讀不到由 LifecycleAndReportsFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadGovernanceExtra(); }
        catch { /* 附加功能：讀不到由 GovernanceExtraFactsService 標三態，不在這裡中斷啟動 */ }
        // Vol 2 批次 B（2026-10-11）：檔案鑑識主線——Shimcache（登錄值，毫秒級）＋MountedDevices（登錄列舉，
        // 毫秒級）＋Prefetch 檔名層（每檔只讀前 80 位元組，目錄級毫秒）；Amcache（NT hive 解析）與
        // Prefetch 壓縮內含解壓屬明示觸發範圍，不在啟動路徑上。
        try { svc.LoadKnowledgePack(); }
        catch { /* 附加功能：讀不到由 KnowledgePackService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadFsForensics(); }
        catch { /* 附加功能：讀不到由 ShimcacheFactsService／MountedDevicesFactsService／PrefetchFactsService 標三態，不在這裡中斷啟動 */ }
        // 平台拓撲與資產（SMBIOS 表層深化 sb.*／asset.*／role.*／monitor.*／numa.*…）：
        // 這一組原本只有 GUI 啟動序列在載入，CLI 拿不到——同一份程式的兩條入口事實集合不同，
        // 匯出與報告就會少一截，而畫面上一切正常。改成兩條入口共用這一個組合點。
        try { svc.LoadPlatformFacts(); }
        catch { /* 附加功能：讀不到由各服務標三態，不在這裡中斷啟動 */ }
        // ACPI 表清單與表層深化（ac.*／acpi.*，usermode 以 GetSystemFirmwareTable 列舉，免驅動）：
        // 同樣原本只有 GUI 在載入——CLI 的匯出與 ng.coverage 因此在數另一份事實集合。
        try { svc.LoadAcpi(new Win32AcpiTableSource()); }
        catch { /* 附加功能：讀不到由 AcpiService 標三態，不在這裡中斷啟動 */ }
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
