using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// 熱管理可讀項盤點（ULTIMATE-1000 階段二 P2-094）：<b>哪些口徑讀得到溫度，各讀到幾項</b>。
///
/// <para>
/// <b>這一項問的是「有哪些路」，不是「溫度是幾度」。</b>本機有三條互不取代的口徑，
/// 而它們在同一台機器上經常對不起來：
/// </para>
/// <list type="number">
///   <item><b>韌體自述</b>：SMBIOS Type 28（溫度探針）與 Type 29（冷卻裝置）——
///     那是主機板廠在生產時填的字串與值，與 OS 無關。</item>
///   <item><b>OS 端 ACPI 熱區</b>：WMI <c>root\WMI</c> 的 <c>MSAcpi_ThermalZoneTemperature</c>——
///     作業系統依 ACPI <c>_TZ</c> 物件建立，值以 <b>0.1 K</b> 表示。</item>
///   <item><b>EC（嵌入式控制器）</b>：<b>本專案沒有直接讀 EC 的通路。</b>
///     這一條要說出來，因為「沒有這一項」與「這一項是 0」在畫面上長得一樣，
///     而它們是完全不同的斷言。<b>那不是「EC 沒有資料」，是「我們沒有那條路」。</b></item>
/// </list>
///
/// <para>
/// <b>界線一：0 不是溫度。</b>熱區回 <c>0</c> 代表驅動沒填（不少平台的實作就是這樣），
/// 0 K 是物理上不可達的值——所以值為 0 的筆數單獨列出，<b>不計入最高溫、也不當成 0 °C</b>。
/// </para>
///
/// <para>
/// <b>界線二：解不開的探針要算得出來。</b>Type 28 未達 0x14、Type 29 未達 0x0C 的結構
/// （<see cref="SmbiosService"/> 的解碼門檻）不進明細，但<b>要能被數出來</b>——
/// 「韌體列了 8 個、我們解開 6 個」與「韌體只列了 6 個」是不同的話。
/// </para>
///
/// <para>
/// <b>界線三：一條路失敗不代表另一條也失敗。</b>熱區查詢在沒有提權時常回權限不足，
/// 而 SMBIOS 照樣讀得到——兩邊的失敗分開陳述；只有<b>兩邊都失敗</b>時整條事實才是讀取錯誤。
/// </para>
/// </summary>
public static class ThermalReachFactsService
{
    private const string Category = "平台";
    private const string Source = "SMBIOS Type 28／29（韌體自述）＋WMI root\\WMI MSAcpi_ThermalZoneTemperature（OS 端 ACPI 熱區）";

    public const string Key = "ec.thermal.readable";

