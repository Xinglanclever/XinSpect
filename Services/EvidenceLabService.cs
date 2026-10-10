using System.Collections.ObjectModel;
using System.IO;

namespace XinSpect;

public sealed class EvidenceLabService : ObservableObject
{
    private string _status = "尚未擷取。建立時間膠囊後，才能和另一份快照逐欄比較。";
    private string _summary = "—";
    private bool _busy;

    public ObservableCollection<EvidenceFactRow> Facts { get; } = [];
    public ObservableCollection<EvidenceChangeRow> Changes { get; } = [];

    /// <summary>晶片組安全三態事實（BIOS_CNTL/SMRAMC…）。由啟動路徑以 WinRing0 後端載入；測試注入假讀取器。預設空＝尚未讀。</summary>
    public IReadOnlyList<HardwareFact> ChipsetFacts { get; private set; } = [];

    // ── 寫入稽核（v2.55，WriteGate 帳本的 UI 投影）────────────────────────────
    // 同意閘門回答「該不該做」，寫入帳本回答「點頭之後到底寫了什麼」。這兩個屬性是
    // 即時計算（帳本在 WriteGate 靜態類），通知由 EvidenceCollection.ReloadInto 的
    // finally 統一發——帳本每輪收集重置，畫面跟著翻頁，不顯示上一輪的殘影。

    /// <summary>寫入稽核一句話摘要：唯讀收集時如實說「沒有寫入」，截斷時說截斷。</summary>
    public string WriteAuditHeadline => WriteGate.DescribeSession();

    /// <summary>本次執行的逐筆寫入記錄（時間／目標／呼叫者／內容／成敗）。唯讀投影，不複製帳本所有權。</summary>
    public IReadOnlyList<WriteAuditRow> WriteAuditRows
        => [.. WriteGate.Session.Select(e => new WriteAuditRow(
            e.AtUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
            e.Target, e.Caller, e.Detail,
            e.Succeeded ? "✓" : "✗"))];

    /// <summary>收集輪結束時由 EvidenceCollection 呼叫，讓畫面上的帳本與本輪一致。</summary>
    public void RaiseWriteAuditChanged()
    {
        OnPropertyChanged(nameof(WriteAuditHeadline));
        OnPropertyChanged(nameof(WriteAuditRows));
    }

    /// <summary>
    /// 本機能力矩陣的畫面投影（v2.56）：從韌體安全列裡挑出九條 cap.* 彙總。
    /// 來源缺席顯示「無法判定」——缺席不讀成不支援，也不讀成可用（v2.52 同一條線）。
    /// </summary>
    public IReadOnlyList<EvidenceFactRow> CapabilityMatrixRows
        => [.. FirmwareSecurityRows.Where(r => r.Category == CapabilityMatrixService.Category)];

