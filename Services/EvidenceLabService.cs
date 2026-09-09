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
            Status = Changes.Count == 0
                ? "沒有發現差異。比較的是已擷取事實；某來源這次讀不到時會明確列為消失，不以舊值填補。"
                : $"發現 {Changes.Count} 項差異。請依來源與可信度逐項判讀；差異本身不等於故障。";
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

        return f;
    }
}

public sealed record EvidenceFactRow(string Category, string Name, string Value, string Source, string Trust, bool Sensitive)
{
    public string ValueText => Sensitive && HardwareSnapshotService.IsRedacted(Value) ? "（已遮蔽）" : Value;
    public static EvidenceFactRow From(HardwareSnapshotFact f) => new(f.Category, f.Name,
        string.IsNullOrEmpty(f.Unit) ? f.Value : $"{f.Value} {f.Unit}", f.Source, HardwareSnapshotService.TrustText(f.Trust), f.Sensitive);
}

public sealed record EvidenceChangeRow(string Kind, string Category, string Name, string Before, string After, string Delta, Severity Severity)
{
    public static EvidenceChangeRow From(HardwareFactChange c) => new(
        c.Kind switch { SnapshotChangeKind.Added => "新增", SnapshotChangeKind.Removed => "消失", _ => "變更" },
        c.Current?.Category ?? c.Previous?.Category ?? "其他",
        c.Current?.Name ?? c.Previous?.Name ?? c.Key,
        c.Previous is { Sensitive: true } ? "（已遮蔽）" : c.Previous?.Value ?? "—",
        c.Current is { Sensitive: true } ? "（已遮蔽）" : c.Current?.Value ?? "—", c.DeltaText ?? "—",
        c.Kind == SnapshotChangeKind.Changed ? Severity.Warning : Severity.Neutral);
}
