using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// AppCompatCache（Shimcache）事實服務（Vol 2 批次 B／FS-001）。
/// <para>
/// 讀 <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache\AppCompatCache</c>
/// 二進位值並交 <see cref="AppCompatCacheDecoder"/> 解碼。登錄檔讀取走
/// <see cref="Registry"/>（唯讀），解析注入式（probe），測試不碰真登錄檔。
/// </para>
/// <para>
/// <b>界線</b>：執行痕跡≠執行成功（條目由系統維護、可能殘留，更不是中毒判決）；
/// 時間是檔案最後修改時間不是執行時間；ControlSet002 的歷史快取本版不讀（只讀 CurrentControlSet，
/// 值裡明說）。路徑可能含使用者名——樣本條目標敏感，匯出預設遮蔽。
/// </para>
/// </summary>
public static class ShimcacheFactsService
{
    public const string Category = "檔案鑑識";
    public const string CountKey = "fs.shim.count";
    public const string LatestKey = "fs.shim.latest";
    public const string SampleKey = "fs.shim.sample";

    /// <summary>登錄值路徑（CurrentControlSet；歷史 ControlSet002 不在自動收集內）。</summary>
    public const string ValuePath = @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache";

    /// <summary>值名稱。</summary>
    public const string ValueName = "AppCompatCache";

    private const string Source = "登錄檔 AppCompatCache（唯讀）＋金標解碼器";

    /// <summary>本機收集。probe 注入式：測試餵合成值，不碰真登錄檔。</summary>
    public static IReadOnlyList<HardwareFact> CollectForThisMachine(DateTimeOffset at, Func<byte[]?>? probe = null) =>
        Collect(at, probe ?? ReadRegistryValue);

    /// <summary>從真登錄檔讀值；讀不到回 null（三態由 <see cref="Collect"/> 誠實處理）。</summary>
    public static byte[]? ReadRegistryValue()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ValuePath);
            return key?.GetValue(ValueName) as byte[];
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Func<byte[]?> probe)
    {
        byte[]? raw;
        string readState = "";
        try
        {
            raw = probe();
        }
        catch (Exception readEx)
        {
            raw = null;
            readState = readEx.GetType().Name;
        }

        if (raw is null)
            return
            [
                new HardwareFact(CountKey, Category, "AppCompatCache（Shimcache）", "", "條", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    readState.Length == 0
                        ? "登錄值不存在（值名 " + ValueName + " 缺席）——可能是空快取；不冒充「沒有執行痕跡」"
                        : "登錄值讀取失敗（" + readState + "）——讀不到不等於沒有"),
            ];

        var parsed = AppCompatCacheDecoder.Parse(raw);
        if (parsed is null)
            return
            [
                new HardwareFact(CountKey, Category, "AppCompatCache（Shimcache）", "", "條", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "登錄值在但格式辨認不出（" + raw.Length + " 位元組）——不猜欄位，不冒充解析完成"),
            ];

        if (parsed.Entries.Count == 0)
            return
            [
                new HardwareFact(CountKey, Category, "AppCompatCache（Shimcache）", parsed.Format + "・收錄 0 條", "條", Source,
                    FactTrustLevel.Derived, false, at, 0.0, FactAvailability.NotSupported,
                    parsed.TruncatedReason ?? "格式辨認出但沒有可解析條目——不適用，不是『沒有執行痕跡』"),
            ];

        var byTime = parsed.Entries.OrderByDescending(e => e.LastModifiedUtc ?? DateTimeOffset.MinValue).ToList();
        var latest = byTime[0];
        string latestText = (latest.LastModifiedUtc is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "時間未知")
            + "｜" + latest.Path + "｜" + (latest.Executed is { } ex ? (ex ? "留有執行旗標" : "無執行旗標") : "執行狀態未記");
        string sample = string.Join("；", byTime.Take(AppCompatCacheDecoder.ListedMax).Select(e =>
                (e.LastModifiedUtc is { } t2 ? t2.ToString("yyyy-MM-dd HH:mm") : "時間未知")
                + "｜" + e.Path + "｜" + (e.Executed is { } ex2 ? (ex2 ? "旗標有" : "旗標無") : "狀態未記")))
            + (byTime.Count > AppCompatCacheDecoder.ListedMax ? "；等 " + (byTime.Count - AppCompatCacheDecoder.ListedMax) + " 條" : "");
        string boundary = "執行痕跡≠執行成功——快取由系統維護可能殘留，條目在不是中毒判決；時間是檔案修改時間不是執行時間；"
            + "只讀 CurrentControlSet（歷史 ControlSet002 不在自動收集內）"
            + (parsed.TruncatedReason is null ? "" : "；" + parsed.TruncatedReason);

        return
        [
            new HardwareFact(CountKey, Category, "AppCompatCache（Shimcache）條目數",
                parsed.Format + "・" + parsed.Entries.Count + " 條" + (parsed.DeclaredCount > 0 && parsed.DeclaredCount != parsed.Entries.Count
                    ? "（表頭申報 " + parsed.DeclaredCount + "，解析 " + parsed.Entries.Count + "）" : "")
                + "・執行痕跡≠執行成功（快取可能殘留，不是中毒判決）",
                "條", Source, FactTrustLevel.Derived, false, at, parsed.Entries.Count),
            new HardwareFact(LatestKey, Category, "AppCompatCache 最新條目", latestText + "・時間是檔案修改時間不是執行時間", "", Source,
                FactTrustLevel.Derived, false, at),
            new HardwareFact(SampleKey, Category, "AppCompatCache 樣本（最新 " + AppCompatCacheDecoder.ListedMax + "）", sample + "・" + boundary, "", Source,
                FactTrustLevel.Derived, true, at),
        ];
    }
}
