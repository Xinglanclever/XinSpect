namespace XinSpect;

/// <summary>
/// U10-TS-0108「刷新率驗證」的資料面：<c>ts.disp.refresh.verify</c>。
/// </summary>
/// <remarks>
/// <para>
/// 驗證邏輯：把<b>目前作用中刷新率</b>（<c>ENUM_CURRENT_SETTINGS</c>）對照驅動宣告的
/// 離散刷新率集合（同一解析度下的模式清單——其來源是驅動從 EDID 與面板能力整理出的集合）。
/// 現值落在集合內＝一致；不在＝OS 正跑一個宣告清單外的值，照實標 Unknown 待仲裁；
/// 現值讀不到＝ReadError。
/// </para>
/// <para>
/// 誠實界線：這裡驗證的是「OS 現況 vs 驅動宣告清單」，不是撕裂計數實測——
/// 後者屬 UI 層（逐模式循環顯示），不在唯讀層。
/// </para>
/// </remarks>
public static class DisplayRefreshVerifyService
{
    private const string Category = "顯示";
    public const string Key = "ts.disp.refresh.verify";
    /// <summary>逐裝置家族前綴（成員＝裝置索引）——名字刻意不含 Key，是家族前綴不是一把鍵。</summary>
    public const string RefreshVerifyPrefix = "ts.disp.refresh.verify.";
    public const string Source = "Windows 顯示驅動模式清單 EnumDisplaySettingsEx（usermode 唯讀）";

    /// <summary>單一顯示裝置的刷新率報告。</summary>
    public sealed record RefreshReport(string DeviceName, int? CurrentRefreshHz, IReadOnlyList<int> DeclaredRefreshHz);

    /// <summary>收集刷新率核對。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<RefreshReport>>? probe = null)
    {
        IReadOnlyList<RefreshReport> reports;
        try { reports = probe is { } p ? p() : RealProbe(); }
        catch (Exception ex)
        {
            return [new HardwareFact(Key, Category, "刷新率核對", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "刷新率列舉失敗：" + ex.Message)];
        }
        if (reports.Count == 0)
            return [new HardwareFact(Key, Category, "刷新率核對", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "列舉不到任何顯示裝置（無作用中桌面）")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            var (value, availability, reason) = Judge(r);
            facts.Add(new HardwareFact(reports.Count == 1 ? Key : $"{RefreshVerifyPrefix}{i}", Category,
                $"刷新率核對：{r.DeviceName}", value, "Hz", Source,
                FactTrustLevel.Reported, false, at,
                availability == FactAvailability.Present ? r.CurrentRefreshHz : null,
                availability, reason));
        }
        return facts;
    }

    /// <summary>純函式：現值 vs 宣告集合 → 判讀。</summary>
    public static (string Value, FactAvailability Availability, string? UnavailableReason) Judge(RefreshReport r)
    {
        if (r.CurrentRefreshHz is not { } current)
            return ("", FactAvailability.ReadError, "ENUM_CURRENT_SETTINGS 未回傳目前刷新率");
        if (r.DeclaredRefreshHz.Count == 0)
            return ("", FactAvailability.Unknown, $"目前 {current} Hz，但驅動宣告清單為空——無從核對，不宣稱一致");
        string declared = string.Join("/", r.DeclaredRefreshHz.OrderBy(hz => hz));
        if (r.DeclaredRefreshHz.Contains(current))
            return ($"目前 {current} Hz，在驅動宣告集合 {declared} Hz 內——一致",
                FactAvailability.Present, null);
        return ($"目前 {current} Hz 不在驅動宣告集合 {declared} Hz 內——OS 正跑宣告清單外的值",
            FactAvailability.Unknown, "現況與宣告清單不一致，來源仲裁未定，不得當已確認值使用");
    }

    private static IReadOnlyList<RefreshReport> RealProbe()
    {
        var reports = new List<RefreshReport>();
        foreach (var device in DisplayModeListService.RealProbe())
        {
            // 目前刷新率：ENUM_CURRENT_SETTINGS 直接問驅動現值；
            // 宣告集合：同一解析度下模式清單的全部離散刷新率。
            var (currentW, currentH, currentHz) = ReadCurrentSettings(device.DeviceName);
            var declared = currentW > 0
                ? device.Modes
                    .Where(m => m.Width == currentW && m.Height == currentH && m.RefreshHz > 0)
                    .Select(m => (int)m.RefreshHz).Distinct().ToList()
                : [];
            reports.Add(new RefreshReport(device.DeviceName,
                currentHz > 0 ? (int?)currentHz : null, declared));
        }
        return reports;
    }

    private static (uint Width, uint Height, uint Hz) ReadCurrentSettings(string deviceName)
    {
        var dm = new DisplayModeListService.DEVMODE();
        dm.dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DisplayModeListService.DEVMODE>();
        if (DisplayModeListService.QueryCurrentSettings(deviceName, ref dm) == 0)
            return (0, 0, 0);
        return (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
    }
}
