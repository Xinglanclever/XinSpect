using System.Management;

namespace XinSpect;

/// <summary>
/// 單顆碟的可靠性計數器原值。<b>來源未提供的欄位一律 null，不以 0 代替</b>——
/// 「0」是合法值（磨損 0%、延遲 0 ms），與「提供者沒給」是兩件不同的事。
/// </summary>
public sealed record StorageReliabilityRaw(
    int DeviceId,
    long? Wear,
    long? Temperature,
    long? TemperatureMax,
    long? PowerOnHours,
    long? StartStopCycleCount,
    long? StartStopCycleCountMax,
    long? LoadUnloadCycleCount,
    long? LoadUnloadCycleCountMax,
    long? ReadErrorsTotal,
    long? ReadErrorsCorrected,
    long? ReadErrorsUncorrected,
    long? WriteErrorsTotal,
    long? WriteErrorsCorrected,
    long? WriteErrorsUncorrected,
    long? ReadLatencyMax,
    long? WriteLatencyMax,
    long? FlushLatencyMax,
    string? ManufactureDate);

/// <summary>
/// 可靠性計數器來源的可注入接縫。形狀照 <see cref="IAcpiTableSource"/>：
/// 可用性＋不可用原因＋資料；真實以 WMI 取，測試注入固定資料、不碰 WMI。
/// </summary>
/// <remarks>
/// <b><see cref="Available"/> 是「最近一次 <see cref="Read"/> 的結果」，不是事前承諾。</b>
/// 真實來源在第一次查詢之前無從得知 WMI 會不會失敗，所以
/// <see cref="StorageReliabilityFactsService.Collect"/> <b>先讀再判</b>——
/// 反過來（先看 <c>Available</c> 再讀）會讓「查詢失敗並帶著原因」掉進
/// 「可用但沒有資料」那一支，把一次失敗講成「不是錯誤」。
/// </remarks>
public interface IStorageReliabilitySource
{
    bool Available { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<StorageReliabilityRaw> Read();
}

/// <summary>
/// 生產來源：<c>root\Microsoft\Windows\Storage</c> 的 <c>MSFT_StorageReliabilityCounter</c>。
/// </summary>
/// <remarks>
/// <b>刻意用 <c>GetRelated</c> 而不是自己組 <c>ASSOCIATORS OF</c> 的 WQL。</b>
/// 這個類別的實例靠關聯取得，而 <c>ObjectId</c> 內含反斜線與引號；
/// 自行組字串會踩到 WQL 跳脫（實測回 0x80041031 屬性無效），
/// 由 WMI 自己做關聯才不會因為一顆碟的 ObjectId 長相不同就整批失敗。
/// </remarks>
public sealed class StorageReliabilityWmiSource : IStorageReliabilitySource
{
    private const string Namespace = @"root\Microsoft\Windows\Storage";

    public bool Available { get; private set; } = true;
    public string? UnavailableReason { get; private set; }

