namespace XinSpect;

/// <summary>
/// SMART failing-now 事實（供健康頁與快照）：逐顆磁碟把「現值 ≤ 門檻」的屬性攤開——
/// 這是「現正低於門檻」的直接判讀，比一般化的健康評分更具體；NVMe 走 WCTEMP 對照合成溫度。
/// 讀不到（無 ATA 門檻表支援、NVMe 未提供 WCTEMP）如實三態。
/// </summary>
public static class SmartFailingNowFactsService
{
    private const string Category = "儲存裝置";
    private const int MaxDrives = 32;

    /// <summary>probe＝(ATA 屬性, ATA 門檻表, NVMe 評比)。供測試注入；生產走 StorageSmartService。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<(List<SmartRow> Attrs, Dictionary<byte, byte> Thresholds,
              (StorageSmartService.WctempState State, int ThresholdC, int CompositeC) Wctemp)?>? probe = null,
        string? diskLabel = null)
    {
        string disk = diskLabel ?? "PhysicalDrive";
        if (probe is not null)
        {
            if (probe() is not { } single)
                return [new HardwareFact("smart.failing_now.count", Category, "SMART failing-now", "", "",
                    "SMART READ DATA＋READ THRESHOLDS", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "來源未提供 SMART 資料——沒有可比對的屬性，不以 0 冒充")];
            var (attrs, thresholds, wctemp) = single;
            return CollectOne(at, disk, attrs, thresholds, wctemp);
        }
        var facts = new List<HardwareFact>();
        for (int i = 0; i < MaxDrives; i++)
        {
            uint bus = StorageSmartService.TryGetBusType(i, out string busName);
            if (bus == 0) continue;
            string label = $"PhysicalDrive{i}（{busName}）";

            var attrs = StorageSmartService.TryReadAtaAttributes(i);
            var thresholds = StorageSmartService.TryReadAtaThresholds(i) is { } sec
                ? StorageSmartService.DecodeAtaThresholds(sec)
                : null;
            StorageSmartService.WctempState wState = StorageSmartService.WctempState.NotProvided;
            int wThreshold = 0, wComposite = 0;
            if (bus == 17)
            {
                // NVMe：合成溫度自健康紀錄、WCTEMP 自 Identify Controller
                var health = StorageSmartService.TryReadNvmeHealth(i);
                var idc = StorageSmartService.TryReadNvmeIdentify(i);
                int composite = health?.CompositeTempCelsius ?? 0;
                var verdict = StorageSmartService.EvaluateWctemp(composite, idc ?? []);
                wState = verdict.State; wThreshold = verdict.ThresholdC; wComposite = verdict.CompositeC;
                if (verdict.State == StorageSmartService.WctempState.Warning)
                    facts.Add(new HardwareFact($"smart.wctemp.{i}", Category, $"NVMe 溫度警告（{label}）",
                        $"合成溫度 {composite}°C 已達 WCTEMP {wThreshold}°C——超過廠商警告門檻", "°C",
                        "NVMe Identify Controller 0x14A＋健康紀錄", FactTrustLevel.Derived, false, at, composite));
            }

            if (attrs is null)
            {
                facts.Add(new HardwareFact($"smart.failing_now.{i}", Category, $"SMART failing-now（{label}）", "", "",
                    "SMART READ DATA＋READ THRESHOLDS", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.NotSupported, "屬性表讀不到（NVMe 無 ATA 屬性、或驅動拒絕命令）"));
                continue;
            }
            facts.AddRange(CollectOne(at, label, attrs, thresholds,
                (wState, wThreshold, wComposite), keySuffix: $".{i}"));
        }
        if (facts.Count == 0)
            facts.Add(new HardwareFact("smart.failing_now.count", Category, "SMART failing-now", "", "",
                "SMART READ DATA＋READ THRESHOLDS", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "系統上沒有讀得到 SMART 的磁碟"));
        return facts;
    }

    private static IReadOnlyList<HardwareFact> CollectOne(DateTimeOffset at, string disk,
        IReadOnlyList<SmartRow> attrs, IReadOnlyDictionary<byte, byte>? thresholds,
        (StorageSmartService.WctempState State, int ThresholdC, int CompositeC) wctemp,
        string keySuffix = "")
    {
        var failing = thresholds is null ? new List<StorageSmartService.FailingNowRow>()
            : StorageSmartService.EvaluateFailingNow(attrs, thresholds);
        var facts = new List<HardwareFact>
        {
            new($"smart.failing_now{keySuffix}", Category, $"SMART failing-now（{disk}）",
                thresholds is null ? "門檻表（0xD1）讀不到——不比對門檻，只列屬性現值"
                    : failing.Count == 0
                    ? $"沒有現正低於門檻的屬性（評比 {thresholds.Count(kvp => kvp.Value > 0)} 項有門檻者）"
                    : $"{failing.Count} 項現正低於門檻", "項",
                "SMART READ DATA＋READ THRESHOLDS（0xD1）", FactTrustLevel.Derived, false, at, failing.Count),
        };
        for (int i = 0; i < failing.Count; i++)
        {
            var f = failing[i];
            facts.Add(new HardwareFact($"smart.failing_now{keySuffix}.{i}", Category, $"現正低於門檻 {i}（{disk}）",
                $"{f.Name}：現值 {f.Value} ≤ 門檻 {f.Threshold}——現正低於門檻", "",
                "SMART READ THRESHOLDS 對照 READ DATA 現值", FactTrustLevel.Derived, false, at, f.Value));
        }
        return facts;
    }
}
