using System.Collections.ObjectModel;
using System.IO;

namespace XinSpect;

/// <summary>
/// 深測中心的狀態機：同一場 Run Session 只產生同一組證據；UI 只並列原始量測，
/// 不合成加權總分，也不把延後項目藏起來。
/// </summary>
public sealed class DeepBenchViewModel : ObservableObject
{
    private const long MiB = 1024 * 1024;
    private const long ReserveBytes = 8L * 1024 * 1024 * 1024;
    private const long MinimumBudgetBytes = 64L * MiB;

    public const string NoScoreNotice =
        "深測中心只並列各測項的原始樣本、可信度與限制，不加權合成單一總分；跨域、跨軟體排名不成立。";

    public const string ScopeNotice =
        "目前可執行三十二個 Phase 1／已接入測項：CPU AES/SHA、Load-to-use/ILP/branch、分支模式矩陣、RDRAND/RDSEED、Intel PMU Top-down、核心延遲、核心到核心搬運頻寬、SMT sibling 干擾、混合核心放置：CPUID hybrid + CPUID 0x1A 誠實分類 P/E，量單緒、同類雙核與混合雙核吞吐、cache coherence／lock scaling、NUMA／TLB／大分頁：遞增 working set 逐頁掃描曲線，大分頁與跨 NUMA 對照各子項獨立判定適用性、DRAM 映射推論：stride 掃描曲線僅供推論，不宣稱確定 row／bank／rank 映射、記憶體四項（含 WHEA 壓力關聯）、D3D11 硬體 GPU FP32、VRAM 讀寫頻寬、PCIe 上傳／下載、dispatch jitter、儲存 QD、混合讀寫、三圖樣寫入驗證、逐 MiB Flush 驗證、SLC 持續寫入、IOCP completion engine、本機 TCP loopback 延遲、WASAPI 音訊緩衝行為、D3D11 Present 幀節奏、Windows 睿頻爬升恢復、Windows 吞吐衰退與 Windows 電源狀態觀察。" +
        "NPU ONNX 不含；.NET crypto 只實測本機 API，不保證特定硬體指令集；Top-down 不適用 AMD／非 Intel 事件配方；網路測項只量 127.0.0.1 loopback；音訊項目只量 WASAPI render 可觀察行為；Present 項目只量 CPU 端 API 時間，不是驅動內部 GPU timestamp 或 input-to-photon latency；睿頻項目只量 managed pulse 下的電源 API 離散頻率曲線，不宣稱實際有效時脈；吞吐項目只量 managed operations，CurrentMhz 只是 P-state 上限換算值；電源項目只量 CallNtPowerInformation 查詢延遲與離散快照變化，不宣稱韌體內部轉換時間；NUMA／TLB 掃描是 TLB、prefetch、快取與 page table walk 的混合效應，不宣稱量到 DTLB 規格，大分頁與跨 NUMA 對照不滿足前提時如實標未執行；DRAM 推論項的 managed 陣列實體分頁由 Windows 決定，本程式不觀察實體位址。";

    public const string LoadWarning =
        "高負載警告：執行期間 CPU、記憶體、GPU 與儲存可能接近滿載；請先儲存工作，筆電請接電源並注意散熱。";

    public const string TempFileWarning =
        "儲存測試只建立 XinSpect.deepbench.tmp 與 XinSpect.iocp.tmp，不碰既有檔案；結束、例外或取消後都會刪除，啟動前會檢查剩餘空間。";

    public const string LocalOnlyNotice =
        "深測歷史只保存在本機設定資料夾，不上傳；沒有雲端同步，也沒有外部伺服器備份。";

    // const 無法被 {Binding} 取值（繫結只走執行個體屬性）；這幾個包裝給 XAML 用，文案單一來源仍是 const。
    public string NoScoreNoticeText => NoScoreNotice;
    public string ScopeNoticeText => ScopeNotice;
    public string LoadWarningText => LoadWarning;
    public string TempFileWarningText => TempFileWarning;
    public string LocalOnlyNoticeText => LocalOnlyNotice;

