namespace XinSpect;

/// <summary>
/// 電池健康三件事分列（U10 第三圈 BT-0002／BT-0003／BT-0004，第 316 輪）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要拆：</b>既有的 <c>pw.battery.summary</c> 把「設計容量、滿充、循環、健康%」
/// 壓成一句話，而其中兩件事在誠實上是錯的——①循環次數沒有回報時被寫成「循環 0」，
/// 那是把「沒揭露」講成「0 次」；②「健康%」與「損耗%」是兩個方向相反的數字，
/// 不寫公式就變成兩個人算出兩個值。這裡把三個值分開，並把公式寫在事實值裡。
/// </para>
/// <para>
/// <b>三態：</b>沒有電池＝<c>NotApplicable</c>（桌機不適用，不是讀不到）；
/// 有電池但某欄沒回報＝<c>Unknown</c> 並寫明是哪一欄；
/// 讀取本身失敗＝<c>ReadError</c>。<b>「沒回報」永遠不會被寫成 0。</b>
/// </para>
/// <para>
/// 純函式：輸入是一筆樣本，這支不碰 WMI，所以測試可以餵任意組合。
/// </para>
/// </remarks>
public static class BatteryHealthFactsService
{
    /// <summary>設計容量（mWh）。</summary>
    public const string DesignKey = "pw.battery.design_mwh";

    /// <summary>滿充容量（mWh）。</summary>
    public const string FullKey = "pw.battery.full_mwh";

    /// <summary>損耗率（%）：公式寫在事實值裡。</summary>
    public const string WearKey = "pw.battery.wear_pct";

    /// <summary>循環次數（次）。</summary>
    public const string CycleKey = "pw.battery.cycle_count";

    private const string Category = "電源";
    private const string Source = "root\\WMI BatteryStaticData／BatteryFullChargedCapacity／BatteryCycleCount（唯讀）";

    /// <summary>
    /// 一筆電池樣本：顆數（0＝沒有電池）、以及三個可能沒回報的欄位。
    /// 欄位用可空型別，因為「沒回報」與「回報 0」是不同的斷言。
    /// </summary>
    public sealed record Sample(int Batteries, long? DesignMwh, long? FullMwh, long? CycleCount);

    /// <summary>把樣本攤成四把事實。</summary>
    /// <param name="sample"><c>null</c> 代表讀取本身失敗。</param>
    /// <param name="at">量測時刻。</param>
    /// <param name="readFailureReason">讀取失敗時寫進每一筆的理由（讀取端知道細節，事實層不編）。</param>
    public static IReadOnlyList<HardwareFact> Collect(Sample? sample, DateTimeOffset at, string? readFailureReason = null)
    {
        if (sample is null)
        {
            string reason = string.IsNullOrWhiteSpace(readFailureReason)
                ? "讀取電池資訊失敗——不是「沒有電池」，也不是 0。"
                : "讀取電池資訊失敗（不是「沒有電池」，也不是 0）：" + readFailureReason;
            return
            [
                Unavailable(DesignKey, "電池設計容量", "", "mWh", at, FactAvailability.ReadError, reason, FactTrustLevel.Unknown),
                Unavailable(FullKey, "電池滿充容量", "", "mWh", at, FactAvailability.ReadError, reason, FactTrustLevel.Unknown),
                Unavailable(WearKey, "電池損耗", "", "%", at, FactAvailability.ReadError, reason, FactTrustLevel.Unknown),
                Unavailable(CycleKey, "電池循環次數", "", "次", at, FactAvailability.ReadError, reason, FactTrustLevel.Unknown),
            ];
        }

        if (sample.Batteries == 0)
        {
            const string reason = "這台沒有電池（桌機／伺服器）——不適用，不是讀不到。";
            return
            [
                Unavailable(DesignKey, "電池設計容量", "", "mWh", at, FactAvailability.NotApplicable, reason, FactTrustLevel.Unknown),
                Unavailable(FullKey, "電池滿充容量", "", "mWh", at, FactAvailability.NotApplicable, reason, FactTrustLevel.Unknown),
                Unavailable(WearKey, "電池損耗", "", "%", at, FactAvailability.NotApplicable, reason, FactTrustLevel.Unknown),
                Unavailable(CycleKey, "電池循環次數", "", "次", at, FactAvailability.NotApplicable, reason, FactTrustLevel.Unknown),
            ];
        }

        return
        [
            CapacityFact(DesignKey, "電池設計容量", sample.DesignMwh, at),
            CapacityFact(FullKey, "電池滿充容量", sample.FullMwh, at),
            WearFact(sample, at),
            CycleFact(sample, at),
        ];
    }

    private static HardwareFact CapacityFact(string key, string name, long? value, DateTimeOffset at)
    {
        if (value is not { } mwh)
        {
            return Unavailable(key, name, "", "mWh", at, FactAvailability.Unknown,
                "電池存在，但這一欄沒有回報（不是 0 mWh）——部分機型的 ACPI 不提供。", FactTrustLevel.Unknown);
        }

        return new HardwareFact(key, name, name, $"{mwh:N0} mWh", "mWh", Source,
            FactTrustLevel.Reported, false, at, mwh, FactAvailability.Present, null);
    }

    private static HardwareFact WearFact(Sample sample, DateTimeOffset at)
    {
        if (sample.DesignMwh is not { } design || sample.FullMwh is not { } full)
        {
            return Unavailable(WearKey, "電池損耗", "", "%", at, FactAvailability.Unknown,
                "缺設計容量或滿充容量，算不出損耗——不給 0%。", FactTrustLevel.Unknown);
        }

        // 公式寫在值裡：損耗 = (1 − 滿充 ÷ 設計) × 100，四捨五入到小數 1 位。
        double raw = (1.0 - (double)full / design) * 100.0;
        bool aboveDesign = raw < 0;
        double wear = Math.Round(aboveDesign ? 0 : raw, 1, MidpointRounding.AwayFromZero);

        string value = aboveDesign
            ? $"0%（滿充 {full:N0} mWh 高於設計 {design:N0} mWh：量測值高於設計值，損耗記 0 並在此註明）"
            : $"{wear:0.0}%（公式：(1 − 滿充 {full:N0} ÷ 設計 {design:N0}) × 100，四捨五入到小數 1 位）";

        return new HardwareFact(WearKey, Category, "電池損耗", value, "%", Source,
            FactTrustLevel.Derived, false, at, wear, FactAvailability.Present, null);
    }

    private static HardwareFact CycleFact(Sample sample, DateTimeOffset at)
    {
        if (sample.CycleCount is not { } cycles)
        {
            return Unavailable(CycleKey, "電池循環次數", "", "次", at, FactAvailability.Unknown,
                "電池存在，但沒有回報循環次數——「沒揭露」不等於「0 次」。", FactTrustLevel.Unknown);
        }

        return new HardwareFact(CycleKey, Category, "電池循環次數", $"{cycles:N0} 次", "次", Source,
            FactTrustLevel.Reported, false, at, cycles, FactAvailability.Present, null);
    }

    private static HardwareFact Unavailable(string key, string name, string value, string unit,
        DateTimeOffset at, FactAvailability availability, string reason, FactTrustLevel trust)
        => new(key, Category, name, value, unit, Source, trust, false, at, null, availability, reason);
}
