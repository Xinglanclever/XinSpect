using System;
using System.Collections.Generic;
using System.Management;

namespace XinSpect;

/// <summary>
/// 網卡落差的<b>讀取</b>層：把兩條鏈路（PCIe 匯流排、乙太網路線路）的實測值湊成
/// <see cref="NicLinkSample"/>，交給純解碼器 <see cref="NicLinkGap"/> 判讀。
/// </summary>
/// <remarks>
/// <para>
/// 三個來源，全部唯讀、usermode 零特權：
/// ① <c>MSFT_NetAdapter</c>（root\StandardCimv2）——介面名稱、描述、狀態、媒體類型、目前線路速率；
/// ② <c>MSFT_NetAdapterAdvancedPropertySettingData</c>——驅動回報的 <c>*SpeedDuplex</c> 可選值，
/// 也就是「這張卡支援哪些速率」的權威來源；
/// ③ <c>MSFT_NetAdapterHardwareInfoSettingData</c>——PCIe 目前／能力速度與寬度（<c>*SpeedEncoded</c>，已是速度代碼）。
/// </para>
/// <para>
/// <b>為什麼不用 PCI 設定空間自己讀：</b>同一份資料 WinRing0 路徑也讀得到（見 <see cref="PcieLinkService"/>），
/// 但那條路要核心驅動；這裡用 Windows 已經收集好的值，零特權、與裝置管理員顯示的一致。
/// 兩條路徑的差異在報告中如實註明來源，不混為一談。
/// </para>
/// </remarks>
public static class NicLinkFactsService
{
    private const string Category = "網路";
    private const string Scope = @"root\StandardCimv2";

    /// <summary>收集每張網卡的落差事實。測試以注入樣本取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<NicLinkSample>>? probe = null)
    {
        IReadOnlyList<NicLinkSample> samples;
        try { samples = (probe ?? ReadAll)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact("nic.gap", Category, "網卡鏈路落差", "", "",
                    "WMI root\\StandardCimv2", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "讀取失敗：" + ex.Message),
            ];
        }

        if (samples.Count == 0)
            return
            [
                new HardwareFact("nic.gap", Category, "網卡鏈路落差", "", "",
                    "WMI root\\StandardCimv2", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError,
                    "沒有回報任何網路介面——讀不到就是不猜（可能是 WMI 未回應）"),
            ];

