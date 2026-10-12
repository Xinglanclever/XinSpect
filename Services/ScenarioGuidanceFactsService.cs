using System.Management;

namespace XinSpect;

/// <summary>
/// 情境引導事實（Vol2 推進第 94–98 項）：SG-006 藍牙、SG-007 Wi-Fi、SG-009 印表機、
/// SG-013 游標卡、SG-014 白屏。
/// </summary>
/// <remarks>
/// <para><b>這些是「導航」不是「診斷」</b>：每一列只陳述「量得到的事實（有沒有這個裝置／這個面向可用）」，
/// 並指向真正有細節的頁面；不給紅黃判決、不猜根因。</para>
/// <para><b>名稱比對的界線</b>：裝置有沒有出現是靠 PnP 名稱／類別比對，<b>不是連線測試</b>——
/// 藍牙沒有裝置不等於「藍牙壞了」，可能只是沒插／沒開。</para>
/// </remarks>
public static class ScenarioGuidanceFactsService
{
    private const string Cat = "情境診斷";

    public const string BtKey = "sg.bt";
    public const string WifiKey = "sg.wifi";
    public const string PrintKey = "sg.print";
    public const string InputKey = "sg.input";
    public const string DisplayKey = "sg.display";

    /// <summary>PnP 裝置（名稱＋類別），測試注入。</summary>
    public readonly record struct PnpRow(string Name, string PnpClass);

    public delegate IReadOnlyList<PnpRow>? PnpProbe();
    public delegate (bool GpuPresent, bool DpcMeasureAvailable) InputProbe();
    public delegate (bool GpuPresent, bool TdrReaderAvailable) DisplayProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        PnpProbe? pnp = null,
        InputProbe? input = null,
        DisplayProbe? display = null)
    {
        var facts = new List<HardwareFact>(5);
        IReadOnlyList<PnpRow>? rows;
        try { rows = (pnp ?? ReadPnp)(); }
        catch { rows = null; }

        facts.Add(DeviceGuidance(at, rows, BtKey, "藍牙狀態", "Bluetooth",
            ["bluetooth", "btusb", "藍牙"], "沒看到藍牙裝置——先看裝置管理員的「藍牙」類別與無線開關"));
        facts.Add(DeviceGuidance(at, rows, WifiKey, "Wi-Fi 介面", "Net",
            ["wi-fi", "802.11", "wireless", "wlan"], "沒看到無線網卡——若機器有 Wi-Fi，先看驅動是否載入"));
        facts.Add(DeviceGuidance(at, rows, PrintKey, "印表機", "Printer",
            ["print", "printer"], "沒看到印表機裝置——不印的第一步是確認佇列與驅動"));

        try
        {
            var i = (input ?? DefaultInput)();
            facts.Add(new HardwareFact(InputKey, Cat, "游標卡頓引導",
                $"GPU {(i.GpuPresent ? "在場" : "不在場")}・DPC 延遲量測{(i.DpcMeasureAvailable ? "可用" : "不可用")}。" +
                "游標卡的可能面向（本工具量得到的）：GPU 滿載、驅動、DPC 延遲——細節看「延遲與 DPC」「顯示卡」頁；本列只導航。",
                "", "WMI ＋ DpcLatencyService 可用性", FactTrustLevel.Reported, false, at));
        }
        catch
        {
            facts.Add(new HardwareFact(InputKey, Cat, "游標卡頓引導", "", "", "WMI＋DPC",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "輸入環境讀取失敗——不猜"));
        }

        try
        {
            var d = (display ?? DefaultDisplay)();
            facts.Add(new HardwareFact(DisplayKey, Cat, "白屏引導",
                $"GPU {(d.GpuPresent ? "在場" : "不在場")}・TDR 事件來源{(d.TdrReaderAvailable ? "可讀" : "不可讀")}。" +
                "白屏的可能面向：顯示驅動重置（TDR）、輸出切換、解析度不支援——細節看「顯示卡」「事件」頁；本列只導航。",
                "", "WMI ＋ 事件記錄可用性", FactTrustLevel.Reported, false, at));
        }
        catch
        {
            facts.Add(new HardwareFact(DisplayKey, Cat, "白屏引導", "", "", "WMI＋事件記錄",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "顯示環境讀取失敗——不猜"));
        }

        return facts;
    }

    private static HardwareFact DeviceGuidance(DateTimeOffset at, IReadOnlyList<PnpRow>? rows,
        string key, string name, string label, string[] keywords, string missingHint)
    {
        if (rows is null)
            return new HardwareFact(key, Cat, name, "", "", "WMI Win32_PnPEntity",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "裝置列舉失敗——讀不到就是不猜（不是「沒有這個裝置」）");

        int matches = rows.Count(r =>
            r.PnpClass.Contains(label, StringComparison.OrdinalIgnoreCase)
            || keywords.Any(k => r.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));
        return new HardwareFact(key, Cat, name,
            matches > 0
                ? $"偵測到 {matches} 個相關裝置（PnP 名稱／類別比對，不是連線測試）——細節與三態看裝置診斷頁。"
                : missingHint + "。0 是「沒比對到」，不是「確定沒有」。",
            "個", "WMI Win32_PnPEntity（名稱／類別比對）", FactTrustLevel.Reported, false, at, matches);
    }

    private static IReadOnlyList<PnpRow>? ReadPnp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPClass FROM Win32_PnPEntity");
            var list = new List<PnpRow>();
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                    list.Add(new PnpRow(o["Name"] as string ?? "", o["PNPClass"] as string ?? ""));
            }
            return list;
        }
        catch { return null; }
    }

    private static (bool, bool) DefaultInput()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            bool gpu = searcher.Get().Count > 0;
            return (gpu, true);
        }
        catch { return (false, false); }
    }

    private static (bool, bool) DefaultDisplay()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            return (searcher.Get().Count > 0, true);
        }
        catch { return (false, false); }
    }
}