    /// <summary>以注入的 PCI 讀取器載入晶片組安全事實；讀不到由 ChipsetSecurityService 標三態，不在這裡觸發核心驅動安裝（測試用假讀取器）。</summary>
    public void LoadChipsetSecurity(IPciConfigReader reader)
    {
        ChipsetFacts = ChipsetSecurityService.Collect(reader, DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>ACPI 表三態事實（表清單 + 逐表簽章/版本/校驗和）。由啟動路徑以 Win32 來源載入；測試注入假來源。</summary>
    public IReadOnlyList<HardwareFact> AcpiFacts { get; private set; } = [];

    /// <summary>儲存可靠性計數器事實（WMI <c>MSFT_StorageReliabilityCounter</c>）。由啟動路徑載入；測試注入假來源。</summary>
    public IReadOnlyList<HardwareFact> StorageReliabilityFacts { get; private set; } = [];

    /// <summary>以注入的來源載入可靠性計數器（usermode、免管理員；讀不到由服務如實標三態）。</summary>
    public void LoadStorageReliability(IStorageReliabilitySource source)
    {
        StorageReliabilityFacts = StorageReliabilityFactsService.Collect(DateTimeOffset.UtcNow, source);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>以注入的 ACPI 來源載入表清單事實（usermode，不需驅動；讀不到由 AcpiService 標三態）。</summary>
    public void LoadAcpi(IAcpiTableSource source)
    {
        AcpiFacts = AcpiService.Collect(source, DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>載入 TPM 量測開機鏈事實（usermode 經 Windows TBS；讀不到由 TpmFactsService 標三態）。</summary>
    public void LoadTpm()
    {
        TpmFacts = TpmFactsService.Collect(DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>SPI 快閃安全三態事實（HSFSTS/FRAP/FREG/PR）。SPIBAR 經 PCI 取得、暫存器要 MMIO——驅動未載時整組三態。</summary>
    public IReadOnlyList<HardwareFact> SpiFlashFacts { get; private set; } = [];

    /// <summary>SPI 快閃地圖與 BIOS 區雜湊三態事實（WP4）。雜湊＝可讀面，RPE 攔截與全 F 頁如實標注。</summary>
    public IReadOnlyList<HardwareFact> SpiHashFacts { get; private set; } = [];

    /// <summary>
    /// SPI 快閃熵圖三態事實：逐 4 KiB 塊的 Shannon 熵、依 FREG 區域切分的內容組成。
    /// 只描述位元組分布（哪裡像壓縮／加密、哪裡是空白），<b>不判斷好壞或是否原廠</b>；
    /// PRx 讀保護攔截的範圍會被算成抹除區，事實文字如實標注「那是讀不到而非沒內容」。
    /// </summary>
    public IReadOnlyList<HardwareFact> SpiEntropyFacts { get; private set; } = [];

    /// <summary>
    /// BIOS 區 vs 參考映像的比對結果（使用者觸發的一次性動作，最多保留最近一次）。
    /// 驅動相依事實重載時清空——資料更新後舊比對失效，不留舊結論冒充現狀。
    /// </summary>
    public IReadOnlyList<HardwareFact> SpiCompareFacts { get; private set; } = [];

    /// <summary>收錄一次比對結果（替換上一次）；重載驅動相依事實會清空。</summary>
    public void AddSpiCompareFact(HardwareFact fact)
    {
        SpiCompareFacts = [fact];
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>以注入的 PCI + MMIO 讀取器載入 SPI 快閃安全事實；測試注入假讀取器，不在這裡觸發核心驅動安裝。</summary>
    public void LoadSpiFlash(IPciConfigReader pci, IMmioReader mmio)
    {
        SpiFlashFacts = SpiFlashService.Collect(pci, mmio, DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>PCIe AER 三態事實（ECAM 基底 + 逐裝置錯誤狀態）。歸類「PCIe」，進快照但不進韌體安全頁。</summary>
    public IReadOnlyList<HardwareFact> PcieAerFacts { get; private set; } = [];

    private DeepAccessService? _deepAccess;

    /// <summary>深層核心存取豁免開關（產生 CA、裝/移信任、載/卸 XsRegProbe）。延遲建立：不點不碰真實系統。</summary>
    public DeepAccessService DeepAccess => _deepAccess ??= new DeepAccessService();

    /// <summary>以注入的 MMIO 讀取器與 ACPI 來源載入 PCIe AER 事實；讀不到由 EcamAerService 標三態。</summary>
    public void LoadPcieAer(IMmioReader mmio, IAcpiTableSource acpi)
    {
        PcieAerFacts = EcamAerService.Collect(mmio, acpi, DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>MCHBAR 三態事實（基底＋暫存器可用性）。歸類「記憶體控制器」，進快照不進韌體安全頁。</summary>
    public IReadOnlyList<HardwareFact> MchbarFacts { get; private set; } = [];

    /// <summary>以注入的 PCI＋MMIO 讀取器載入 MCHBAR 事實；暫存器解讀刻意未實作（誠實界線見 MchbarService）。</summary>
    public void LoadMchbar(IPciConfigReader pci, IMmioReader mmio)
    {
        MchbarFacts = MchbarService.Collect(pci, mmio, DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>Platform 安全 MSR 三態事實（IA32_FEATURE_CONTROL／IA32_DEBUG_INTERFACE）。WinRing0 今天就讀得到。</summary>
    public IReadOnlyList<HardwareFact> PlatformSecurityFacts { get; private set; } = [];

    /// <summary>以注入的 MSR 讀取器載入平台安全事實；讀不到由 PlatformSecurityMsrService 標三態。</summary>
    public void LoadPlatformSecurity(IKernelMsrReader reader)
    {
        PlatformSecurityFacts = PlatformSecurityMsrService.Collect(reader, DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>後端與環境三態事實（誰在服務 MSR/MMIO、HVCI/Secure Boot/testsigning、環境矩陣裁決）。usermode 探測＋讀取器來源標示。</summary>
    public IReadOnlyList<HardwareFact> BackendFacts { get; private set; } = [];

    /// <summary>CPU 韌體身分三態事實（微碼修訂版雙來源＋TjMax）。供交叉對帳的輸入。</summary>
    public IReadOnlyList<HardwareFact> CpuFirmwareFacts { get; private set; } = [];

    /// <summary>交叉對帳結果（WP5 矛盾矩陣）：每條規則一列，一致／矛盾／無法驗證都如實成列。</summary>
    public IReadOnlyList<HardwareFact> ReconcileFacts { get; private set; } = [];

    /// <summary>交叉對帳判決卡（韌體安全頁頂部）：每條規則的判定徽章＋原因——判決要看得見，不埋在清單裡。</summary>
    public IReadOnlyList<ReconcileVerdictRow> ReconcileVerdicts { get; private set; } = [];

    /// <summary>判決摘要：一致／矛盾／無法驗證的計數。</summary>
    public string ReconcileSummary { get; private set; } = "尚未擷取。";

    /// <summary>I/O 埠唯讀事實（POST 代碼等，WP1／A38）。由注入的 IIoPortAccess 載入。</summary>
    public IReadOnlyList<HardwareFact> IoPortFacts { get; private set; } = [];

    /// <summary>CMOS/RTC 唯讀三態事實（VRT、RTC 時鐘、PC-AT 校驗和；WP6）。廠商設定區刻意不解碼。</summary>
    public IReadOnlyList<HardwareFact> CmosFacts { get; private set; } = [];

    /// <summary>SMBus 唯讀事實（TSOD 溫度感測器掃描；WP2）。空位址不列，逐顆三態。</summary>
    public IReadOnlyList<HardwareFact> SmbusFacts { get; private set; } = [];

    /// <summary>UEFI 開機設定三態事實（SecureBoot/SetupMode/AuditMode/DeployedMode/BootOrder；WP6）。Secure Boot 的第二個獨立來源。</summary>
    public IReadOnlyList<HardwareFact> UefiFacts { get; private set; } = [];

    /// <summary>Super I/O 探測三態事實（0x2E/0x4E 晶片 ID；WP31）。設定模式必以 finally 退出。</summary>
    public IReadOnlyList<HardwareFact> SuperIoFacts { get; private set; } = [];

    /// <summary>SuperIO HWM 感測器三態事實（WP17：風扇／溫度／電壓；驅動相依）。</summary>
    public IReadOnlyList<HardwareFact> HwmFacts { get; private set; } = [];

    /// <summary>PMU 能力與唯讀觀察三態事實（WP27 第一階段：編程路徑未實作）。</summary>
    public IReadOnlyList<HardwareFact> PmuFacts { get; private set; } = [];

    /// <summary>Uncore 頻率 MSR 事實（平台白名單制；與效能天花板頁同一對 MSR，這裡進快照／CLI／覆蓋申報）。</summary>
    public IReadOnlyList<HardwareFact> UncorePmuFacts { get; private set; } = [];

    /// <summary>記憶體加密（TME/SGX）與 C-state 駐留三態事實（處理器深化；驅動相依）。</summary>
    public IReadOnlyList<HardwareFact> MemoryEncryptionFacts { get; private set; } = [];

    /// <summary>AMD 安全事實（SME/SEV/SNP/PSP）；非 AMD 平台整組 NotApplicable（R5）。</summary>
    public IReadOnlyList<HardwareFact> AmdSecurityFacts { get; private set; } = [];

    /// <summary>PMBus 電源軌（R7）；無 PMBus 裝置時整組 NotApplicable。</summary>
    public IReadOnlyList<HardwareFact> PsuPmbusFacts { get; private set; } = [];

    /// <summary>UEFI 安全開機簽章資料庫（db/dbx/KEK/PK）與 Boot 條目（R2；需提權）。</summary>
    public IReadOnlyList<HardwareFact> UefiSignatureFacts { get; private set; } = [];

    public IReadOnlyList<HardwareFact> CStateFacts { get; private set; } = [];

    /// <summary>WP22 記憶體壓力探測的危險聲明（UI 紅字呈現；探測本身需明確同意才執行）。</summary>
    public string RowhammerDangerText => RowhammerProbeService.DangerNotice;

    /// <summary>WP27 PMU 編程驗證的多輪測試聲明（UI 紅字呈現）。</summary>
    public string PmuProgrammingNotice => PmuProgrammingService.FormatNotice;

    /// <summary>Bus 0 裝置盤點三態事實（WP30 知識層：PCI-SIG 類別碼→角色）。</summary>
    public IReadOnlyList<HardwareFact> PciInventoryFacts { get; private set; } = [];

    /// <summary>TPM 2.0 量測開機鏈三態事實（WP14：PCR 0–7 SHA-256＋TCG log 摘要）。usermode 經 Windows TBS。</summary>
    public IReadOnlyList<HardwareFact> TpmFacts { get; private set; } = [];

    /// <summary>平台拓撲與攻擊面聲明（NUMA 拓撲＋Rowhammer 未施測聲明；usermode）。</summary>
    public IReadOnlyList<HardwareFact> PlatformFacts { get; private set; } = [];

    /// <summary>系統與軟體層三態事實（WP15：Windows Update 歷史／服務／排程工作／事件記錄／安全政策；usermode）。</summary>
    public IReadOnlyList<HardwareFact> SoftwareFacts { get; private set; } = [];

    /// <summary>載入系統與軟體層事實（usermode；讀不到由各服務標三態）。COM/WMI 查詢可能數秒，呼叫端自行放背景。</summary>
    public void LoadSoftwareFacts()
    {
        var at = DateTimeOffset.UtcNow;
        SoftwareFacts = WindowsUpdateHistoryService.Collect(at)
            .Concat(ServiceInventoryService.Collect(at))
            .Concat(EventLogSummaryService.Collect(at))
            .Concat(AuditPolicyService.Collect(at))
            .Concat(OptionalFeatureService.Collect(at))
            .Concat(KernelModuleService.Collect(at))
            .Concat(ByovdCompareService.Collect(at, null, () => KernelModuleService.FetchLoadedModules() ?? []))
            .Concat(SecurityAuditFactsService.CollectDefenderExclusions(at))
            .Concat(SecurityAuditFactsService.CollectLogClearEvents(at))
            .Concat(SecurityAuditFactsService.CollectForeignRootCerts(at))
            .Concat(SecurityAuditFactsService.CollectUsbstor(at))
            .Concat(NicHealthFactsService.Collect(at))
            .Concat(NicHealthFactsService.CollectMacVendors(at))
            .Concat(SmartFailingNowFactsService.Collect(at))
            .Concat(ChassisFactsService.Collect(at))
            .Concat(HpaFactsService.Collect(at))
            .Concat(GpuTdrFactsService.Collect(at))
            .Concat(LevelZeroFactsService.Collect(at))
            .Concat(AmdAdlFactsService.Collect(at))
            .Concat(UpsFactsService.Collect(at))
            .Concat(DebugConfigService.Collect(at))
            .ToList();
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>
    /// 虛擬化平台三態事實：把「元件裝了」「服務在跑」「虛擬層載入了」分開陳列。
    /// 一般工具把這三件事壓成一句「Hyper-V：已啟用／停用」，於是「裝了但沒開」
    /// （不會有 VM 能跑，但服務與攻擊面開著）永遠被講錯。
    /// </summary>
    public IReadOnlyList<HardwareFact> VirtualizationFacts { get; private set; } = [];

    /// <summary>
    /// 顯示器組成事實（真實螢幕 vs 軟體虛擬螢幕 vs 作業系統預設物件）。
    /// 與 <see cref="DisplayAdapterFactsService"/>（轉接器）分開：這裡看的是「接了什麼螢幕」。
    /// </summary>
    public IReadOnlyList<HardwareFact> MonitorFacts { get; private set; } = [];

    /// <summary>以注入的樣本載入顯示器事實；測試注入假清單。</summary>
    public void LoadMonitors(Func<IReadOnlyList<MonitorSample>>? probe = null, DateTimeOffset? at = null)
    {
        MonitorFacts = MonitorFactsService.Collect(at ?? DateTimeOffset.UtcNow, probe);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
        OnPropertyChanged(nameof(MonitorSummary));
        OnPropertyChanged(nameof(AssetHeadline));
        OnPropertyChanged(nameof(AssetEvidence));
        OnPropertyChanged(nameof(RoleHeadline));
        OnPropertyChanged(nameof(RoleEvidence));
    }

    /// <summary>識別與資產事實（SMBIOS 識別欄位＋韌體未填標注）。</summary>
    public IReadOnlyList<HardwareFact> AssetFacts { get; private set; } = [];

    /// <summary>已安裝角色與功能事實。</summary>
    public IReadOnlyList<HardwareFact> RoleFacts { get; private set; } = [];

    /// <summary>識別與資產卡片的一行結論。</summary>
    public string AssetHeadline =>
        AssetFacts.FirstOrDefault(f => f.Key == "asset.identify")?.Value ?? "尚未讀取。";

    /// <summary>識別與資產卡片的依據列。</summary>
    public string AssetEvidence =>
        AssetFacts.FirstOrDefault(f => f.Key == "asset.identify.evidence")?.Value ?? "";

    /// <summary>已安裝角色卡片的一行結論。</summary>
    public string RoleHeadline =>
        RoleFacts.FirstOrDefault(f => f.Key == "role.surface")?.Value ?? "尚未讀取。";

    /// <summary>已安裝角色卡片的依據列。</summary>
    public string RoleEvidence =>
        RoleFacts.FirstOrDefault(f => f.Key == "role.surface.evidence")?.Value ?? "";

    /// <summary>顯示器組成的一行摘要。</summary>
    public string MonitorSummary =>
        MonitorFacts.FirstOrDefault(f => f.Key == "monitor.summary")?.Value ?? "尚未讀取。";

    /// <summary>以注入的探測載入虛擬化事實；測試注入假狀態。</summary>
    public void LoadVirtualization(Func<VirtualizationState>? probe = null, DateTimeOffset? at = null)
    {
        VirtualizationFacts = VirtualizationFactsService.Collect(at ?? DateTimeOffset.UtcNow, probe);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
        OnPropertyChanged(nameof(VirtualizationHeadline));
        OnPropertyChanged(nameof(VirtualizationEvidence));
    }

    /// <summary>虛擬化卡片的一行結論；尚未載入時如實說尚未讀取。</summary>
    public string VirtualizationHeadline =>
        VirtualizationFacts.FirstOrDefault(f => f.Key == "virt.judge")?.Value ?? "尚未讀取。";

    /// <summary>虛擬化卡片的依據列：哪些欄位讀到什麼值。</summary>
    public string VirtualizationEvidence =>
        VirtualizationFacts.FirstOrDefault(f => f.Key == "virt.msr") is { } msr
            ? $"MSR／TSC 讀值：{msr.Value}。來源：CPUID leaf 1 ECX bit 31、選用功能與服務狀態，全部唯讀。"
            : "";

    /// <summary>載入平台拓撲與攻擊面事實（usermode；讀不到由各服務標三態）。</summary>
    public void LoadPlatformFacts()
    {
        var at = DateTimeOffset.UtcNow;
        MonitorFacts = MonitorFactsService.Collect(at);
        AssetFacts = AssetFactsService.Collect(at);
        RoleFacts = RoleSurfaceFactsService.Collect(at);
        PlatformFacts = NumaTopologyService.Collect(at)
            .Append(MemoryAttackSurfaceService.Collect(at))
            .Concat(OobFactsService.Collect(at))
            .Concat(VirtualizationFactsService.Collect(at))
            .Concat(NicLinkFactsService.Collect(at))
            .Concat(DisplayAdapterFactsService.Collect(at))
            .Concat(MonitorFacts)
            .Concat(AudioLatencyFactsService.Collect(at))
            .Concat(AssetFacts)
            .Concat(RoleFacts)
            .Concat(TimeSyncFactsService.Collect(at, new Win32AcpiTableSource()))
            .Concat(CxlFactsService.Collect(at, new Win32AcpiTableSource()))
            .Concat(UsbTopologyService.Collect(at))
            .Concat(MonitorConnectionService.Collect(at))
            .Concat(CameraFactsService.Collect(at))
            .Concat(EnterpriseStorageFactsService.Collect(at))
            .Concat(TopologyDeepenFactsService.CollectCpuTopology(at))
            .Concat(TopologyDeepenFactsService.CollectNumaDistance(at, new Win32AcpiTableSource()))
            .ToList();
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>原始暫存器區（P4）：重載驅動相依事實時一併收集，讀不到的區三態。存檔是使用者主動行為（raw 不匿名化）。</summary>
    public IReadOnlyList<RawRegisterRegion> RawRegions { get; private set; } = [];

    public ObservableCollection<EvidenceRawChangeRow> RawChanges { get; } = [];

    private string _rawStatus = "尚未擷取。原始快照收錄 PCI 安全暫存器、平台安全 MSR、ACPI 表整表、SPIBAR 與 MCHBAR 的原始位元組；隨驅動相依事實一起收集。";
    public string RawStatus { get => _rawStatus; private set => SetProperty(ref _rawStatus, value); }

    private string _rawSummary = "—";
    public string RawSummary { get => _rawSummary; private set => SetProperty(ref _rawSummary, value); }

    /// <summary>
    /// 事實重載（深層存取啟用後免重啟翻真值）：五組驅動相依事實整批「替換」——每組各自重新 Collect 後整組指派，
    /// 不附加不累積；ACPI 表清單不在內（usermode 來源、另由 LoadAcpi 管理）。啟用深層存取後以新鮮的驅動後端
    /// 呼叫即可把三態翻成真值；停用後以不可用後端呼叫則如實回到三態，不留舊值冒充。
    /// </summary>
    public void ReloadDriverBackedFacts(IPciConfigReader pci, IKernelMsrReader msr, IMmioReader mmio,
        IAcpiTableSource acpi, IIoPortAccess io, ISmbusIo? smbusIo = null)
    {
        var at = DateTimeOffset.UtcNow;
        ChipsetFacts = ChipsetSecurityService.Collect(pci, at);
        PlatformSecurityFacts = PlatformSecurityMsrService.Collect(msr, at);
        SpiFlashFacts = SpiFlashService.Collect(pci, mmio, at);
        SpiHashFacts = SpiFlashHashService.Collect(pci, mmio, at);
        SpiEntropyFacts = SpiEntropyService.Collect(pci, mmio, at);
        SpiEntropyRegionsCache = SpiEntropyService.DescribeRegions(pci, mmio);
        UefiFvFacts = UefiFvFactsService.Collect(pci, mmio, at);
        UefiFvRegionsCache = UefiFvFactsService.DescribeRows(pci, mmio);
        SpiCompareFacts = []; // 資料更新後舊比對失效，如實清空
        MchbarFacts = MchbarService.Collect(pci, mmio, at);
        PcieAerFacts = EcamAerService.Collect(mmio, acpi, at);
        BackendFacts = BackendEnvironmentService.Collect(msr, mmio, at);
        CpuFirmwareFacts = CpuFirmwareFactsService.Collect(msr, at);
        PmuFacts = PmuCapabilityFactsService.Collect(at, msr: msr);
        UncorePmuFacts = UncorePmuService.Collect(msr, at);
        MemoryEncryptionFacts = MemoryEncryptionFactsService.Collect(at, msr: msr);
        AmdSecurityFacts = AmdSecurityFactsService.Collect(at, msr: msr, pci: pci);
        PsuPmbusFacts = PsuPmbusFactsService.CollectWithLock(smbusIo, pci.ReadDword, at);
        CStateFacts = CStateResidencyFactsService.Collect(at, msr);
        IoPortFacts = IoPortFactsService.Collect(io, at);
        CmosFacts = CmosService.Collect(io, at);
        SmbusFacts = TsodSurveyor.CollectWithLock(smbusIo, pci.ReadDword, at);
        UefiFacts = UefiBootFactsService.Collect(at);
        UefiSignatureFacts = UefiSignatureFactsService.Collect(at);
        SuperIoFacts = SuperIoProbeService.Collect(io, at);
        HwmFacts = SuperIoHwmFactsService.Collect(io, at);
        PciInventoryFacts = Bus0InventoryService.Collect(pci, at);
        RawRegions = RawRegisterCollectService.Collect(pci, acpi, msr, mmio, at);
        RawSummary = $"{RawRegions.Count} 區原始位元組・" +
                     $"{RawRegions.Count(r => r.Availability == FactAvailability.Present)} 區可讀・" +
                     $"{RawRegions.Count(r => r.Availability != FactAvailability.Present)} 區三態";
        RawStatus = "原始快照已隨本次擷取更新；按「建立原始快照」存檔，或「與目前差分」比對舊檔。";
        ReconcileFacts = EvaluateReconciliation(at);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>把目前的原始暫存器區存成帶 SHA-256 完整性信封的檔案。raw 不匿名化，分享前請自行確認。</summary>
    public async Task SaveRawSnapshotAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var snapshot = RawRegisterCollectService.Create(AppInfo.Version, RawRegions, DateTimeOffset.UtcNow);
            await Task.Run(() => RawRegisterSnapshotStore.Save(path, snapshot));
            RawSummary = $"{snapshot.Regions.Count} 區原始位元組 ・ SHA-256 完整性信封";
            RawStatus = "原始快照已儲存。內容為原始位元組，不做匿名化——檔案可能含 OEM 原始材料，公開分享前請自行確認。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or NotSupportedException or ArgumentException)
        {
            RawSummary = "儲存失敗";
            RawStatus = ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>載入原始快照檔：驗完整性信封後只陳述內容，不冒充是目前狀態。</summary>
    public async Task InspectRawSnapshotAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var snapshot = await Task.Run(() => RawRegisterSnapshotStore.Load(path));
            RawChanges.Clear();
            RawSummary = $"{snapshot.Regions.Count} 區原始位元組 ・ {snapshot.TakenAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            RawStatus = "完整性驗證通過。這是檔案內保存的舊原始位元組，不是目前硬體狀態。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or NotSupportedException or ArgumentException
                                   or System.Text.Json.JsonException)
        {
            RawChanges.Clear();
            RawSummary = "載入失敗";
            RawStatus = ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>載入舊原始快照並與目前區做逐位元組差分（套揮發遮罩）；兩台機器／無現有區時如實拒比。</summary>
    public async Task CompareRawAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var old = await Task.Run(() => RawRegisterSnapshotStore.Load(path));
            RawChanges.Clear();
            if (RawRegions.Count == 0)
            {
                RawSummary = "尚無目前可比的原始區";
                RawStatus = "還沒收集過原始區（等驅動相依事實擷取完成），無從差分。";
                return;
            }
            var diff = RawRegisterSnapshotService.Diff(old.Regions, RawRegions);
            int meaningful = 0;
            foreach (var change in diff.Regions.Where(c => c.Kind != RawRegionChangeKind.Unchanged))
            {
                RawChanges.Add(EvidenceRawChangeRow.From(change));
                meaningful++;
            }
            RawSummary = $"變更 {diff.Regions.Count(c => c.Kind == RawRegionChangeKind.Changed)} ・ " +
                         $"狀態改變 {diff.Regions.Count(c => c.Kind == RawRegionChangeKind.AvailabilityChanged)} ・ " +
                         $"新增 {diff.Regions.Count(c => c.Kind == RawRegionChangeKind.Added)} ・ " +
                         $"消失 {diff.Regions.Count(c => c.Kind == RawRegionChangeKind.Removed)}";
            RawStatus = meaningful == 0
                ? "沒有發現差異（揮發位元組已依遮罩略過）。"
                : "發現差異。逐位元組差異本身不等於故障；遮罩位元組（SMI 計數、TSC 等）已略過。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or NotSupportedException or ArgumentException
                                   or System.Text.Json.JsonException)
        {
            RawChanges.Clear();
            RawSummary = "差分失敗";
            RawStatus = ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>把全部事實組交給對帳引擎逐規則評估；每條規則一列（一致／矛盾／無法驗證都是 Present 的「結論事實」）。</summary>
    private IReadOnlyList<HardwareFact> EvaluateReconciliation(DateTimeOffset at)
    {
        var all = ChipsetFacts.Concat(SpiFlashFacts).Concat(PlatformSecurityFacts).Concat(CpuFirmwareFacts).Concat(PmuFacts).Concat(UncorePmuFacts).Concat(MemoryEncryptionFacts).Concat(AmdSecurityFacts).Concat(CStateFacts)
            .Concat(BackendFacts).Concat(MchbarFacts).Concat(PcieAerFacts).Concat(AcpiFacts).ToList();
        var rows = FactRelationService.Evaluate(FactRelationRules.All, all);

        // 判決卡資料（A45/A47 之後最重要的使用者面價值：說得出的判決要看得見，不埋在清單裡）
        ReconcileVerdicts = rows.Select(r => new ReconcileVerdictRow(
            r.RuleName, r.Relation,
            r.Relation switch { FactRelation.Consistent => "一致", FactRelation.Contradicts => "矛盾", _ => "無法驗證" },
            r.Reason,
            r.Relation switch
            {
                FactRelation.Consistent => Severity.Good,
                FactRelation.Contradicts => Severity.Critical,
                _ => Severity.Neutral,
            })).ToList();
        OnPropertyChanged(nameof(ReconcileVerdicts));

        int consistent = rows.Count(r => r.Relation == FactRelation.Consistent);
        int contradicts = rows.Count(r => r.Relation == FactRelation.Contradicts);
        ReconcileSummary = $"一致 {consistent} ・ 矛盾 {contradicts} ・ 無法驗證 {rows.Count - consistent - contradicts}（共 {rows.Count} 條）";
        OnPropertyChanged(nameof(ReconcileSummary));

        return rows
            .Select(r =>
            {
                string verdict = r.Relation switch
                {
                    FactRelation.Consistent => "一致",
                    FactRelation.Contradicts => "矛盾",
                    _ => "無法驗證",
                };
                return new HardwareFact($"reconcile.{r.RuleId}", "交叉對帳", r.RuleName, $"{verdict}：{r.Reason}", "",
                    $"對帳規則 {r.RuleId}", FactTrustLevel.Derived, false, at);
            })
            .ToList();
    }

    /// <summary>熵圖卡片的摘要列（右側小字）：全區平均熵與各類區塊數；讀不到時如實顯示原因。</summary>
    public string SpiEntropySummary
    {
        get
        {
            var f = SpiEntropyFacts.FirstOrDefault(x => x.Key == SpiEntropyService.FactKey);
            if (f is null) return "—";
            if (f.Availability != FactAvailability.Present) return "不可得";
            return f.NumericValue is double v ? $"平均 {v:0.00} bits/byte" : "—";
        }
    }

    /// <summary>熵圖卡片的區域列（逐一列出有配置的 FREG 區域）；無資料時回單列說明原因，不給空清單假裝成功。</summary>
    public IReadOnlyList<EntropyRegionRow> SpiEntropyRegions
    {
        get
        {
            var f = SpiEntropyFacts.FirstOrDefault(x => x.Key == SpiEntropyService.FactKey);
            if (f is null)
                return [new EntropyRegionRow("—", "尚未擷取", "")];
            if (f.Availability != FactAvailability.Present)
                return [new EntropyRegionRow("—", f.UnavailableReason ?? "讀不到，原因未提供", "")];
            if (SpiEntropyRegionsCache.Count > 0) return SpiEntropyRegionsCache;
            return [new EntropyRegionRow("—", f.Value, "")];
        }
    }

    /// <summary>最近一次熵分析的分區明細（服務層分析後填入；無資料時為空清單）。</summary>
    public IReadOnlyList<EntropyRegionRow> SpiEntropyRegionsCache { get; private set; } = [];

    /// <summary>UEFI FV 結構的事實（總數＋逐 FV 摘要）；驅動後端可用時填入。</summary>
    public IReadOnlyList<HardwareFact> UefiFvFacts { get; private set; } = [];

    /// <summary>驅動檔靜態檢視的事實（LoadUsermodeFacts 接線；不載入驅動、不呼叫 IOCTL）。</summary>
    public IReadOnlyList<HardwareFact> DriverInspectionFacts { get; private set; } = [];

    /// <summary>裝置安裝記錄時間線的事實（LoadUsermodeFacts 接線；唯讀解析 setupapi.dev.log）。</summary>
    public IReadOnlyList<HardwareFact> SetupTimelineFacts { get; private set; } = [];

    /// <summary>.etl 內容讀回的事實（LoadUsermodeFacts 接線；TraceEvent 檔案模式唯讀）。</summary>
    public IReadOnlyList<HardwareFact> EtlReadbackFacts { get; private set; } = [];

    /// <summary>ESP 檔案層掃描的事實（LoadUsermodeFacts 接線；唯讀＋dbx 交叉引用）。</summary>
    public IReadOnlyList<HardwareFact> EspScanFacts { get; private set; } = [];

    /// <summary>音訊端點混合格式的事實（LoadUsermodeFacts 接線；MMDevice API 唯讀）。</summary>
    public IReadOnlyList<HardwareFact> AudioEndpointFacts { get; private set; } = [];

    /// <summary>開機計時的事實（LoadUsermodeFacts 接線；事件記錄＋WMI 唯讀，不評級）。</summary>
    public IReadOnlyList<HardwareFact> BootTimingFacts { get; private set; } = [];

    /// <summary>網路卸載狀態的事實（LoadUsermodeFacts 接線；WMI root\StandardCimv2 唯讀）。</summary>
    public IReadOnlyList<HardwareFact> NetOffloadFacts { get; private set; } = [];

    /// <summary>LoadUsermodeFacts 的驅動檢視段：列舉非系統目錄驅動、逐顆讀檔做靜態解析。</summary>
    public void LoadDriverInspection()
    {
        DriverInspectionFacts = DriverInspectionFactsService.Collect(
            DateTimeOffset.UtcNow,
            KernelModuleService.FetchLoadedModules(),
            path => { try { return File.ReadAllBytes(path); } catch { return null; } });
    }

    /// <summary>LoadUsermodeFacts 的裝置安裝時間線段：唯讀解析 setupapi.dev.log。</summary>
    public void LoadSetupTimeline()
    {
        SetupTimelineFacts = SetupApiTimelineService.Collect(DateTimeOffset.UtcNow);
    }

    /// <summary>LoadUsermodeFacts 的 .etl 讀回段：把本專案落地的軌跡讀回內容統計。</summary>
    public void LoadEtlReadback()
    {
        EtlReadbackFacts = EtlReadbackService.Collect(DateTimeOffset.UtcNow);
    }

    /// <summary>LoadUsermodeFacts 的 ESP 掃描段：列舉 .efi 檔並與 dbx 交叉引用。</summary>
    public void LoadEspScan()
    {
        EspScanFacts = EspScanService.Collect(DateTimeOffset.UtcNow);
    }

    /// <summary>LoadUsermodeFacts 的音訊端點段：列舉作用中端點並報引擎混合格式。</summary>
    public void LoadAudioEndpoints()
    {
        AudioEndpointFacts = AudioEndpointFactsService.Collect(DateTimeOffset.UtcNow);
    }

    /// <summary>LoadUsermodeFacts 的開機計時段：讀事件記錄的開機耗時與最近開機時間。</summary>
    public void LoadBootTiming()
    {
        BootTimingFacts = BootTimingFactsService.Collect(DateTimeOffset.UtcNow);
    }

    /// <summary>LoadUsermodeFacts 的網路卸載段：讀 Windows 實際啟用的卸載狀態。</summary>
    public void LoadNetOffload()
    {
        NetOffloadFacts = NetOffloadFactsService.Collect(DateTimeOffset.UtcNow);
    }

    /// <summary>本地安全審計事實（v2.56，SA 組：IFEO／Winlogon／AppInit／輔助功能／代理）。唯讀登錄檔。</summary>
    public IReadOnlyList<HardwareFact> LocalSecurityAuditFacts { get; private set; } = [];

    /// <summary>LoadUsermodeFacts 的本地安全審計段：五個收集器串接；命中＝風險面非判決。</summary>
    public void LoadLocalSecurityAudit()
    {
        var at = DateTimeOffset.UtcNow;
        LocalSecurityAuditFacts =
        [
            .. LocalSecurityAuditService.CollectIfeo(at),
            .. LocalSecurityAuditService.CollectWinlogon(at),
            .. LocalSecurityAuditService.CollectAppInit(at),
            .. LocalSecurityAuditService.CollectAccessibility(at),
            .. LocalSecurityAuditService.CollectProxy(at),
            .. LocalSecurityAuditService.CollectExposure(at),
            .. LocalSecurityAuditService.CollectWinsockLsp(at),
        ];
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    // ── Vol 2 批次（2026-10-11）：自我完整性／基線學習／CVE 對照／伺服器摘要／情境診斷／SBOM ──

    /// <summary>自我完整性事實（IN-001/002/004/007/010）：四項唯讀；基線記錄需使用者明示（CLI --integrity-baseline）。</summary>
    public IReadOnlyList<HardwareFact> SelfIntegrityFacts { get; private set; } = [];

    public void LoadSelfIntegrity()
    {
        SelfIntegrityFacts = SelfIntegrityFactsService.Collect(DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>基線學習事實（BL-001/004/007）：吃歷史倉的序列；沒有歷史就由服務標「還沒有資料」，不給空集合。</summary>
    public IReadOnlyList<HardwareFact> BaselineFacts { get; private set; } = [];

    public void LoadBaselineLearning(HistorySeries? series = null)
    {
        BaselineFacts = BaselineLearningService.Collect(DateTimeOffset.UtcNow, series ?? HistorySeries.Empty);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>作業系統 CVE 離線對照（SE-002）：本版沒出貨條目，如實說「還沒有資料」。</summary>
    public IReadOnlyList<HardwareFact> CveFacts { get; private set; } = [];

    public void LoadCveOffline()
    {
        CveFacts = CveOfflineFactsService.CollectForThisMachine(DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>伺服器合規摘要（SV-014）：由既有角色事實聚合——裝了不等於配置好，只陳述計數。</summary>
    public IReadOnlyList<HardwareFact> ServerComplianceFacts { get; private set; } = [];

    public void LoadServerCompliance()
    {
        ServerComplianceFacts =
        [
            ServerComplianceFactsService.Collect(DateTimeOffset.UtcNow, RoleFacts, Environment.OSVersion.VersionString),
        ];
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>磁碟為什麼滿（SG-002）：唯讀排行、不刪任何東西；預設只掃暫存與傾印根。</summary>
    public IReadOnlyList<HardwareFact> DiskFullFacts { get; private set; } = [];

    public void LoadDiskFull(DiskFullFactsService.ScanRequest? request = null)
    {
        DiskFullFacts = DiskFullFactsService.Collect(DateTimeOffset.UtcNow,
            request ?? DiskFullFactsService.DefaultRequest());
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>電腦為什麼當（SG-011）：四類事件的事實面；觀察不是診斷。</summary>
    public IReadOnlyList<HardwareFact> FreezeFacts { get; private set; } = [];

    public void LoadFreezeDiagnosis()
    {
        FreezeFacts = FreezeDiagnosisFactsService.Collect(DateTimeOffset.UtcNow);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>軟體物料清單（RS-002）：元件限本機可列舉的驅動＋作業系統＋本程式；只產生內容不寫檔。</summary>
    public IReadOnlyList<HardwareFact> SbomFacts { get; private set; } = [];

    public void LoadSbom()
    {
        SbomFacts = SbomService.Collect(DateTimeOffset.UtcNow, AppInfo.Name, AppInfo.Version);
        OnPropertyChanged(nameof(FirmwareSecurityRows));
    }

    /// <summary>UEFI FV 卡片的摘要列（右側小字）：FV 總數；讀不到時如實顯示原因。</summary>
    public string UefiFvSummary
    {
        get
        {
            var f = UefiFvFacts.FirstOrDefault(x => x.Key == UefiFvFactsService.CountKey);
            if (f is null) return "—";
            if (f.Availability != FactAvailability.Present) return "不可得";
            return f.NumericValue is double v ? $"{(int)v} 個 FV" : "—";
        }
    }

    /// <summary>UEFI FV 卡片的逐 FV 列；無資料時回單列說明原因，不給空清單假裝成功。</summary>
    public IReadOnlyList<UefiFvRow> UefiFvRegions
    {
        get
        {
            var f = UefiFvFacts.FirstOrDefault(x => x.Key == UefiFvFactsService.CountKey);
            if (f is null)
                return [new UefiFvRow("—", "尚未擷取", "")];
            if (f.Availability != FactAvailability.Present)
                return [new UefiFvRow("—", f.UnavailableReason ?? "讀不到，原因未提供", "")];
            if (UefiFvRegionsCache.Count > 0) return UefiFvRegionsCache;
            return [new UefiFvRow("—", f.Value, "")];
        }
    }

    /// <summary>最近一次 FV 解析的逐 FV 明細（服務層分析後填入；無資料時為空清單）。</summary>
    public IReadOnlyList<UefiFvRow> UefiFvRegionsCache { get; private set; } = [];

    /// <summary>
    /// 全部事實組合併成單一清單（CLI 與報告用）。與 FirmwareSecurityRows 同集合、不轉渲染列。
    /// 尾端追加能力矩陣彙總（cap.*）與鍵範圍檢查（qs.range）：彙總輸入是前面各組的聯集、
    /// 不含彙總自己——無遞迴。
    /// </summary>
    public IReadOnlyList<HardwareFact> AllFacts
    {
        get
        {
            var baseFacts = ChipsetFacts.Concat(SpiFlashFacts).Concat(SpiHashFacts).Concat(SpiEntropyFacts).Concat(SpiCompareFacts).Concat(PlatformSecurityFacts).Concat(BackendFacts)
                .Concat(CpuFirmwareFacts).Concat(PmuFacts).Concat(UncorePmuFacts).Concat(MemoryEncryptionFacts).Concat(AmdSecurityFacts).Concat(PsuPmbusFacts).Concat(CStateFacts).Concat(ReconcileFacts).Concat(IoPortFacts).Concat(CmosFacts)
                .Concat(SmbusFacts).Concat(UefiFacts).Concat(UefiSignatureFacts).Concat(SuperIoFacts).Concat(HwmFacts).Concat(PciInventoryFacts).Concat(TpmFacts)
                .Concat(PlatformFacts).Concat(VirtualizationFacts).Concat(SoftwareFacts).Concat(AcpiFacts).Concat(StorageReliabilityFacts).Concat(UefiFvFacts).Concat(DriverInspectionFacts).Concat(SetupTimelineFacts).Concat(EtlReadbackFacts).Concat(EspScanFacts).Concat(AudioEndpointFacts).Concat(BootTimingFacts).Concat(NetOffloadFacts).Concat(LocalSecurityAuditFacts)
                .Concat(SelfIntegrityFacts).Concat(BaselineFacts).Concat(CveFacts).Concat(ServerComplianceFacts).Concat(DiskFullFacts).Concat(FreezeFacts).Concat(SbomFacts)
                .ToList();
            baseFacts.AddRange(CapabilityMatrixService.Collect(baseFacts, DateTimeOffset.UtcNow));
            // 鍵範圍檢查（QS-001）與 cap.* 同款：輸入是前面各組的聯集、不含自己——無遞迴
            baseFacts.Add(KeyRangeGuardService.Collect(DateTimeOffset.UtcNow, baseFacts));
            return baseFacts;
        }
    }

    /// <summary>
    /// 韌體安全頁用：把 <see cref="AllFacts"/> 依分類與鍵排序後轉成誠實渲染的列。
    /// </summary>
    /// <remarks>
    /// <b>刻意由 <see cref="AllFacts"/> 派生，而不是自己再串一次。</b>
    /// 這兩個屬性原本各寫了一份一模一樣的 27 個 <c>Concat</c>——只差最後有沒有
    /// <c>Select(EvidenceFactRow.From)</c>。那種重複不會報錯，只會在某天有人把新的
    /// 事實組加進其中一個而忘了另一個時，讓「頁面上看得到的」與「匯出／CLI 拿到的」
    /// 變成兩份不同的集合，而畫面上一切正常。
    /// 現在事實組的清單只有一份（<see cref="AllFacts"/>），要加新的組只改那一處。
    /// </remarks>
    public IReadOnlyList<EvidenceFactRow> FirmwareSecurityRows =>
        AllFacts.OrderBy(f => f.Category, StringComparer.Ordinal)
                .ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(EvidenceFactRow.From).ToList();

    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRun)); } }
    public bool CanRun => !_busy;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public HardwareSnapshot Capture(MainViewModel vm, bool includeSensitive)
    {
        var facts = Collect(vm);
        return HardwareSnapshotService.Create(AppInfo.Version, facts, includeSensitive);
    }

    public async Task SaveAsync(MainViewModel vm, string path, bool includeSensitive)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "正在擷取各來源目前已知的事實…";
        try
        {
            var snapshot = Capture(vm, includeSensitive);
            var policy = includeSensitive ? SensitiveValuePolicy.Preserve : SensitiveValuePolicy.Redact;
            await HardwareSnapshotService.SaveAsync(path, snapshot,
                new HardwareSnapshotSaveOptions { SensitiveValues = policy });
            ShowFacts(snapshot);
            Changes.Clear();
            Summary = $"{snapshot.Facts.Count} 項事實 ・ {(includeSensitive ? "保留敏感識別" : "敏感值已遮蔽")} ・ SHA-256 完整性封套";
            Status = "時間膠囊已儲存。雜湊只能偵測檔案是否被改動，不是數位簽章。";
            AppendAudit("建立時間膠囊", "時間膠囊", Summary, snapshot.Integrity.Hash, snapshot.AnonymousMachineId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or NotSupportedException or ArgumentException)
        {
            Changes.Clear();
            Summary = "儲存失敗";
            Status = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private static string KindText(AssetEventKind kind) => kind switch
    {
        AssetEventKind.Added => "新增",
        AssetEventKind.Removed => "移除",
        _ => "變更",
    };

    public async Task CompareAsync(MainViewModel vm, string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "正在驗證快照完整性並逐欄比較…";
        try
        {
            var old = await HardwareSnapshotService.LoadAsync(path);
            var current = Capture(vm, old.SensitiveValuesPreserved);
            var diff = HardwareSnapshotService.Diff(old, current);
            ShowFacts(current);
            Changes.Clear();
            if (!diff.IsSameMachine)
            {
                Summary = "不同機器，未執行差異比較";
                Status = "時間膠囊的匿名機器識別與目前電腦不同。為避免把兩台電腦的差異誤認成硬體變更，本次比較已停止。";
                return;
            }
            foreach (var change in diff.Changes.Where(x => x.Kind != SnapshotChangeKind.Unchanged))
                Changes.Add(EvidenceChangeRow.From(change));
            Summary = $"變更 {diff.Changed} ・ 新增 {diff.Added} ・ 消失 {diff.Removed} ・ 未變 {diff.Unchanged}";
            string assetNote = "";
            try
            {
                // WP42 資產生命週期：從差分自動分類資產事件（記憶體／處理器／顯示卡／儲存／主機板），
                // 摘要併入通知文字——分類失敗不影響比較主流程。
                var events = AssetChangeDetector.Detect(old, current);
                var assets = events.Where(e => e.AssetClass != "狀態").ToList();
                if (assets.Count > 0)
                    assetNote = $"其中資產事件 {assets.Count} 件：" +
                                string.Join("、", assets.Take(3).Select(e => $"{e.AssetClass}{KindText(e.Kind)}")) +
                                (assets.Count > 3 ? " 等" : "。");
            }
            catch { /* 資產分類為附加功能，失敗由 assetNote 留空呈現 */ }
            Status = Changes.Count == 0
                ? "沒有發現差異。比較的是已擷取事實；某來源這次讀不到時會明確列為消失，不以舊值填補。"
                : $"發現 {Changes.Count} 項差異。{assetNote}請依來源與可信度逐項判讀；差異本身不等於故障。";
            AppendAudit("比較時間膠囊", "時間膠囊", Summary, current.Integrity.Hash, current.AnonymousMachineId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or NotSupportedException or ArgumentException)
        {
            Changes.Clear();
            Summary = "比較失敗";
            Status = ex.Message;
        }
        finally { IsBusy = false; }
    }

    public async Task InspectAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "正在驗證並載入時間膠囊…";
        try
        {
            var snapshot = await HardwareSnapshotService.LoadAsync(path);
            ShowFacts(snapshot);
            Changes.Clear();
            Summary = $"{snapshot.Facts.Count} 項事實 ・ {snapshot.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} ・ 結構 v{snapshot.SchemaVersion}";
            Status = "完整性驗證通過。這是檔案內保存的舊讀值，不是目前硬體狀態。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or NotSupportedException or ArgumentException)
        {
            Facts.Clear();
            Changes.Clear();
            Summary = "載入失敗";
            Status = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private void ShowFacts(HardwareSnapshot snapshot)
    {
        Facts.Clear();
        foreach (var fact in snapshot.Facts.OrderBy(x => x.Category).ThenBy(x => x.Key))
            Facts.Add(EvidenceFactRow.From(fact));
    }

    /// <summary>
    /// 審計日誌追加（V7 WP36／A47）：誰、何時、對哪台（匿名雜湊）、做了什麼、結果雜湊。
    /// 只記中繼資料不記內容；寫入失敗不影響主流程（審計為附加，不打斷驗機）。
    /// </summary>
    private static void AppendAudit(string action, string scope, string summary, string? resultHash, string machineId)
    {
        try
        {
            var path = AuditLogService.DefaultPath;
            var log = AuditLogService.Load(path);
            log.Add(AuditLogService.Append(log, AuditLogService.CurrentOperator(), machineId,
                action, scope, summary, resultHash ?? "", DateTimeOffset.UtcNow));   // 無結果雜湊＝空字串，Append 的表欄位不存 null
            AuditLogService.Save(path, log);
        }
        catch { /* 審計為附加功能；本機磁碟不可寫等情況不影響驗機主流程 */ }
    }

    private static List<HardwareFact> Collect(MainViewModel vm)
    {
        var at = DateTimeOffset.UtcNow;
        var f = new List<HardwareFact>();
        void Add(string key, string category, string name, string? value, string unit, string source,
                 FactTrustLevel trust = FactTrustLevel.Reported, bool sensitive = false)
        {
            string v = string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
            if (v == "—") return;
            f.Add(new HardwareFact(key, category, name, v, unit, source, trust, sensitive, at));
        }

        Add("system.manufacturer", "系統", "整機製造商", vm.System.SystemManufacturer, "", "SMBIOS/WMI");
        Add("system.model", "系統", "整機型號", vm.System.SystemModel, "", "SMBIOS/WMI");
        Add("system.sku", "系統", "整機 SKU", vm.System.SystemSku, "", "SMBIOS/WMI");
        Add("system.uuid", "系統", "系統 UUID", vm.System.SystemUuid, "", "SMBIOS/WMI", sensitive: true);
        Add("board.vendor", "主機板", "主機板製造商", vm.System.BoardVendor, "", "SMBIOS/WMI");
        Add("board.model", "主機板", "主機板型號", vm.System.BoardModel, "", "SMBIOS/WMI");
        Add("board.version", "主機板", "PCB/版本", vm.System.BoardVersion, "", "SMBIOS/WMI");
        Add("board.serial", "主機板", "主機板序號", vm.System.BoardSerial, "", "SMBIOS/WMI", sensitive: true);
        Add("bios.vendor", "韌體", "BIOS 製造商", vm.System.BiosVendor, "", "SMBIOS/WMI");
        Add("bios.version", "韌體", "BIOS 版本", vm.System.BiosVersion, "", "SMBIOS/WMI");
        Add("bios.date", "韌體", "BIOS 日期", vm.System.BiosDate, "", "SMBIOS/WMI");
        Add("cpu.name", "處理器", "處理器型號", vm.Cpu.Name, "", "WMI");
        Add("cpu.id", "處理器", "ProcessorId", vm.Cpu.ProcessorId, "", "WMI", sensitive: true);
        Add("cpu.topology", "處理器", "核心/執行緒", $"{vm.Cpu.Cores}/{vm.Cpu.Threads}", "", "WMI");
        Add("cpu.effective.summary", "處理器", "有效頻率量測", vm.FreqTruth.Status, "", "APERF/MPERF 差分", FactTrustLevel.Measured);
        var freqAt = vm.FreqTruth.LastMeasuredAtUtc ?? at;
        foreach (var c in vm.FreqTruth.ClockRows)
            if (c.Mhz >= 0)
                f.Add(new HardwareFact($"cpu.effective.g{c.Ref.Group}.lp{c.Ref.Index}", "處理器", c.LpText,
                    c.Mhz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "MHz", "MSR 0xE8/0xE7", FactTrustLevel.Measured, false, freqAt));
        Add("memory.current", "記憶體", "目前時序", vm.Timings.PrimaryTimingsText, "", "CPU-Z 報告", FactTrustLevel.Derived);
        Add("memory.rate", "記憶體", "目前資料速率", vm.Timings.DataRateText, "", "CPU-Z 報告", FactTrustLevel.Derived);

        var spdOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < vm.SpdModules.Count; i++)
        {
            var m = vm.SpdModules[i];
            string identity = string.Join("\0", m.PartNumber, m.Manufacturer, m.Size, m.ManufacturingDate);
            string baseKey = HardwareSnapshotService.StableKey(identity);
            int occurrence = spdOccurrences.TryGetValue(baseKey, out int seen) ? seen + 1 : 1;
            spdOccurrences[baseKey] = occurrence;
            string p = $"memory.spd.{baseKey}.{occurrence}";
            Add(p + ".slot", "SPD", "插槽", m.Slot, "", m.Source);
            Add(p + ".manufacturer", "SPD", "模組製造商", m.Manufacturer, "", m.Source);
            Add(p + ".dram", "SPD", "顆粒製造商", m.DramManufacturer, "", m.Source);
            Add(p + ".part", "SPD", "料號", m.PartNumber, "", m.Source);
            Add(p + ".size", "SPD", "容量", m.Size, "", m.Source);
            Add(p + ".checksum", "SPD", "CRC", m.Checksum, "", m.Source, FactTrustLevel.Measured);
            Add(p + ".date", "SPD", "製造日期", m.ManufacturingDate, "", m.Source);
        }

        foreach (var d in vm.PhysicalDisks.OrderBy(x => x.Index))
        {
            string serial = string.IsNullOrWhiteSpace(d.SerialNumber) || d.SerialNumber == "—" ? "" : d.SerialNumber;
            string identity = serial.Length > 0 ? d.Model + "\0" + serial : d.Model + "\0index:" + d.Index;
            string p = "disk." + HardwareSnapshotService.AnonymousKey(identity);
            Add(p + ".model", "儲存", "磁碟型號", d.Model, "", "Win32_DiskDrive");
            Add(p + ".size", "儲存", "容量", d.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), "B", "Win32_DiskDrive", FactTrustLevel.Measured);
            Add(p + ".firmware", "儲存", "韌體", d.Firmware, "", "Win32_DiskDrive");
            Add(p + ".serial", "儲存", "序號", d.SerialNumber, "", "Win32_DiskDrive", sensitive: true);
        }

        var gpuGroups = vm.GpuDetails.GroupBy(g => string.Join("\0", g.Name, g.VendorId, g.ModelId, g.RevisionId))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in gpuGroups)
        {
            int occurrence = 0;
            foreach (var g in group)
            {
                occurrence++;
                string id = HardwareSnapshotService.AnonymousKey(group.Key) + "." + occurrence;
                Add($"gpu.{id}.name", "顯示卡", "型號", g.Name, "", "CPU-Z 報告");
                Add($"gpu.{id}.ids", "顯示卡", "Vendor/Device/Revision", $"{g.VendorId}/{g.ModelId}/{g.RevisionId}", "", "CPU-Z 報告");
                Add($"gpu.{id}.driver", "顯示卡", "驅動版本", g.DriverVersion, "", "CPU-Z 報告");
            }
        }

        foreach (var link in vm.PcieLink.Rows.OrderBy(x => x.Location))
        {
            string p = "pcie." + link.Location.Replace(':', '-').Replace('.', '-');
            Add(p + ".name", "PCIe", "裝置", link.Name, "", "PCI 設定空間 + PnP");
            Add(p + ".current", "PCIe", "目前鏈路", link.CurrentText, "", "PCIe Link Status", FactTrustLevel.Measured);
            Add(p + ".capability", "PCIe", "鏈路能力", link.CapableText, "", "PCIe Link Capabilities");
            Add(p + ".errors", "PCIe", "錯誤旗標", link.ErrorText, "", "PCI/PCIe 狀態暫存器", FactTrustLevel.Measured);
        }

        foreach (var driver in vm.DriverAudit.AllRows.OrderBy(x => x.Device))
        {
            string p = "driver." + HardwareSnapshotService.StableKey(driver.Device + "-" + driver.Inf);
            Add(p + ".version", "驅動", driver.Device, driver.VersionText, "", "Win32_PnPSignedDriver");
            Add(p + ".date", "驅動", "驅動日期", driver.DateText, "", "Win32_PnPSignedDriver");
            Add(p + ".signed", "驅動", "簽章狀態", driver.SignText, "", "Win32_PnPSignedDriver");
        }

        // 晶片組安全三態事實（啟動路徑以 WinRing0 後端載入；讀不到即帶原因，不被省略）。
        f.AddRange(vm.EvidenceLab.ChipsetFacts);
        // SPI 快閃安全三態事實（驅動未載時整組標缺自家驅動）。
        f.AddRange(vm.EvidenceLab.SpiFlashFacts);
        // SPI 快閃地圖與 BIOS 區雜湊（WP4）。
        f.AddRange(vm.EvidenceLab.SpiHashFacts);
        // SPI 快閃熵圖：內容組成（只描述分布，不判斷好壞）。
        f.AddRange(vm.EvidenceLab.SpiEntropyFacts);
        // BIOS 區 vs 參考映像的比對結果（使用者觸發，重載後清空）。
        f.AddRange(vm.EvidenceLab.SpiCompareFacts);
        // ACPI 表清單三態事實（usermode 列舉）。
        f.AddRange(vm.EvidenceLab.AcpiFacts);
        // 儲存可靠性計數器（WMI；逐欄位三態，未提供的欄位不會變成 0）。
        f.AddRange(vm.EvidenceLab.StorageReliabilityFacts);
        // PCIe AER 三態事實（ECAM 掃描；歸類「PCIe」）。
        f.AddRange(vm.EvidenceLab.PcieAerFacts);
        // MCHBAR 三態事實（歸類「記憶體控制器」）。
        f.AddRange(vm.EvidenceLab.MchbarFacts);
        // 平台安全 MSR 三態事實（FEATURE_CONTROL／DEBUG_INTERFACE）。
        f.AddRange(vm.EvidenceLab.PlatformSecurityFacts);
        // 後端與環境三態事實（誰在服務、HVCI/Secure Boot/testsigning、環境矩陣裁決）。
        f.AddRange(vm.EvidenceLab.BackendFacts);
        // CPU 韌體身分三態事實（微碼雙來源＋TjMax）與交叉對帳結果。
        f.AddRange(vm.EvidenceLab.CpuFirmwareFacts);
        f.AddRange(vm.EvidenceLab.ReconcileFacts);
        // I/O 埠唯讀事實（POST 代碼）。
        f.AddRange(vm.EvidenceLab.IoPortFacts);
        // CMOS/RTC 唯讀三態事實。
        f.AddRange(vm.EvidenceLab.CmosFacts);
        // SMBus 唯讀事實（TSOD）。
        f.AddRange(vm.EvidenceLab.SmbusFacts);
        // UEFI 開機設定三態事實。
        f.AddRange(vm.EvidenceLab.UefiFacts);
        // UEFI 簽章資料庫（db/dbx/KEK/PK）與 Boot 條目（需提權；讀不到三態）。
        f.AddRange(vm.EvidenceLab.UefiSignatureFacts);
        // Super I/O 探測三態事實。
        f.AddRange(vm.EvidenceLab.SuperIoFacts);
        // Bus 0 裝置盤點（WP30 知識層）。
        f.AddRange(vm.EvidenceLab.PciInventoryFacts);
        // TPM 量測開機鏈（WP14）。
        f.AddRange(vm.EvidenceLab.TpmFacts);
        // 平台拓撲與攻擊面聲明（NUMA＋Rowhammer 界線）。
        f.AddRange(vm.EvidenceLab.PlatformFacts);
        // 虛擬化平台三態（元件／服務／虛擬層分離）——獨立於 PlatformFacts，故另加。
        f.AddRange(vm.EvidenceLab.VirtualizationFacts);

        return f;
    }
}

/// <summary>寫入稽核畫面的一列（v2.55）：時間／目標／呼叫者／內容／成敗符號。資料投影，不是事實鍵。</summary>
public sealed record WriteAuditRow(string TimeText, string Target, string Caller, string Detail, string ResultText);

public sealed record EvidenceFactRow(string Category, string Name, string Value, string Source, string Trust, bool Sensitive,
    FactAvailability Availability = FactAvailability.Present, string? UnavailableReason = null)
{
    public bool IsUnavailable => Availability != FactAvailability.Present;
    public string ValueText => IsUnavailable
        ? UnavailableText(Availability, UnavailableReason)
        : Sensitive && HardwareSnapshotService.IsRedacted(Value) ? "（已遮蔽）" : Value;

    /// <summary>
    /// 裁決警示：值以這些前綴開頭＝裁決對使用者不利（未保護／未鎖／開放／可寫入），頁面以警示色呈現。
    /// 前綴與各服務的裁決文字一一對應（ChipsetSecurityService／SpiFlashService／PlatformSecurityMsrService）；
    /// 讀不到的列一律不算警示——「讀不到」有自己的誠實呈現，不冒充危險。
    /// </summary>
    public bool IsWarning => !IsUnavailable && WarningPrefixes.Any(p => Value.StartsWith(p, StringComparison.Ordinal));

    internal static readonly string[] WarningPrefixes =
    [
        "未保護",           // BIOS_CNTL BLE=0
        "未鎖定",           // HSFSTS FLOCKDN=0、FEATURE_CONTROL Lock=0
        "未鎖：",           // SMRAMC D_LCK=0
        "SMRAM 對外開放",   // SMRAMC D_OPEN
        "BIOS 區域可寫入",  // FRAP bit1=1
        "除錯埠啟用中",     // DEBUG_INTERFACE ENABLE 且未鎖
        "測試簽章模式開啟", // testsigning：允許未經微軟簽署的核心驅動
        "矛盾：",           // 交叉對帳 Contradicts：兩個來源說不同的話
        "RTC 掉電",         // CMOS VRT=0：電池失效或曾斷電
        "金鑰未部署",       // UEFI SetupMode=1：Secure Boot 金鑰處於安裝模式
    ];

    /// <summary>把三態可用性轉成誠實的人類文字：讀不到就說讀不到並附原因，不以空白或舊值冒充。</summary>
    internal static string UnavailableText(FactAvailability availability, string? reason)
    {
        string label = availability switch
        {
            FactAvailability.Present => "可用",
            FactAvailability.NotSupported => "不支援",
            FactAvailability.InsufficientPrivilege => "讀不到",
            FactAvailability.ReadError => "讀取失敗",
            FactAvailability.NotApplicable => "不適用",
            FactAvailability.Unknown => "未確認",
            _ => "讀不到",
        };
        return string.IsNullOrWhiteSpace(reason) ? label : $"{label}：{reason}";
    }

    public static EvidenceFactRow From(HardwareSnapshotFact f) => new(f.Category, f.Name,
        string.IsNullOrEmpty(f.Unit) ? f.Value : $"{f.Value} {f.Unit}", f.Source,
        HardwareSnapshotService.TrustText(f.Trust), f.Sensitive, f.Availability, f.UnavailableReason);

    public static EvidenceFactRow From(HardwareFact f) => new(f.Category, f.Name,
        string.IsNullOrEmpty(f.Unit) ? f.Value : $"{f.Value} {f.Unit}", f.Source,
        HardwareSnapshotService.TrustText(f.Trust), f.Sensitive, f.Availability, f.UnavailableReason);
}

public sealed record EvidenceChangeRow(string Kind, string Category, string Name, string Before, string After, string Delta, Severity Severity)
{
    public static EvidenceChangeRow From(HardwareFactChange c) => new(
        c.Kind switch { SnapshotChangeKind.Added => "新增", SnapshotChangeKind.Removed => "消失", _ => "變更" },
        c.Current?.Category ?? c.Previous?.Category ?? "其他",
        c.Current?.Name ?? c.Previous?.Name ?? c.Key,
        SideText(c.Previous),
        SideText(c.Current), c.DeltaText ?? "—",
        c.Kind == SnapshotChangeKind.Changed ? Severity.Warning : Severity.Neutral);

    /// <summary>差異某一側的顯示文字：讀不到就顯示原因，敏感值遮蔽，不存在則破折號。</summary>
    private static string SideText(HardwareFact? f)
    {
        if (f is null) return "—";
        if (f.Availability != FactAvailability.Present)
            return EvidenceFactRow.UnavailableText(f.Availability, f.UnavailableReason);
        return f.Sensitive ? "（已遮蔽）" : f.Value;
    }
}

/// <summary>交叉對帳判決卡的一列：規則名＋判定（徽章色由 Severity 經 SeverityToBrush 轉換）＋原因。</summary>
public sealed record ReconcileVerdictRow(string RuleName, FactRelation Relation, string RelationText, string Reason, Severity Severity);

/// <summary>原始快照差異的一列：來源鍵、變動種類、細節（變動位元組數與前幾個位移，或三態原因）。</summary>
public sealed record EvidenceRawChangeRow(string Kind, string Source, string Detail)
{
    public static EvidenceRawChangeRow From(RawRegionChange c) => new(
        c.Kind switch
        {
            RawRegionChangeKind.Added => "新增",
            RawRegionChangeKind.Removed => "消失",
            RawRegionChangeKind.AvailabilityChanged => "狀態改變",
            RawRegionChangeKind.Changed => "變更",
            _ => "未變",
        },
        c.Source,
        c.Kind switch
        {
            RawRegionChangeKind.Changed => $"{c.ChangedOffsets.Count} 位元組變動：前 8 個位移 {string.Join(" ", c.ChangedOffsets.Take(8).Select(i => $"0x{i:X2}"))}" +
                (c.ChangedOffsets.Count > 8 ? "…" : ""),
            RawRegionChangeKind.AvailabilityChanged =>
                $"{EvidenceFactRow.UnavailableText(c.PreviousAvailability, null)} → {EvidenceFactRow.UnavailableText(c.CurrentAvailability, null)}",
            RawRegionChangeKind.Added => $"新出現的來源（狀態：{c.CurrentAvailability}）",
            RawRegionChangeKind.Removed => $"來源這次不存在（前次狀態：{c.PreviousAvailability}）",
            _ => "—",
        });
}
