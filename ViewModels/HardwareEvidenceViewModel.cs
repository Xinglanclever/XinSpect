using System.Collections.ObjectModel;
using System.IO;

namespace XinSpect;

public sealed class HardwareEvidenceViewModel : ObservableObject
{
    private static class MetricIds
    {
        public const string EffectiveClock = "cpu.effective_clock";
        public const string AperfMperfRatio = "cpu.aperf_mperf_ratio";
        public const string NvmePercentageUsed = "nvme.percentage_used";
        public const string NvmeMediaErrors = "nvme.media_errors";
        public const string NvmeErrorLogEntries = "nvme.error_log_entries";
        public const string NvmeDataWritten = "nvme.data_units_written";
        public const string NvmeUnsafeShutdowns = "nvme.unsafe_shutdowns";
    }

    private readonly MainViewModel _vm;
    private readonly EvidenceTimelineService _timeline;
    private bool _busy;
    private string _status = "選擇一項量測。每一列都保留來源；讀不到就留白，不以典型值補齊。";
    private string _summary = "—";
    private int _selectedSection;

    public HardwareEvidenceViewModel(MainViewModel vm)
    {
        _vm = vm;
        try { _timeline = new EvidenceTimelineService(); }
        catch (InvalidDataException)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");
            _timeline = new EvidenceTimelineService(folder, "evidence-timeline-recovered.v1.jsonl");
            _status = "原證據時間軸格式損壞或不相容，已改用新的復原時間軸；原檔保留未動。";
        }
    }

    public ObservableCollection<EvidenceAuditRow> Rows { get; } = [];
    public ObservableCollection<EvidenceTimelineRow> TimelineRows { get; } = [];
    public IReadOnlyList<string> Sections { get; } = ["PCI 資源", "SPD 一致性", "裝置診斷", "電源樣本", "儲存樣本"];
    public int SelectedSection { get => _selectedSection; set { if (SetProperty(ref _selectedSection, value)) { Rows.Clear(); TimelineRows.Clear(); Summary = "—"; Status = "按「重新擷取」讀取這一類證據。"; } } }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRun)); } }
    public bool CanRun => !_busy;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Rows.Clear();
        TimelineRows.Clear();
        Status = "正在擷取唯讀證據…";
        try
        {
            switch (SelectedSection)
            {
                case 0: await LoadPciAsync(); break;
                case 1: LoadSpd(); break;
                case 2: await LoadDevicesAsync(); break;
                case 3: await CapturePowerAsync(); break;
                case 4: await CaptureStorageAsync(); break;
            }
        }
        catch (Exception ex)
        {
            Summary = "擷取失敗";
            Status = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadPciAsync()
    {
        var report = await Task.Run(() => new PciResourceAuditService().Audit());
        foreach (var audit in report.Devices)
        {
            var d = audit.Device;
            string ids = $"VEN {audit.VendorId} ・ DEV {audit.DeviceId} ・ SUBSYS {audit.SubsystemId} ・ REV {audit.RevisionId}";
            string resources = d.Resources.Count == 0 ? "沒有已配置資源"
                : string.Join("；", d.Resources.Select(ResourceText));
            string finding = audit.Findings.Count == 0 ? "Windows 未回報問題" : string.Join("；", audit.Findings);
            Rows.Add(new EvidenceAuditRow(d.Description, ids, resources, finding,
                "SetupAPI / cfgmgr32", audit.Findings.Count == 0 ? Severity.Neutral : Severity.Warning));
        }
        Summary = $"{report.Devices.Count} 個 PCI 功能 ・ {report.Devices.Sum(x => x.Device.Resources.Count)} 段 Windows 配置資源 ・ {report.Devices.Count(x => x.Findings.Count > 0)} 個有客觀發現";
        Status = "記憶體範圍是 Windows 配置的 BAR/孔徑，不是顯示記憶體容量；本頁沒有讀 MMIO、AER、BER 或訓練歷史。";
    }

    private static string ResourceText(PciAssignedResource r)
    {
        if (r.Kind is PciResourceKind.Irq or PciResourceKind.Dma) return $"{r.Label} {r.Start}";
        return $"{r.Label} 0x{r.Start:X}-0x{r.End:X}（{r.Length:N0} B）";
    }

    private void LoadSpd()
    {
        var reports = SpdConsistencyAuditService.Audit(_vm.DirectSpdReads, _vm.CpuzSpdModules,
            _vm.Smbios.MemoryDevices, _vm.Timings);
        foreach (var slot in reports)
            foreach (var f in slot.Findings)
                Rows.Add(new EvidenceAuditRow(FieldName(f.Field), slot.Slot, f.Summary,
                    string.Join("；", f.Evidence.Select(x => $"{x.Source}={x.Value}")),
                    string.Join(" / ", f.Evidence.Select(x => x.Kind).Distinct()), ToSeverity(f.Verdict)));
        Summary = $"{reports.Count} 條原生 SPD ・ 一致 {reports.Count(x => x.Verdict == SpdAuditVerdict.Consistent)} ・ 矛盾 {reports.Count(x => x.Verdict == SpdAuditVerdict.Conflict)} ・ 可疑 {reports.Count(x => x.Verdict == SpdAuditVerdict.Suspicious)} ・ 缺資料 {reports.Count(x => x.Verdict == SpdAuditVerdict.MissingData)}";
        Status = reports.Count == 0
            ? "目前沒有保留到原生 SPD 位元組；重新初始化後若 SMBus 可讀，這裡才會進行 CRC 與跨來源稽核。"
            : "只有原生 SPD 能驗 CRC；CPU-Z/SMBIOS 是交叉證據。單一欄位不一致不會直接宣稱模組造假。";
    }

    private async Task LoadDevicesAsync()
    {
        var report = await Task.Run(() => new DeviceDiagnosticService().Scan());
        foreach (var d in report.Devices)
        {
            string state = d.IsPresent ? "目前在場" : "目前未連接／不在場";
            string driver = $"{d.DriverProvider} {d.DriverVersion} ・ {d.InfName} ・ {d.Signer}".Trim();
            Rows.Add(new EvidenceAuditRow(d.FriendlyName, $"{d.DeviceClass} ・ {state}", driver,
                $"問題碼 {d.ProblemCodeText}：{d.ProblemExplanation} ・ 父項 {d.ParentInstanceId}",
                "SetupAPI / WMI", d.IsPresent && d.Findings.Count > 0 ? Severity.Warning : Severity.Neutral));
            foreach (var f in d.Findings)
                Rows.Insert(0, new EvidenceAuditRow(f.Summary, d.FriendlyName, f.Evidence,
                    $"Windows problem code {d.ProblemCodeText} ・ {d.InstanceId}", "Windows PnP", ToSeverity(f.Severity)));
        }
        Summary = $"{report.Devices.Count} 個裝置 ・ 在場 {report.PresentCount} ・ 不在場 {report.GhostCount} ・ 發現 {report.FindingCount}";
        Status = "不在場（幽靈）裝置本身不是故障；只有 Windows problem code、缺驅動或簽章事實才列為發現。";
    }

    private async Task CapturePowerAsync()
    {
        await _vm.FreqTruth.MeasureAsync();
        var now = _vm.FreqTruth.LastMeasuredAtUtc ?? DateTimeOffset.UtcNow;
        var samples = new List<EvidenceSample>();
        foreach (var c in _vm.FreqTruth.ClockRows)
        {
            if (c.Mhz < 0) continue;
            samples.Add(new EvidenceSample
            {
                Category = "power", DeviceKey = c.LpText, Metric = MetricIds.EffectiveClock, Value = (decimal)c.Mhz,
                Unit = "MHz", Source = "APERF/MPERF 差分", Trust = EvidenceTrustLevel.DirectHardware,
                TimeUtc = now, Semantics = EvidenceMetricSemantics.Gauge, Role = EvidenceValueRole.DerivedMeasurement,
                Context = new(StringComparer.Ordinal) { ["window"] = "250 ms", ["note"] = "不是 P-state 上限" },
            });
            samples.Add(new EvidenceSample
            {
                Category = "power", DeviceKey = c.LpText, Metric = MetricIds.AperfMperfRatio, Value = (decimal)c.Ratio,
                Unit = "ratio", Source = "MSR 0xE8/0xE7", Trust = EvidenceTrustLevel.DirectHardware,
                TimeUtc = now, Semantics = EvidenceMetricSemantics.Ratio, Role = EvidenceValueRole.DerivedMeasurement,
                Context = new(StringComparer.Ordinal) { ["window"] = "250 ms", ["note"] = "不是 P-state 上限" },
            });
        }
        await Task.Run(() => _timeline.Ingest(samples));
        ShowTimeline("power", now.AddDays(-7), now);
        Summary = $"寫入 {samples.Count} 筆逐核有效頻率證據 ・ 近 7 天 {TimelineRows.Count} 筆";
        Status = samples.Count == 0
            ? "目前沒有 APERF/MPERF 量測；請先到處理器頁執行「頻率真相」，再回來擷取。"
            : "保存的是取樣窗平均有效頻率與比值，不把頻率上限當成實際 P-state。";
    }

    private async Task CaptureStorageAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new List<EvidenceSample>();
        foreach (var d in _vm.PhysicalDisks.Where(x => x.Kind == DiskKind.NvmeSsd))
        {
            byte[]? raw = await Task.Run(() => StorageSmartService.TryReadNvmeLog(d.Index));
            if (raw is null || NvmeHealth.Decode(raw) is not { } h) continue;
            string serial = string.IsNullOrWhiteSpace(d.SerialNumber) || d.SerialNumber == "—" ? "" : d.SerialNumber;
            string identity = serial.Length > 0 ? d.Model + "\0" + serial : d.Model + "\0index:" + d.Index;
            string key = "disk-id-" + HardwareSnapshotService.AnonymousKey(identity);
            samples.AddRange([
                Sample(MetricIds.NvmePercentageUsed, h.PercentageUsed, "%", EvidenceMetricSemantics.Gauge),
                Sample(MetricIds.NvmeMediaErrors, h.MediaErrors, "count", EvidenceMetricSemantics.MonotonicCounter, 64),
                Sample(MetricIds.NvmeErrorLogEntries, h.ErrorLogEntries, "count", EvidenceMetricSemantics.MonotonicCounter, 64),
                Sample(MetricIds.NvmeDataWritten, h.DataUnitsWritten, "1000×512 B", EvidenceMetricSemantics.MonotonicCounter, 64),
                Sample(MetricIds.NvmeUnsafeShutdowns, h.UnsafeShutdowns, "count", EvidenceMetricSemantics.MonotonicCounter, 64),
            ]);

            EvidenceSample Sample(string metric, ulong value, string unit, EvidenceMetricSemantics semantics, int? bits = null) => new()
            {
                Category = "storage", DeviceKey = key, Metric = metric, Value = value,
                Unit = unit, Source = "NVMe log page 0x02", Trust = EvidenceTrustLevel.DeviceReported,
                TimeUtc = now, Semantics = semantics, Role = EvidenceValueRole.Observed, CounterBits = bits,
                Context = new(StringComparer.Ordinal) { ["model"] = d.Model },
            };
        }
        await Task.Run(() => _timeline.Ingest(samples));
        ShowTimeline("storage", now.AddDays(-30), now);
        Summary = $"寫入 {samples.Count} 筆 NVMe 原始計數器 ・ 近 30 天 {TimelineRows.Count} 筆";
        Status = samples.Count == 0
            ? "沒有讀到 NVMe 健康紀錄；儲存堆疊或外接盒可能不轉發協定查詢，這不等於磁碟沒有 SMART。"
            : "只顯示原始值與增量，不估算「還能活幾天」。計數器倒退會被標成重設，不算負成長。";
    }

    private void ShowTimeline(string category, DateTimeOffset from, DateTimeOffset to)
    {
        var all = _timeline.Query(new EvidenceQuery { Category = category, FromUtc = from, ToUtc = to });
        IReadOnlyList<EvidenceSample> display;
        if (all.Count <= 300) display = all;
        else
        {
            var groups = all.GroupBy(x => (x.DeviceKey, x.Metric, x.Unit), StringTupleComparer.OrdinalIgnoreCase).ToArray();
            int perSeries = Math.Max(2, 300 / Math.Max(1, groups.Length));
            display = groups.SelectMany(g => Representative(g.OrderBy(x => x.TimeUtc).ToArray(), perSeries))
                .OrderBy(x => x.TimeUtc).TakeLast(300).ToArray();
        }
        foreach (var p in display.OrderByDescending(x => x.TimeUtc))
            TimelineRows.Add(new EvidenceTimelineRow(p.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), p.DeviceKey,
                EvidenceNames.Name(p.Metric), $"{p.Value:0.###} {p.Unit}", p.Source));
        foreach (var e in EvidenceTimelineService.GenerateEvents(all).TakeLast(200))
        {
            bool concerning = e.Kind == EvidenceEventKind.LinkValueDecreased
                || e.Metric is MetricIds.NvmeMediaErrors or MetricIds.NvmeErrorLogEntries or MetricIds.NvmeUnsafeShutdowns;
            Rows.Add(new EvidenceAuditRow(EventTitle(e), e.DeviceKey,
                $"{e.PreviousValue:0.###} → {e.CurrentValue:0.###} {e.Unit}",
                e.Delta is { } delta ? $"差值 {delta:+0.###;-0.###;0}" : "觀測事件", e.Source,
                concerning ? Severity.Warning : Severity.Neutral));
        }

        static IEnumerable<EvidenceSample> Representative(IReadOnlyList<EvidenceSample> samples, int max)
        {
            if (samples.Count <= max) return samples;
            if (max <= 1) return [samples[^1]];
            return Enumerable.Range(0, max)
                .Select(i => samples[(int)((long)i * (samples.Count - 1) / (max - 1))]);
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string DeviceKey, string Metric, string Unit)>
    {
        public static StringTupleComparer OrdinalIgnoreCase { get; } = new();
        public bool Equals((string DeviceKey, string Metric, string Unit) x, (string DeviceKey, string Metric, string Unit) y)
            => string.Equals(x.DeviceKey, y.DeviceKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Metric, y.Metric, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Unit, y.Unit, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string DeviceKey, string Metric, string Unit) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DeviceKey),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Metric), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Unit));
    }

    private static string EventTitle(EvidenceEvent e) => e.Kind switch
    {
        EvidenceEventKind.LinkValueDecreased => "PCIe 實際鏈路值下降",
        EvidenceEventKind.CounterIncreased => EvidenceNames.Name(e.Metric) + "增加",
        EvidenceEventKind.PercentageIncreased => "NVMe 已使用百分比增加",
        EvidenceEventKind.EffectiveRatioObserved => "APERF/MPERF 有新樣本",
        EvidenceEventKind.ResidencyObserved => "C-state 駐留有新樣本",
        _ => "證據時間軸事件",
    };

    private static string FieldName(SpdAuditField field) => field switch
    {
        SpdAuditField.Checksum => "SPD CRC",
        SpdAuditField.JedecVendor => "JEDEC 廠商",
        SpdAuditField.PartNumber => "料號",
        SpdAuditField.SerialNumber => "序號",
        SpdAuditField.Capacity => "容量",
        SpdAuditField.Speed => "JEDEC 速度",
        SpdAuditField.ManufactureDate => "製造週年",
        SpdAuditField.CurrentTimings => "目前時序",
        _ => "來源完整性",
    };

    private static Severity ToSeverity(SpdAuditVerdict verdict) => verdict switch
    {
        SpdAuditVerdict.Conflict => Severity.Warning,
        SpdAuditVerdict.Suspicious => Severity.Serious,
        _ => Severity.Neutral,
    };

    private static Severity ToSeverity(DeviceDiagnosticSeverity severity) => severity switch
    {
        DeviceDiagnosticSeverity.Error => Severity.Serious,
        DeviceDiagnosticSeverity.Warning => Severity.Warning,
        _ => Severity.Neutral,
    };
}

public sealed record EvidenceAuditRow(string Title, string Scope, string Detail, string Evidence, string Source, Severity Severity);
public sealed record EvidenceTimelineRow(string Time, string Device, string Metric, string Value, string Source);

public static class EvidenceNames
{
    public static string Name(string metric) => metric switch
    {
        "cpu.effective_clock" => "有效頻率",
        "cpu.aperf_mperf_ratio" => "APERF/MPERF",
        "nvme.percentage_used" => "已使用壽命",
        "nvme.media_errors" => "媒體錯誤",
        "nvme.error_log_entries" => "錯誤紀錄項目",
        "nvme.data_units_written" => "累計寫入單位",
        "nvme.unsafe_shutdowns" => "不安全關機",
        _ => metric,
    };
}