    private readonly CacheBenchService _cache;
    private readonly MemBandwidthService _memBandwidth;
    private readonly CoreLatencyService _coreLatency;
    private readonly TopDownService _topDown;
    private readonly DeepBenchRunStore _store;
    private readonly IDiskIoFileSystem? _fileSystem;
    private readonly DeepBenchOrchestrator? _injectedOrchestrator;
    private CancellationTokenSource? _runCancellation;

    public CacheBenchService Cache => _cache;
    public MemBandwidthService MemBandwidth => _memBandwidth;
    public CoreLatencyService CoreLatency => _coreLatency;
    public TopDownService TopDown => _topDown;

    public ObservableCollection<DeepBenchCatalogEntry> CatalogRows { get; } = [];
    public ObservableCollection<DeepBenchResultCard> ResultCards { get; } = [];
    public ObservableCollection<DeepBenchInsight> Insights { get; } = [];
    public ObservableCollection<DeepBenchHistoryRow> History { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    private string _stateText = "尚未啟動；選擇快速或完整檔後執行。";
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }

    private string _currentTestId = "none";
    public string CurrentTestId { get => _currentTestId; private set => SetProperty(ref _currentTestId, value); }

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    private DeepBenchRunProfile _selectedProfile = DeepBenchRunProfile.Quick;
    public DeepBenchRunProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
                OnPropertyChanged(nameof(SelectedIds));
        }
    }

    private string _storageRoot = string.Empty;
    public string StorageRoot { get => _storageRoot; set => SetProperty(ref _storageRoot, value); }

    private int _tempBudgetMiB = 256;
    public int TempBudgetMiB { get => _tempBudgetMiB; set => SetProperty(ref _tempBudgetMiB, Math.Clamp(value, 64, 1_048_576)); }

    public DeepBenchRunState? LastState { get; private set; }
    public DeepBenchRunRecord? CurrentRecord { get; internal set; }
    public bool CanStart => !IsRunning;
    public IReadOnlyList<string> SelectedIds => DeepBenchSuitePlanner.Plan(SelectedProfile, [.. CatalogRows]).SelectedIds;

    public DeepBenchViewModel(
        CacheBenchService cache,
        MemBandwidthService memBandwidth,
        CoreLatencyService coreLatency,
        DeepBenchRunStore runStore,
        IDiskIoFileSystem? diskIoFileSystem = null,
        DeepBenchOrchestrator? orchestrator = null,
        TopDownService? topDown = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(memBandwidth);
        ArgumentNullException.ThrowIfNull(coreLatency);
        ArgumentNullException.ThrowIfNull(runStore);
        _cache = cache;
        _memBandwidth = memBandwidth;
        _coreLatency = coreLatency;
        _topDown = topDown ?? new TopDownService();
        _store = runStore;
        _fileSystem = diskIoFileSystem;
        _injectedOrchestrator = orchestrator;

        foreach (DeepBenchCatalogEntry entry in DeepBenchCatalog.All) CatalogRows.Add(entry);
        ReloadHistory();
    }

    public void SetProfileQuick() => SelectedProfile = DeepBenchRunProfile.Quick;
    public void SetProfileFull() => SelectedProfile = DeepBenchRunProfile.Full;

    public void Cancel()
    {
        if (!IsRunning) return;
        StateText = "正在取消；已完成結果會保留。";
        try { _runCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return;
        Errors.Clear();
        IReadOnlyList<string> selected = SelectedIds;
        if (selected.Count == 0)
        {
            AddError("沒有可執行測項；請重新載入深測中心。");
            return;
        }

        if (!ValidateStorage(selected)) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCancellation = linked;
        IsRunning = true;
        OnPropertyChanged(nameof(CanStart));
        LastState = DeepBenchRunState.Running;
        CurrentRecord = null;
        ResultCards.Clear();
        Insights.Clear();
        ProgressPercent = 0;
        CurrentTestId = selected[0];
        StateText = "準備啟動；不產生加權總分。";

        try
        {
            DeepBenchRunRecord record = await BuildOrchestrator().RunAsync(
                SelectedProfile,
                selected,
                new Progress<DeepBenchProgress>(UpdateProgress),
                linked.Token).ConfigureAwait(false);
            ReplaceRecord(record);
        }
        catch (OperationCanceledException)
        {
            LastState = DeepBenchRunState.Cancelled;
            StateText = "已取消；已完成結果已保留。";
            AddError("已取消；只保留取消前已完成的結果，未跑項目不補值。");
        }
        catch (Exception exception)
        {
            LastState = DeepBenchRunState.CompletedWithFailures;
            StateText = "啟動失敗；未產生有效量測。";
            AddError($"深測啟動失敗：{exception.Message}");
        }
        finally
        {
            _runCancellation = null;
            IsRunning = false;
            OnPropertyChanged(nameof(CanStart));
        }
    }

    private bool ValidateStorage(IReadOnlyList<string> selected)
    {
        string[] storageIds = selected
            .Where(id => id is DiskIoMatrixService.QdTestId or DiskIoMatrixService.MixedTestId
                or StorageWriteIntegrityService.TestId or StorageFlushDurabilityService.TestId
                or SlcSustainedWriteAdapter.TestId or StorageIocpEngineService.TestId)
            .ToArray();
        if (storageIds.Length == 0) return true;

        if (string.IsNullOrWhiteSpace(StorageRoot))
        {
            AddError("請先選擇儲存根：storage.qd-ladder、storage.mixed-rw、storage.write-integrity、storage.flush-durability、storage.slc-sustained-write 與 storage.iocp-engine 都需要可寫的暫存位置。");
            return false;
        }

        long available;
        try
        {
            available = _fileSystem?.GetAvailableFreeSpace(StorageRoot)
                ?? new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(StorageRoot))!).AvailableFreeSpace;
        }
        catch (Exception exception)
        {
            AddError($"無法讀取儲存根剩餘空間：{exception.Message}");
            return false;
        }

        long budget = Math.Max(TempBudgetMiB * MiB, MinimumBudgetBytes);
        long remaining = available - budget;
        long slcTarget = selected.Contains(SlcSustainedWriteAdapter.TestId, StringComparer.Ordinal)
            ? SlcSustainedWriteAdapter.GetTargetBytes(SelectedProfile)
            : 0;
        if (remaining - slcTarget < ReserveBytes)
        {
            AddError($"空間守衛未通過：預算 {budget / MiB:0} MiB 後必須仍保留 8 GB；目前可用 {available / (double)MiB:0} MiB。");
            AddError($"受影響測項：storage.qd-ladder、storage.mixed-rw、storage.write-integrity、storage.flush-durability、storage.slc-sustained-write、storage.iocp-engine。請改用更大磁碟或降低暫存預算。");
            return false;
        }

        return true;
    }

    private DeepBenchOrchestrator BuildOrchestrator()
    {
        if (_injectedOrchestrator is not null) return _injectedOrchestrator;

        string root = System.IO.Path.GetFullPath(StorageRoot);
        long budget = Math.Max(TempBudgetMiB * MiB, MinimumBudgetBytes);
        IDeepBenchTest[] tests =
        [
            new CryptoMicrobenchService(),
            new CpuMicroarchBenchService(),
            new BranchSpeculationService(),
            new HardwareRandomBenchAdapter(),
            new TopDownAdapter(_topDown),
            new CoreLatencyAdapter(_coreLatency),
            new TopologyCoreBandwidthService(),
            new SmtContentionService(),
            new TopologyHybridPlacementService(),
            new CoherenceLockService(),
            new MemoryNumaTlbLargePageService(),
            new CacheLatencyAdapter(_cache),
            new StreamBandwidthAdapter(_memBandwidth),
            new LoadedLatencyAdapter(_memBandwidth),
            new DramMappingInferenceService(),
            new MemoryEccWheaStressAdapter(_memBandwidth),
            new GpuFp32ComputeService(),
            new GpuVramBandwidthService(),
            new GpuPcieTransferService(),
            new GpuDispatchJitterService(),
            new DiskIoMatrixService(DiskIoMatrixKind.QdLadder, root, budget, _fileSystem),
            new DiskIoMatrixService(DiskIoMatrixKind.MixedReadWrite, root, budget, _fileSystem),
            new StorageWriteIntegrityService(root, budget, _fileSystem),
            new StorageFlushDurabilityService(root, budget, _fileSystem),
            new SlcSustainedWriteAdapter(root, budget, _fileSystem),
            new StorageIocpEngineService(root, budget),
            new NetworkStackLatencyAdapter(),
            new AudioBufferGlitchService(),
            new PresentFramePacingService(),
            new BoostRecoveryService(),
            new ThroughputDegradationService(),
            new PowerStateLatencyService(),
        ];
        return new DeepBenchOrchestrator(tests, _store);
    }

    private void UpdateProgress(DeepBenchProgress progress)
    {
        CurrentTestId = progress.CurrentTestId;
        ProgressPercent = Math.Clamp(progress.Fraction * 100, 0, 100);
        StateText = $"{progress.CurrentTestId}：{progress.CompletedCount}/{progress.TotalCount}・{progress.Phase}";
    }

    private void ReplaceRecord(DeepBenchRunRecord record)
    {
        CurrentRecord = record;
        LastState = record.State;
        ProgressPercent = record.State == DeepBenchRunState.Cancelled ? ProgressPercent : 100;
        StateText = DescribeState(record);
        ResultCards.Clear();
        foreach (DeepBenchTestResult result in record.Results)
            ResultCards.Add(DeepBenchResultCard.From(result));
        Insights.Clear();
        foreach (DeepBenchInsight insight in record.Insights) Insights.Add(insight);

        var row = DeepBenchHistoryRow.From(record);
        History.Insert(0, row);
        if (History.Count > 20) History.RemoveAt(History.Count - 1);
    }

    private void ReloadHistory()
    {
        History.Clear();
        foreach (DeepBenchRunRecord record in _store.LoadRecent(20))
            History.Add(DeepBenchHistoryRow.From(record));
        if (!string.IsNullOrWhiteSpace(_store.LastLoadError)) AddError(_store.LastLoadError!);
    }

    private static string DescribeState(DeepBenchRunRecord record) => record.State switch
    {
        DeepBenchRunState.Completed => $"已完成 {record.Results.Count} 項；只並列原始量測，不加權合成。",
        DeepBenchRunState.CompletedWithFailures => $"已完成 {record.Results.Count} 項，其中含失敗或無效結果；失敗項不補值。",
        DeepBenchRunState.Cancelled => $"已取消；保留 {record.Results.Count} 項結果，其餘標示未跑。",
        _ => "執行中。",
    };

    private void AddError(string message)
    {
        if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message);
    }
}