    public IReadOnlyList<StorageReliabilityRaw> Read()
    {
        var rows = new List<StorageReliabilityRaw>();
        try
        {
            var disks = new ManagementObjectSearcher(Namespace, "SELECT * FROM MSFT_PhysicalDisk");
            foreach (ManagementObject disk in disks.Get())
            {
                using (disk)
                {
                    foreach (ManagementObject rel in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                    {
                        using (rel) rows.Add(ReadOne(rel));
                    }
                }
            }
            Available = true;
            UnavailableReason = null;
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason = $"{ex.GetType().Name}：{ex.Message}";
            rows.Clear();
        }
        return rows;
    }

    private static StorageReliabilityRaw ReadOne(ManagementBaseObject o) => new(
        DeviceId: (int)(Num(o, "DeviceId") ?? -1),
        Wear: Num(o, "Wear"),
        Temperature: Num(o, "Temperature"),
        TemperatureMax: Num(o, "TemperatureMax"),
        PowerOnHours: Num(o, "PowerOnHours"),
        StartStopCycleCount: Num(o, "StartStopCycleCount"),
        StartStopCycleCountMax: Num(o, "StartStopCycleCountMax"),
        LoadUnloadCycleCount: Num(o, "LoadUnloadCycleCount"),
        LoadUnloadCycleCountMax: Num(o, "LoadUnloadCycleCountMax"),
        ReadErrorsTotal: Num(o, "ReadErrorsTotal"),
        ReadErrorsCorrected: Num(o, "ReadErrorsCorrected"),
        ReadErrorsUncorrected: Num(o, "ReadErrorsUncorrected"),
        WriteErrorsTotal: Num(o, "WriteErrorsTotal"),
        WriteErrorsCorrected: Num(o, "WriteErrorsCorrected"),
        WriteErrorsUncorrected: Num(o, "WriteErrorsUncorrected"),
        ReadLatencyMax: Num(o, "ReadLatencyMax"),
        WriteLatencyMax: Num(o, "WriteLatencyMax"),
        FlushLatencyMax: Num(o, "FlushLatencyMax"),
        ManufactureDate: o["ManufactureDate"] is string d && d.Length > 0 ? d : null);

    /// <summary>讀一個數值欄位；<b>空值回 null，不回 0</b>——空與 0 是兩件不同的事。</summary>
    private static long? Num(ManagementBaseObject o, string prop)
    {
        try
        {
            object? v = o[prop];
            if (v is null) return null;
            if (v is string s) return s.Length == 0 ? null : (long.TryParse(s, out long p) ? p : null);
            return Convert.ToInt64(v);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;   // 讀得到但解不開＝沒有值，不是 0
        }
    }
}

/// <summary>
/// 儲存裝置可靠性計數器事實（WMI <c>MSFT_StorageReliabilityCounter</c>）。
/// </summary>
/// <remarks>
/// 這是本專案原本沒讀的結構化來源：Windows 自己就有，不必呼叫 smartctl 一類的外部工具。
/// <para>
/// <b>每個欄位各自三態</b>：提供者有給就成一筆事實；沒給就整欄不出現，只在該碟的
/// 「收錄情形」那一筆裡被數出來並列出名稱——「沒提供」與「值是 0」絕不混為一談。
/// </para>
/// </remarks>
public static class StorageReliabilityFactsService
{
    private const string Category = "儲存裝置";

    /// <summary>WMI 類別路徑，逐筆事實的來源欄位用。</summary>
    public const string Source = @"WMI root\Microsoft\Windows\Storage:MSFT_StorageReliabilityCounter";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IStorageReliabilitySource? source = null)
    {
        source ??= new StorageReliabilityWmiSource();
        var facts = new List<HardwareFact>();

        // 先讀再判可用性：真實來源在查詢之前不知道 WMI 會不會失敗，它的 Available 是
        // 「剛剛那次讀取的結果」。若先檢查 Available（預設 true）就會在查詢失敗時
        // 走到下面那一支，把「失敗並帶著原因」講成「讀到了、只是沒有計數器——不是錯誤」。
        IReadOnlyList<StorageReliabilityRaw> rows;
        try
        {
            rows = source.Read();
        }
        catch (Exception ex)
        {
            // 來源自己把例外丟出來也一樣：那是一次讀取失敗，不是「沒有這回事」。
            // 真實來源會自行吞噬並回報，但接縫不該假設每個實作者都那麼做——
            // 這裡吞掉的話，畫面與 CLI 會看到一個空集合，而空集合讀起來像「沒問題」。
            facts.Add(new HardwareFact("storage.reliability.available", Category, "儲存可靠性計數器", "", "",
                Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, $"來源在讀取時擲出 {ex.GetType().Name}：{ex.Message}"));
            return facts;
        }

        if (!source.Available)
        {
            // 來源不可用時不是「沒有這回事」，而是一筆說得出原因的不可用事實。
            facts.Add(new HardwareFact("storage.reliability.available", Category, "儲存可靠性計數器", "", "",
                Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, source.UnavailableReason ?? "來源未提供原因"));
            return facts;
        }

        if (rows.Count == 0)
        {
            facts.Add(new HardwareFact("storage.reliability.available", Category, "儲存可靠性計數器", "", "",
                Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "讀到了來源，但它沒有回報任何計數器——不是錯誤，是本機沒有可用的提供者"));
            return facts;
        }

        facts.Add(new HardwareFact("storage.reliability.count", Category, "可靠性計數器涵蓋磁碟數",
            rows.Count.ToString(), "顆", Source, FactTrustLevel.Reported, false, at, rows.Count));

        foreach (var r in rows) facts.AddRange(ForDisk(at, r));
        return facts;
    }