    /// <summary>
    /// 三條口徑的讀取結果。<paramref name="FirmwareFailure"/>／<paramref name="WmiFailure"/> 為
    /// <c>null</c> 代表那條路查成功（含「查成功但 0 項」）。<b>失敗與 0 項是不同的斷言。</b>
    /// </summary>
    public sealed record ThermalReachState(
        int FirmwareProbes, int FirmwareProbesUndecodable,
        int FirmwareCooling, int FirmwareCoolingUndecodable,
        int WmiZones, int WmiUnreported, double? WmiWarmestCelsius,
        string? FirmwareFailure = null, string? WmiFailure = null);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<ThermalReachState?>? probe = null)
        => [ReachFact(at, probe)];

    public static HardwareFact ReachFact(DateTimeOffset at, Func<ThermalReachState?>? probe = null)
    {
        ThermalReachState? s;
        try { s = (probe ?? new Func<ThermalReachState?>(ReadState))(); }
        catch (Exception ex) { s = new ThermalReachState(0, 0, 0, 0, 0, 0, null, "SMBIOS 讀取失敗：" + ex.Message, null); }

        if (s is null || (s.FirmwareFailure is not null && s.WmiFailure is not null))
            return new HardwareFact(Key, Category, "熱管理可讀項盤點", "", "項", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "**兩條路都不通**（"
                + (s?.FirmwareFailure ?? "備援探針回了 null") + "；"
                + (s?.WmiFailure ?? "WMI 熱區查詢失敗")
                + "）——**「讀不到」與「這台機器沒有溫度感測器」是不同的斷言**，"
                + "把前者說成後者會讓一次失敗看起來像一個硬體事實");

        int probesOk = Math.Max(0, s.FirmwareProbes - s.FirmwareProbesUndecodable);
        int coolingOk = Math.Max(0, s.FirmwareCooling - s.FirmwareCoolingUndecodable);
        int readable = probesOk + coolingOk + s.WmiZones;
        return new HardwareFact(Key, Category, "熱管理可讀項盤點",
            Value(s, probesOk, coolingOk), "項", Source, FactTrustLevel.Measured, false, at,
            readable, FactAvailability.Present, Note(s));
    }

    private static string Value(ThermalReachState s, int probesOk, int coolingOk)
    {
        string firmware = s.FirmwareFailure is null
            ? $"SMBIOS Type 28 溫度探針 {s.FirmwareProbes} 個"
              + (s.FirmwareProbesUndecodable > 0 ? $"（其中 {s.FirmwareProbesUndecodable} 個結構長度不足、解不開）" : "")
              + $"、Type 29 冷卻裝置 {s.FirmwareCooling} 個"
              + (s.FirmwareCoolingUndecodable > 0 ? $"（其中 {s.FirmwareCoolingUndecodable} 個解不開）" : "")
            : $"**讀取失敗（{s.FirmwareFailure}）**，缺源如實列";

        string wmi;
        if (s.WmiFailure is not null)
            wmi = $"**查詢失敗（{s.WmiFailure}）**，缺源如實列";
        else if (s.WmiZones == 0)
            wmi = "查到了、但這台機器沒有回報任何熱區（**查證後的「沒有」，不是讀不到**）";
        else
            wmi = $"{s.WmiZones} 個熱區"
                + (s.WmiUnreported > 0 ? $"（其中 {s.WmiUnreported} 個回 0＝未回報，**不當成 0 °C**）" : "")
                + (s.WmiWarmestCelsius is { } w
                    ? $"、最高 {w.ToString("0.0", CultureInfo.InvariantCulture)} °C"
                    : "、沒有任何一個回報了可用的值");

        return "三條口徑分開講，**不互相取代**："
            + $"①**韌體自述**——{firmware}；"
            + $"②**OS 端 ACPI 熱區**（WMI）——{wmi}；"
            + "③**EC（嵌入式控制器）**——**本專案沒有直接讀 EC 的通路**："
            + "這一項講的不是「EC 沒有資料」，而是「我們沒有那條路」。"
            + $"（可讀項合計 {probesOk + coolingOk + s.WmiZones} 項）";
    }

    private static string Note(ThermalReachState s) =>
        "**這一項盤點的是「哪些讀得到」，不是溫度本身。**"
        + "單位：熱區值以 0.1 K 表示，換算攝氏要減 273.15——回 0 代表驅動未填，0 K 不是溫度。"
        + "界線：韌體探針與 OS 熱區常在同一台機器上對不起來，那是兩套口徑，"
        + "**並列陳述不互相取代**；要判讀溫度是否異常請看各自的量測事實。"
        + (s.FirmwareFailure is not null || s.WmiFailure is not null
            ? "有一條路這次沒讀到——**那一條標缺源，其他兩條照常陳述**（一條失敗不代表另一條也失敗）。"
            : "");

    /// <summary>
    /// 真實讀取：SMBIOS 那半邊直接重用 <see cref="SmbiosService"/>（同一次讀表，不為此再讀一次），
    /// WMI 那半邊只查 <c>CurrentTemperature</c> 一個欄位。
    /// </summary>
    private static ThermalReachState ReadState()
    {
        int p28 = 0, p28Bad = 0, c29 = 0, c29Bad = 0;
        string? firmwareFailure = null;
        try
        {
            var structs = new SmbiosService().Structs;
            foreach (var t in structs.Where(t => t.Type == 28))
            { p28++; if (t.Length < 0x14) p28Bad++; }   // 解碼門檻同 SmbiosService.DecodeTemperatureProbe
            foreach (var t in structs.Where(t => t.Type == 29))
            { c29++; if (t.Length < 0x0C) c29Bad++; }    // 同 SmbiosService.DecodeCoolingDevice
        }
        catch (Exception ex) { firmwareFailure = ex.Message; }

        int zones = 0, unreported = 0;
        double? warmest = null;
        string? wmiFailure = null;
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var rows = searcher.Get();
            foreach (ManagementObject o in rows)
            {
                using (o)
                {
                    zones++;
                    uint raw = Convert.ToUInt32(o["CurrentTemperature"] ?? 0u, CultureInfo.InvariantCulture);
                    if (raw == 0) { unreported++; continue; }   // 0 K 不是溫度：驅動未填
                    double celsius = raw / 10.0 - 273.15;
                    warmest = warmest is null ? celsius : Math.Max(warmest.Value, celsius);
                }
            }
        }
        catch (UnauthorizedAccessException) { wmiFailure = "權限不足（ACPI 熱區通常需要提權）"; }
        catch (ManagementException ex)
        {
            // 「平台不支援」與「查詢失敗」是不同的斷言（實測這台機器回前者）。
            // 訊息文字是本地化的（繁中「不受支援」／英文 "Not supported"），所以兩種都認。
            bool unsupported = ex.Message.Contains("不受支援", StringComparison.Ordinal)
                            || ex.Message.Contains("Not supported", StringComparison.OrdinalIgnoreCase);
            wmiFailure = unsupported
                ? "平台不支援（WMI 回「不受支援」——這台機器沒有可用的 ACPI 熱區物件，與權限不足不同）"
                : "WMI 查詢失敗：" + ex.Message;
        }
        catch (Exception ex) { wmiFailure = ex.Message; }

        return new ThermalReachState(p28, p28Bad, c29, c29Bad, zones, unreported, warmest,
            firmwareFailure, wmiFailure);
    }
}