/// <summary>UI 用的結果卡：把每一項結果的可信度與限制一起搬上畫面。</summary>
public sealed class DeepBenchResultCard
{
    public required string TestId { get; init; }
    public required string Title { get; init; }
    public required string StateText { get; init; }
    public required string SummaryText { get; init; }
    public required string ConfigurationText { get; init; }
    public required string StartedText { get; init; }
    public required string EndedText { get; init; }
    public required IReadOnlyList<string> Limitations { get; init; }
    public required IReadOnlyList<DeepBenchMetricDisplay> Metrics { get; init; }
    public required DeepBenchFailureKind FailureKind { get; init; }
    public required string? Error { get; init; }
    public bool HasErrorText => !string.IsNullOrWhiteSpace(Error);

    public static DeepBenchResultCard From(DeepBenchTestResult result, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        string displayTitle = string.IsNullOrWhiteSpace(title)
            ? DeepBenchCatalog.All.FirstOrDefault(entry => entry.Id == result.TestId)?.Title ?? result.TestId
            : title;
        return new DeepBenchResultCard
        {
            TestId = result.TestId,
            Title = displayTitle,
            StateText = State(result),
            SummaryText = result.HasSuccessfulMeasurement
                ? $"{result.Metrics.Count} 項指標；樣本與可信度逐項列出。"
                : "沒有有效量測樣本；不提供推算值。",
            ConfigurationText = result.Configuration,
            StartedText = result.StartedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            EndedText = result.EndedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            Limitations = [.. result.Limitations],
            Metrics = [.. result.Metrics.Select(DeepBenchMetricDisplay.From)],
            FailureKind = result.FailureKind,
            Error = result.Error,
        };
    }

