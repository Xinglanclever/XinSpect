namespace XinSpect;

/// <summary>範圍檢查的判定：域內／超域／無規則／非數值。四態而非布林——「沒規則」不等於「沒問題」。</summary>
public enum RangeVerdict
{
    /// <summary>有規則，且值落在物理合理域內。</summary>
    InDomain,
    /// <summary>有規則，但值超出物理合理域——輔助訊號，不是錯誤。</summary>
    OutOfDomain,
    /// <summary>這一條事實沒有數值（文字事實）——範圍檢查不適用。</summary>
    NoNumeric,
    /// <summary>這一條事實沒有登記規則——<b>不代表沒問題</b>，代表還沒有人替它訂域。</summary>
    NoRule,
}

/// <summary>一條物理合理域規則；鍵以字首比對（動態家族的鍵尾由機器決定）。</summary>
public sealed record RangeRule(string KeyPrefix, double Min, double Max, string Unit, string Note);

/// <summary>
/// 事實鍵的物理合理域檢查（Vol 2 批次 C／QS-001）。
/// <para>
/// <b>為什麼要有這一層：</b>解碼錯誤、單位混淆、驅動回預設值——這三種病在畫面上長得跟真讀值一模一樣。
/// 一個「這台機器的處理器 TjMax 是 999 °C」的值不會讓任何程式崩潰，只會安安靜靜地錯很久。
/// 範圍檢查是唯一能自動嗅到那一類錯誤的機制。
/// </para>
/// <para>
/// <b>界線：</b>①域是<b>物理合理</b>域不是正常域——70 °C 在域內不代表健康；
/// ②超出域<b>標 Unknown 而不是錯誤</b>（如實待人工看那一列），也不把超域的事實改寫成 0；
/// ③沒有登記規則的事實回 <see cref="RangeVerdict.NoRule"/>，<b>那不是通過</b>——分母要誠實。
/// </para>
/// </summary>
public static class KeyRangeGuardService
{
    public const string Category = "查詢與智慧層";
    public const string RangeKey = "qs.range";

    private const string Source = "內建物理合理域表";
    private const int ListedMax = 3;

    /// <summary>
    /// 物理合理域表。每一條都要說得出「這個域的物理依據」——沒有依據的域是猜，不是規則。
    /// </summary>
    public static IReadOnlyList<RangeRule> Rules { get; } =
    [
        new("cpu.tjmax", 0, 200, "°C", "處理器接面溫度上限：矽的實際範圍在 60–120 °C，超過 200 一定是解碼錯"),
        new("boot.duration_ms", 0, 3_600_000, "ms", "開機耗時：一小時以上的『開機』不是開機"),
        new("nic.count", 0, 64, "個", "網路介面數：單機數十張已是上限"),
        new("audio.endpoints", 0, 64, "個", "音訊端點數：單機數十個已是上限"),
        new("usb.controllers", 0, 32, "個", "USB 控制器數：xHCI 通常 1–4 個"),
        new("usb.devices", 0, 512, "個", "USB 裝置數：含 hub 展開的裝置總數，數百個已是極限"),
        new("display.hw.gpu", 0, 64, "個", "硬體 GPU 數：多卡工作站數張為極限"),
        new("cam.count", 0, 64, "個", "相機數：內建與外接相機加起來數十個已是上限"),
        new("mon.count", 0, 16, "台", "顯示器數：一台機器接十幾台螢幕已是上限"),
        new("virt.vmcount", 0, 65_536, "台", "虛擬機數：單機上萬台 VM 已是極小機率的上限"),
        new("drvinsp.drivers.count", 0, 100_000, "個", "驅動檔數：Windows 安裝數千，十萬是防呆上限"),
        new("smart.failing_now.count", 0, 256, "顆", "預測失敗中的磁碟數不可能超過實體磁碟數"),
        new("storage.reliability.count", 0, 64, "顆", "有可靠性計數器的磁碟數"),
        new("gpu.retired_pages", 0, 10_000_000, "頁", "GPU 退役頁數：以百萬頁為上限"),
        new("time.drift.ppm", -10_000, 10_000, "ppm", "時鐘漂移：萬分之一以下才是矽振盪器的世界"),
        new("ups.battery.percent", 0, 100, "%", "電量是百分比，0–100 之外是單位錯"),
    ];

    /// <summary>這一條事實的範圍判定；命中規則時由 <paramref name="rule"/> 帶回那一條。</summary>
    public static RangeVerdict Check(HardwareFact fact, out RangeRule? rule)
    {
        rule = Rules.FirstOrDefault(r => fact.Key.StartsWith(r.KeyPrefix, StringComparison.Ordinal));
        if (rule is null) return RangeVerdict.NoRule;
        if (fact.NumericValue is not { } v) return RangeVerdict.NoNumeric;
        return v >= rule.Min && v <= rule.Max ? RangeVerdict.InDomain : RangeVerdict.OutOfDomain;
    }

    /// <summary>對一整批事實做範圍檢查，回一條彙總事實（分母誠實：無規則的也算進去）。</summary>
    public static HardwareFact Collect(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        int inDomain = 0, outOfDomain = 0, noRule = 0, noNumeric = 0;
        var offenders = new List<string>(ListedMax + 1);
        foreach (var f in facts)
        {
            switch (Check(f, out var rule))
            {
                case RangeVerdict.InDomain: inDomain++; break;
                case RangeVerdict.OutOfDomain:
                    outOfDomain++;
                    if (offenders.Count < ListedMax && rule is not null)
                        offenders.Add($"{f.Key}={f.NumericValue:0.##}{rule.Unit}（域 {rule.Min:0.##}–{rule.Max:0.##}）");
                    break;
                case RangeVerdict.NoNumeric: noNumeric++; break;
                default: noRule++; break;
            }
        }

        int checkedCount = inDomain + outOfDomain + noNumeric;
        if (checkedCount == 0)
            return new HardwareFact(RangeKey, Category, "鍵範圍檢查", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "沒有任何事實可以檢查（空集合）——不適用，不是通過");

        string detail = offenders.Count == 0
            ? "沒有超域值"
            : $"超域：{string.Join("、", offenders)}" + (outOfDomain > offenders.Count ? " 等" : "");
        return new HardwareFact(RangeKey, Category, "鍵範圍檢查",
            $"已檢 {checkedCount} 條有事實值：域內 {inDomain} 條、超域 {outOfDomain} 條、無規則 {noRule} 條、非數值 {noNumeric} 條・{detail}。" +
            "域是物理合理域不是健康範圍；超域標 Unknown 待人工看那一列，不改寫成 0；無規則不等於沒問題。",
            "條", Source, FactTrustLevel.Derived, false, at, inDomain,
            outOfDomain > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            outOfDomain > 0
                ? $"{outOfDomain} 條超出物理合理域（{string.Join("、", offenders.Take(ListedMax))}）——可能是解碼或單位問題，如實待確認"
                : null);
    }
}