        var list = new List<HardwareFact>();
        foreach (var s in samples)
        {
            var v = NicLinkGap.Judge(s);
            list.Add(new HardwareFact($"nic.gap.{s.Name}", Category, $"鏈路落差（{s.Name}）",
                v.Headline, "", "WMI MSFT_NetAdapter ＋ MSFT_NetAdapterHardwareInfoSettingData",
                FactTrustLevel.Measured, false, at, null,
                v.Kind == NicLinkGap.GapKind.Unknown
                    ? FactAvailability.NotApplicable : FactAvailability.Present,
                v.Kind == NicLinkGap.GapKind.Unknown ? v.Evidence : null));
            list.Add(new HardwareFact($"nic.gap.evidence.{s.Name}", Category, $"鏈路依據（{s.Name}）",
                v.Evidence, "", "WMI MSFT_NetAdapter ＋ MSFT_NetAdapterHardwareInfoSettingData",
                FactTrustLevel.Derived, false, at, null, FactAvailability.Present));
        }
        return list;
    }

    // ── 生產探測（唯讀 WMI）──

    internal static IReadOnlyList<NicLinkSample> ReadAll()
    {
        var linkSpeed = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var desc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var up = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var media = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var s = new ManagementObjectSearcher(Scope,
                "SELECT Name, InterfaceDescription, LinkSpeed, Speed, MediaType, MediaConnectionState "
                + "FROM MSFT_NetAdapter");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = Str(o, "Name");
                    if (name.Length == 0) continue;
                    desc[name] = Str(o, "InterfaceDescription");
                    media[name] = Str(o, "MediaType");
                    up[name] = Str(o, "MediaConnectionState")
                        .Equals("Connected", StringComparison.OrdinalIgnoreCase);
                    linkSpeed[name] = o["Speed"] is { } v ? Convert.ToUInt64(v) : 0;
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("網卡基本資料查詢", ex, "網卡落差整組視為讀不到");
            return [];
        }

        var supported = new Dictionary<string, List<ulong>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher(Scope,
                "SELECT Name, RegistryKeyword, ValidDisplayValues FROM MSFT_NetAdapterAdvancedPropertySettingData "
                + "WHERE RegistryKeyword = '*SpeedDuplex'");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = Str(o, "Name");
                    if (name.Length == 0) continue;
                    if (o["ValidDisplayValues"] is not Array vals) continue;
                    var list = new List<ulong>();
                    foreach (object? item in vals)
                        if (item is string text && ParseSpeed(text) is { } bps && bps > 0)
                            list.Add(bps);
                    list.Sort();
                    supported[name] = list;
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("網卡支援速率查詢", ex, "支援速率清單留空——不影響 PCIe 段落");
        }

        // 類別名是 MSFT_NetAdapterHardwareInfoSettingData（不是 MSFT_NetAdapterHardwareInfo——後者不存在，
        // 用了會整個查詢失敗而靜靜留空）。速度欄位是 *SpeedEncoded：回傳的<b>已經是 PCIe 速度代碼</b>
        // （1＝2.5、2＝5、3＝8、4＝16 GT/s…），與 Link Capabilities 的編碼同源，可直接餵 PcieLink。
        // 另外兩個同名的 PciExpressCurrentLinkSpeed／PciExpressMaxLinkSpeed 在本機是空的，
        // 只有 *Encoded 有值——這是要用 Encoded 的原因。
        var pcie = new Dictionary<string, (int CurSp, int CurW, int MaxSp, int MaxW)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher(Scope,
                "SELECT Name, PciExpressCurrentLinkSpeedEncoded, PciExpressCurrentLinkWidth, "
                + "PciExpressMaxLinkSpeedEncoded, PciExpressMaxLinkWidth "
                + "FROM MSFT_NetAdapterHardwareInfoSettingData");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = Str(o, "Name");
                    if (name.Length == 0) continue;
                    pcie[name] = (Int(o, "PciExpressCurrentLinkSpeedEncoded"),
                                  Int(o, "PciExpressCurrentLinkWidth"),
                                  Int(o, "PciExpressMaxLinkSpeedEncoded"),
                                  Int(o, "PciExpressMaxLinkWidth"));
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("網卡 PCIe 硬體資訊查詢", ex, "PCIe 段落留空——不影響線路段落");
        }

        var result = new List<NicLinkSample>();
        foreach (string name in desc.Keys)
        {
            var (cs, cw, ms, mw) = pcie.TryGetValue(name, out var p) ? p : (0, 0, 0, 0);
            result.Add(new NicLinkSample(
                name,
                desc[name],
                up.TryGetValue(name, out bool u) && u,
                media.TryGetValue(name, out var m) ? m : "",
                cs, cw, ms, mw,
                linkSpeed.TryGetValue(name, out ulong bps) ? bps : 0,
                supported.TryGetValue(name, out var sup) ? sup : []));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>「10 Gbps」／「2.5 Gbps」／「100 Mbps」→ bit/s。認不出來回 null。</summary>
    internal static ulong? ParseSpeed(string text)
    {
        var s = text.Trim();
        int space = s.IndexOf(' ');
        if (space <= 0) return null;
        if (!double.TryParse(s[..space], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n)) return null;
        string unit = s[(space + 1)..].Trim().ToLowerInvariant();
        double mult = unit.StartsWith("gbps") ? 1e9
                    : unit.StartsWith("mbps") ? 1e6
                    : unit.StartsWith("kbps") ? 1e3
                    : 0;
        return mult == 0 ? null : (ulong)(n * mult);
    }

    private static string Str(ManagementObject o, string prop) => o[prop] as string ?? "";
    private static int Int(ManagementObject o, string prop)
        => o[prop] is { } v ? Convert.ToInt32(v) : 0;
    private static double Dbl(ManagementObject o, string prop)
        => o[prop] is { } v ? Convert.ToDouble(v) : 0;
}