    private static string State(DeepBenchTestResult result) => result.FailureKind switch
    {
        DeepBenchFailureKind.None when result.Metrics.Count > 0 => "已完成；含原始樣本與可信度。",
        DeepBenchFailureKind.None => "無有效量測。",
        DeepBenchFailureKind.Cancelled => "已取消；不補值。",
        DeepBenchFailureKind.NotRun => "未執行；無有效量測。",
        _ => $"無有效量測（{result.FailureKind}）。",
    };
}

/// <summary>單一指標的展示快照，保留樣本數與可信度，不把一個數字偽裝成完整證據。</summary>
public sealed record DeepBenchMetricDisplay(
    string Title,
    string Unit,
    int SampleCount,
    DeepBenchConfidence Confidence,
    string SummaryText,
    string Configuration,
    IReadOnlyList<string> PointLines)
{
    public static DeepBenchMetricDisplay From(DeepBenchMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        DeepBenchMeasurementSummary statistics = metric.Statistics;
        if (DeepBenchMeasurementStatistics.PoolsDistinctConfigurations(metric))
        {
            // 樣本跨多種量測配置：池化平均對應不到任何真實配置，只列逐點值。
            string[] pointLines = metric.Points.Select(point =>
            {
                DeepBenchMeasurementSummary pointStatistics = point.Statistics;
                string detail = pointStatistics.Count > 1
                    ? $"・{pointStatistics.Count} 樣本・可信度 {pointStatistics.Confidence}"
                    : string.Empty;
                return $"{DeepBenchMeasurementStatistics.DescribeAxes(point.Axes)}：{point.Value:0.###} {metric.Unit}{detail}";
            }).ToArray();
            return new DeepBenchMetricDisplay(
                metric.Title,
                metric.Unit,
                metric.Samples.Count,
                DeepBenchConfidence.Insufficient,
                $"{metric.Title}：{metric.Points.Count} 點跨多種配置；不跨配置平均，逐點值如下。",
                metric.Configuration,
                pointLines);
        }
        return new DeepBenchMetricDisplay(
            metric.Title,
            metric.Unit,
            metric.Samples.Count,
            statistics.Confidence,
            $"{metric.Title}：平均 {statistics.Mean:0.###} {metric.Unit}・樣本 {metric.Samples.Count}・可信度 {statistics.Confidence}",
            metric.Configuration,
            []);
    }
}

