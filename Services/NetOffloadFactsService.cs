using System.Management;

namespace XinSpect;

/// <summary>一行 WMI 查詢結果（屬性名→值）；服務層防禦式讀取，屬性缺席視為未知。</summary>
public sealed class WmiRow : Dictionary<string, object>
{
    public object? Get(string property) =>
        TryGetValue(property, out var v) ? v : null;

    public bool? Bool(string property)
    {
        if (!TryGetValue(property, out var v) || v is null) return null;
        try { return Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }
}

/// <summary>
/// 網路卸載狀態事實（V7 WP13／A16 第一層）：Checksum Offload 與 RSS 的<b>實際啟用狀態</b>
/// （root\StandardCimv2 的 MSFT_NetAdapter* 類別）——「驅動宣稱支援」與「Windows 實際開著」是兩件事，
/// 本層報後者。屬性名隨 Windows 版本可能缺席：讀不到如實標「屬性未提供」，不猜。
/// </summary>
public static class NetOffloadFactsService
{
    private const string Category = "網路";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<string, IReadOnlyList<WmiRow>>? wmiQuery = null)
    {
        var query = wmiQuery ?? RealQuery;
        var facts = new List<HardwareFact>();
        AppendChecksumFacts(facts, query, at);
        AppendRssFacts(facts, query, at);
        if (facts.Count == 0)
            return [Unavailable("net.offload", "網路卸載狀態", at,
                FactAvailability.NotSupported, "WMI 查不到 MSFT_NetAdapter 卸載類別（root\\StandardCimv2）——如實標，不推測")];
        return facts;
    }

    private static void AppendChecksumFacts(List<HardwareFact> facts, Func<string, IReadOnlyList<WmiRow>> query, DateTimeOffset at)
    {
        var rows = Safe(query, "SELECT InstanceName, TransmitChecksumOffloadEnabled, TransmitChecksumOffloadSupported, ReceiveChecksumOffloadEnabled, ReceiveChecksumOffloadSupported FROM MSFT_NetAdapterChecksumOffload");
        int i = 0;
        foreach (var row in rows)
        {
            string id = InstanceId(row, i);
            bool? tx = row.Bool("TransmitChecksumOffloadEnabled");
            bool? rx = row.Bool("ReceiveChecksumOffloadEnabled");
            bool? txSup = row.Bool("TransmitChecksumOffloadSupported");
            bool? rxSup = row.Bool("ReceiveChecksumOffloadSupported");
            facts.Add(new HardwareFact($"net.offload.checksum.{i}", Category, $"網路卸載 Checksum（{id}）",
                $"TX：{OnOff(tx, txSup)}・RX：{OnOff(rx, rxSup)}", "",
                "WMI MSFT_NetAdapterChecksumOffload（實際啟用狀態）", FactTrustLevel.Reported, false, at));
            i++;
        }
    }

    private static void AppendRssFacts(List<HardwareFact> facts, Func<string, IReadOnlyList<WmiRow>> query, DateTimeOffset at)
    {
        var rows = Safe(query, "SELECT InstanceName, Enabled FROM MSFT_NetAdapterRss");
        int i = 0;
        foreach (var row in rows)
        {
            string id = InstanceId(row, i);
            bool? rss = row.Bool("Enabled");
            facts.Add(new HardwareFact($"net.offload.rss.{i}", Category, $"網路卸載 RSS（{id}）",
                rss is null ? "Enabled 屬性未提供——此 Windows 版本的類別佈局不同，不猜" : rss.Value ? "啟用" : "停用",
                "", "WMI MSFT_NetAdapterRss（實際啟用狀態）", FactTrustLevel.Reported, false, at));
            i++;
        }
    }

    /// <summary>遮罩與支援旗標的三態呈現：開／停／未知，絕不把「讀不到」畫成「停用」。</summary>
    private static string OnOff(bool? enabled, bool? supported) => (enabled, supported) switch
    {
        (true, _) => "開",
        (false, true) => "停（控制器支援）",
        (false, _) => "停",
        (_, true) => "未啟用（支援）",
        _ => "屬性未提供",
    };

    private static string InstanceId(WmiRow row, int i)
    {
        var name = row.Get("InstanceName") as string;
        return string.IsNullOrWhiteSpace(name) ? $"#{i}" : name.Trim();
    }

    private static IReadOnlyList<WmiRow> Safe(Func<string, IReadOnlyList<WmiRow>> query, string wql)
    {
        try { return query(wql); }
        catch { return []; }
    }

    private static IReadOnlyList<WmiRow> RealQuery(string wql)
    {
        var rows = new List<WmiRow>();
        using var searcher = new ManagementObjectSearcher("root\\StandardCimv2", wql);
        foreach (ManagementObject m in searcher.Get())
        {
            var row = new WmiRow();
            foreach (var p in m.Properties)
                if (p.Value is not null) row[p.Name] = p.Value;
            rows.Add(row);
        }
        return rows;
    }

    private static HardwareFact Unavailable(string key, string name, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", "WMI root\\StandardCimv2", FactTrustLevel.Unknown, false, at,
            null, availability, reason);
}
