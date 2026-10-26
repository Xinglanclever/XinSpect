using System.Management;

namespace XinSpect;

/// <summary>一個網路介面的統計（彙整前形態）。</summary>
public sealed record NicStatsEntry(string Name, ulong RxErrors, ulong TxErrors, ulong RxDiscards, ulong TxDiscards);

/// <summary>
/// 網路補缺：網卡錯誤／丟棄計數（WMI MSFT_NetAdapterStatistics，usermode 零特權）。
/// 錯誤／丟棄不為零的介面逐條攤開——驅動劣化、線材、交換器埠問題的第一指紋；
/// 全零是「乾淨」不是讀不到。MAC OUI 廠商對照由 <see cref="OuiKnowledge"/> 提供。
/// </summary>
public static class NicHealthFactsService
{
    private const string Category = "週邊匯流排";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<NicStatsEntry>?>? probe = null)
    {
        var adapters = (probe ?? FetchWmi)();
        if (adapters is null)
            return [new HardwareFact("nic.count", Category, "網路介面統計", "", "",
                "WMI root\\StandardCimv2（MSFT_NetAdapterStatistics）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "WMI 查詢失敗——網卡統計讀不到就是不猜")];

        var dirty = adapters.Where(a => a.RxErrors + a.TxErrors + a.RxDiscards + a.TxDiscards > 0).ToList();
        var facts = new List<HardwareFact>
        {
            new("nic.count", Category, "網路介面", adapters.Count.ToString(), "個",
                "WMI root\\StandardCimv2（MSFT_NetAdapterStatistics）", FactTrustLevel.Reported, false, at, adapters.Count),
            new("nic.dirty_count", Category, "有錯誤／丟棄的介面",
                dirty.Count == 0 ? "0 個（全部介面計數歸零——線路與驅動層乾淨）" : $"{dirty.Count} 個", "個",
                "WMI MSFT_NetAdapterStatistics", FactTrustLevel.Derived, false, at, dirty.Count),
        };
        for (int i = 0; i < dirty.Count && i < 8; i++)
        {
            var d = dirty[i];
            facts.Add(new HardwareFact($"nic.dirty.{i}", Category, $"介面 {d.Name}",
                $"{d.Name}：RX 錯誤 {d.RxErrors}・TX 錯誤 {d.TxErrors}・RX 丟棄 {d.RxDiscards}・TX 丟棄 {d.TxDiscards}", "",
                "WMI MSFT_NetAdapterStatistics", FactTrustLevel.Reported, false, at, null));
        }
        return facts;
    }

    /// <summary>MAC 對照的來源字串——三種結果（對照、讀不到、沒有網卡）共用同一個宣稱。</summary>
    private const string MacSource = "WMI Win32_NetworkAdapter＋OUI 知識庫（IEEE 登記前綴的手工子集）";

    /// <summary>MAC 位址 → OUI 廠商（<see cref="OuiKnowledge"/> 知識庫）。每介面一列。</summary>
    public static IReadOnlyList<HardwareFact> CollectMacVendors(DateTimeOffset at,
        Func<IReadOnlyList<(string Name, string Mac)>?>? probe = null)
    {
        var adapters = (probe ?? FetchAdaptersWithMac)();
        if (adapters is null)
            return [new HardwareFact("nic.mac.0", Category, "MAC 廠商對照", "", "",
                MacSource, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "WMI 查詢失敗——MAC 對照讀不到就是不猜")];
        var facts = new List<HardwareFact>();
        for (int i = 0; i < adapters.Count && i < 10; i++)
        {
            var (name, mac) = adapters[i];
            // 第 285 輪：值末尾帶固定的名稱出處標記（`OuiKnowledge.Describe`）。
            // 查詢統計靠那一段分類，不再比對「值裡有沒有出現某句中文」——
            // 舊寫法在 WMI 失敗時把空字串算成命中。
            facts.Add(new HardwareFact($"nic.mac.{i}", Category, $"MAC {name}",
                OuiKnowledge.Describe(mac), "", MacSource, FactTrustLevel.Reported, false, at, null));
        }
        if (adapters.Count == 0)
            // 這台機器沒有實體網卡：**沒有東西可查**，不是查不到。
            // 先前標 Present 會讓回收端把它算成一次「無法解析」的查詢。
            facts.Add(new HardwareFact("nic.mac.0", Category, "MAC 廠商對照", "沒有實體網路介面", "",
                MacSource, FactTrustLevel.Reported, false, at, null,
                FactAvailability.NotApplicable, "沒有任何實體網路介面——這一輪沒有 MAC 可以對照"));
        return facts;
    }

    private static IReadOnlyList<(string, string)>? FetchAdaptersWithMac()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT NetConnectionID, MACAddress FROM Win32_NetworkAdapter WHERE MACAddress IS NOT NULL AND PhysicalAdapter = True");
            var list = new List<(string, string)>();
            foreach (var m in searcher.Get())
            {
                string? mac = m["MACAddress"]?.ToString();
                string? name = m["NetConnectionID"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(mac)) list.Add((name, mac));
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>WMI 通路（極薄）：失敗回 null。</summary>
    public static IReadOnlyList<NicStatsEntry>? FetchWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\StandardCimv2", "SELECT Name, ReceivedErrors, SentErrors, ReceivedDiscards, SentDiscards FROM MSFT_NetAdapterStatistics");
            var list = new List<NicStatsEntry>();
            foreach (var m in searcher.Get())
            {
                string? name = m["Name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new NicStatsEntry(name,
                    ToUlong(m["ReceivedErrors"]), ToUlong(m["SentErrors"]),
                    ToUlong(m["ReceivedDiscards"]), ToUlong(m["SentDiscards"])));
            }
            return list;
        }
        catch { return null; }
    }

    private static ulong ToUlong(object? v)
    {
        try { return v is null ? 0 : Convert.ToUInt64(v); }
        catch { return 0; }
    }
}