    private static IEnumerable<HardwareFact> ForDisk(DateTimeOffset at, StorageReliabilityRaw r)
    {
        string disk = $"磁碟 {r.DeviceId}";
        var missing = new List<string>();

        foreach (var (key, name, value, unit) in Fields(r))
        {
            if (value is null) { missing.Add(name); continue; }
            yield return new HardwareFact($"storage.reliability.{r.DeviceId}.{key}", Category, $"{name}（{disk}）",
                value.Value.ToString(), unit, Source, FactTrustLevel.Reported, false, at, value.Value);
        }

        if (r.ManufactureDate is null) missing.Add("製造日期");
        else
            yield return new HardwareFact($"storage.reliability.{r.DeviceId}.manufacture_date", Category,
                $"製造日期（{disk}）", r.ManufactureDate, "", Source, FactTrustLevel.Reported, false, at, null);

        // 收錄情形：把「提供者沒給的欄位」數出來並具名列出。缺不是錯，但不能無聲無息。
        int supplied = Fields(r).Count(f => f.Value is not null) + (r.ManufactureDate is null ? 0 : 1);
        int total = Fields(r).Count() + 1;
        yield return new HardwareFact($"storage.reliability.{r.DeviceId}.coverage", Category,
            $"計數器收錄情形（{disk}）",
            missing.Count == 0 ? $"{supplied}/{total}（提供者全數提供）" : $"{supplied}/{total}；未提供：{string.Join("、", missing)}",
            "", Source, FactTrustLevel.Derived, false, at, supplied,
            missing.Count == 0 ? FactAvailability.Present : FactAvailability.NotApplicable,
            missing.Count == 0 ? null : "這些欄位不是讀取失敗，是提供者沒有提供——空值不代表 0");
    }

    /// <summary>逐欄位的（鍵後綴, 顯示名, 值, 單位）。單位以 WMI 類別的定義為準。</summary>
    private static IEnumerable<(string Key, string Name, long? Value, string Unit)> Fields(StorageReliabilityRaw r)
    {
        yield return ("wear", "磨損程度", r.Wear, "%");
        yield return ("temperature", "溫度", r.Temperature, "°C");
        yield return ("temperature_max", "溫度上限", r.TemperatureMax, "°C");
        yield return ("power_on_hours", "通電時數", r.PowerOnHours, "小時");
        yield return ("start_stop", "啟停次數", r.StartStopCycleCount, "次");
        yield return ("start_stop_max", "啟停次數上限", r.StartStopCycleCountMax, "次");
        yield return ("load_unload", "讀寫頭載卸次數", r.LoadUnloadCycleCount, "次");
        yield return ("load_unload_max", "讀寫頭載卸次數上限", r.LoadUnloadCycleCountMax, "次");
        yield return ("read_errors_total", "讀取錯誤總數", r.ReadErrorsTotal, "次");
        yield return ("read_errors_corrected", "讀取錯誤（已修正）", r.ReadErrorsCorrected, "次");
        yield return ("read_errors_uncorrected", "讀取錯誤（未修正）", r.ReadErrorsUncorrected, "次");
        yield return ("write_errors_total", "寫入錯誤總數", r.WriteErrorsTotal, "次");
        yield return ("write_errors_corrected", "寫入錯誤（已修正）", r.WriteErrorsCorrected, "次");
        yield return ("write_errors_uncorrected", "寫入錯誤（未修正）", r.WriteErrorsUncorrected, "次");
        yield return ("read_latency_max", "最大讀取延遲", r.ReadLatencyMax, "ms");
        yield return ("write_latency_max", "最大寫入延遲", r.WriteLatencyMax, "ms");
        yield return ("flush_latency_max", "最大清除延遲", r.FlushLatencyMax, "ms");
    }
}
