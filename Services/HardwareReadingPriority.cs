namespace XinSpect;

/// <summary>同義欄位以硬體直讀、WMI、外部報告的順序補值。</summary>
internal static class HardwareReadingPriority
{
    private static bool Known(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim() is not ("—" or "-" or "N/A");

    private static string Pick(params string?[] values)
        => values.FirstOrDefault(Known) ?? "—";

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    internal static CpuDetail ReadNativeCpu(MainViewModel vm)
    {
        var result = new CpuDetail
        {
            Specification = vm.CpuId.Supported ? vm.CpuId.Brand : "—",
            Instructions = vm.CpuId.Supported && vm.CpuId.Features.Count > 0
                ? string.Join(", ", vm.CpuId.Features.Select(f => f.Name)) : "—",
            CoreSpeed = vm.Live?.CpuClockText ?? "—",
            CoreVoltage = vm.Live?.CpuVoltText ?? "—",
        };
        if (CpuGeneration.ReadSignature() is { } signature)
            result.CpuId = $"0x{signature:X8}（CPUID）";
        return result;
    }

    internal static CpuDetail ResolveCpu(CpuDetail direct, CpuStatic wmi, CpuDetail? report)
    {
        report ??= new CpuDetail();
        var result = new CpuDetail
        {
            Specification = Pick(direct.Specification, wmi.Name, report.Specification),
            Package = Pick(direct.Package, report.Package),
            CoreSpeed = Pick(direct.CoreSpeed, report.CoreSpeed),
            CoreVoltage = Pick(direct.CoreVoltage,
                Positive(wmi.CurrentVoltage) ? wmi.CurrentVoltageText + "（WMI）" : null, report.CoreVoltage),
            StockFreq = Pick(direct.StockFreq, report.StockFreq),
            L2 = Pick(direct.L2, wmi.L2Cache, report.L2),
            L3 = Pick(direct.L3, wmi.L3Cache, report.L3),
            CpuId = Pick(direct.CpuId, report.CpuId),
            ExtCpuId = Pick(direct.ExtCpuId, report.ExtCpuId),
            Instructions = Pick(direct.Instructions, report.Instructions),
            Codename = Pick(direct.Codename, report.Codename),
            Technology = Pick(direct.Technology, report.Technology),
            Stepping = Pick(direct.Stepping, report.Stepping),
            Microcode = Pick(direct.Microcode, report.Microcode),
            Multiplier = Pick(direct.Multiplier, report.Multiplier),
            TdpLimit = Pick(direct.TdpLimit, report.TdpLimit),
            Tjmax = Pick(direct.Tjmax, report.Tjmax),
            MaxNonTurbo = Pick(direct.MaxNonTurbo, report.MaxNonTurbo),
            MaxTurbo = Pick(direct.MaxTurbo, report.MaxTurbo),
            MaxEfficiency = Pick(direct.MaxEfficiency, report.MaxEfficiency),
            PowerMaxPl1 = Pick(direct.PowerMaxPl1, report.PowerMaxPl1),
            Pl1Window = Pick(direct.Pl1Window, report.Pl1Window),
            PowerMaxPl2 = Pick(direct.PowerMaxPl2, report.PowerMaxPl2),
            L1D = Pick(direct.L1D, report.L1D),
            L1I = Pick(direct.L1I, report.L1I),
        };
        result.TurboRatios.AddRange(direct.HasTurboRatios ? direct.TurboRatios : report.TurboRatios);
        result.Loaded = Known(result.Specification) || Known(result.Codename);
        return result;
    }

    internal static MemoryTimings ResolveMemory(
        MemoryTimings? direct, MemoryTimings? wmi, MemoryTimings? report)
    {
        // SPD 能力表與控制器目前運行時序是不同事實，不相互覆蓋。
        var current = direct is { IsJedecReference: false } ? direct : null;
        var timing = current is { Loaded: true } ? current
            : report is { Loaded: true } ? report : direct;
        var timingSources = new HashSet<string>();
        string TimingValue(Func<MemoryTimings, string> read)
        {
            var candidates = timing?.IsJedecReference == true ? new[] { timing } : new[] { current, wmi, report };
            var source = candidates.FirstOrDefault(t => t is { Loaded: true } && Known(read(t)));
            if (source is null) return "—";
            timingSources.Add(Pick(source.SourceText));
            return read(source);
        }
        var frequency = new[] { current, wmi, report, direct }
            .FirstOrDefault(t => t is not null && (Positive(t.DramFrequencyMHz)
                || t.DramFrequencyMHz == 0 && Known(t.DataRateText)));
        var result = new MemoryTimings
        {
            Loaded = timing?.Loaded == true,
            IsJedecReference = timing?.IsJedecReference == true,
            MemoryTypeText = Pick(direct?.MemoryTypeText, wmi?.MemoryTypeText, report?.MemoryTypeText),
            MemorySizeText = Pick(wmi?.MemorySizeText, report?.MemorySizeText, direct?.MemorySizeText),
            ChannelsText = Pick(current?.ChannelsText, wmi?.ChannelsText, report?.ChannelsText),
            DramFrequencyMHz = frequency?.DramFrequencyMHz ?? 0,
            DataRateText = frequency?.DataRateText ?? "—",
            CL = TimingValue(t => t.CL),
            TRCD = TimingValue(t => t.TRCD),
            TRP = TimingValue(t => t.TRP),
            TRAS = TimingValue(t => t.TRAS),
            TRFC = TimingValue(t => t.TRFC),
            CommandRate = TimingValue(t => t.CommandRate),
            UncoreText = Pick(current?.UncoreText, report?.UncoreText),
            TCCD = TimingValue(t => t.TCCD),
            TCCDL = TimingValue(t => t.TCCDL),
            TCCDWR = TimingValue(t => t.TCCDWR),
            TCCDWRL = TimingValue(t => t.TCCDWRL),
            HostBridge = Pick(current?.HostBridge, report?.HostBridge),
        };
        var sources = new List<string>();
        if (frequency is not null) sources.Add("頻率：" + Pick(frequency.SourceText));
        if (result.Loaded) sources.Add("時序：" + string.Join("＋", timingSources));
        result.SourceText = sources.Count > 0 ? string.Join("；", sources) : "無";
        result.Status = result.IsJedecReference
            ? "時序僅為 SPD JEDEC 基準，並非目前運行值；頻率依標示來源。"
            : result.Loaded ? "已讀取目前時序；頻率依標示來源。"
            : frequency is not null ? "已取得設定速率；未取得目前運行時序。" : "未讀到記憶體頻率或時序。";
        return result;
    }
}