/// <summary>本機歷史列；只描述一場 Run Session，不做不同場次的合成。</summary>
public sealed class DeepBenchHistoryRow
{
    public required Guid SessionId { get; init; }
    public required string State { get; init; }
    public required string ProfileText { get; init; }
    public required string StartedText { get; init; }
    public required string EndedText { get; init; }
    public required int ResultCount { get; init; }
    public required string CompletionText { get; init; }

    public static DeepBenchHistoryRow From(DeepBenchRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new DeepBenchHistoryRow
        {
            SessionId = record.SessionId,
            State = StateText(record.State),
            ProfileText = record.Profile == DeepBenchRunProfile.Quick ? "Quick" : "Full",
            StartedText = record.StartedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            EndedText = record.EndedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            ResultCount = record.Results.Count,
            CompletionText = record.State == DeepBenchRunState.Cancelled
                ? $"{StateText(record.State)}；取消前完成 {record.Results.Count} 項，其餘未執行。"
                : $"{StateText(record.State)}；已完成 {record.Results.Count} 項。",
        };
    }

    private static string StateText(DeepBenchRunState state) => state switch
    {
        DeepBenchRunState.Running => "執行中",
        DeepBenchRunState.Completed => "完成",
        DeepBenchRunState.CompletedWithFailures => "完成但含失敗",
        DeepBenchRunState.Cancelled => "已取消",
        _ => "未知",
    };
}
