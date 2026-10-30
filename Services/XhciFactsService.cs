namespace XinSpect;

/// <summary>
/// xHCI 控制器的一筆來源資料：它的 MMIO 能力區前 N 位元組，或讀不到的原因。
/// </summary>
/// <param name="ControllerId">控制器的 PnP instance ID。</param>
/// <param name="Mmio">能力區位元組（至少 <see cref="XhciCapabilityDecoder.CapabilityBytes"/>）；讀不到為 <c>null</c>。</param>
/// <param name="Why">讀不到時的原因。</param>
/// <param name="BarAddress">從哪個 BAR 位址讀的（供人複核；讀不到時為 <c>null</c>）。</param>
public sealed record XhciControllerSample(
    string ControllerId, byte[]? Mmio, string? Why, ulong? BarAddress = null);

/// <summary>
/// xHCI 控制器版本與支援速度的事實（U10 第三圈 NP-0002）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>「這台機器的 USB 3 控制器是哪一版 xHCI、它自己宣告支援哪幾種速度」。
/// 第 323 輪的 <c>usb.hub.*</c> 回答了「根集線器有幾個埠」，但那裡拿不到<b>版本</b>與
/// <b>支援速度位</b>——那兩個東西只住在控制器 MMIO 裡。
/// </para>
/// <para>
/// <b>與 <c>usb.hub.boundary</c> 的關係：</b>第 323 輪的界線宣告說「控制器版本在 MMIO、
/// usermode 讀不到」。這一輪把「讀得到的時候長什麼樣」的<b>解碼器</b>補上，
/// 並把「這一台讀不讀得到」寫成事實——兩輪合起來才是完整的陳述：
/// <b>能力在，通道不一定在。</b>
/// </para>
/// <para>
/// <b>沒有新增讀取面：</b>MMIO 由呼叫端注入（既有的 <see cref="IMmioReader"/>）。
/// 沒有後端時，本服務只出「讀不到與為什麼」，不猜版本、不從驅動版本反推。
/// </para>
/// </remarks>
public static class XhciFactsService
{
    public const string Category = "週邊匯流排";
    public const string SummaryKey = "usb.xhci.summary";
    public const string BoundaryKey = "usb.xhci.boundary";
    public const string ControllerPrefix = "usb.xhci.ctrl.";

    private const string Source = "xHCI 能力暫存器區（MMIO：CAPLENGTH／HCIVERSION／HCSPARAMS1／HCCPARAMS1）";

    public static IReadOnlyList<HardwareFact> Collect(
        IReadOnlyList<XhciControllerSample> controllers, DateTimeOffset at)
    {
        if (controllers.Count == 0)
            return
            [
                new HardwareFact(SummaryKey, Category, "xHCI 控制器版本與支援速度", "", "個", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "這台機器的裝置清單裡沒有任何 xHCI 控制器——不是「讀不到版本」，是沒有東西可問"),
                BoundaryFact(at),
            ];

        var facts = new List<HardwareFact>();
        int decoded = 0, readable = 0;
        var versions = new List<string>();

        for (int i = 0; i < controllers.Count; i++)
        {
            var c = controllers[i];
            string key = ControllerPrefix + (i + 1).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

            if (c.Mmio is null)
            {
                facts.Add(new HardwareFact(key, Category, $"xHCI 控制器 {i + 1} 版本與支援速度", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                    "控制器能力區在 PCI BAR 的 MMIO 裡，usermode 讀不到"
                    + (c.Why is { } w ? $"（{w}）" : "")
                    + "——**不從驅動版本反推**。這一格缺的是 ring0 的實體記憶體讀取"));
                continue;
            }

            readable++;
            var caps = XhciCapabilityDecoder.ParseCapabilities(c.Mmio);
            if (caps is null)
            {
                facts.Add(new HardwareFact(key, Category, $"xHCI 控制器 {i + 1} 版本與支援速度", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    $"能力區只有 {c.Mmio.Length} 位元組（< {XhciCapabilityDecoder.CapabilityBytes}）——不猜欄位"));
                continue;
            }

            decoded++;
            versions.Add(caps.VersionText);
            var protocols = XhciCapabilityDecoder.ParseSupportedProtocols(c.Mmio, caps);
            facts.Add(ControllerFact(key, i + 1, c, caps, protocols, at));
        }

        facts.Insert(0, SummaryFact(controllers.Count, readable, decoded, versions, at));
        facts.Add(BoundaryFact(at));
        return facts;
    }

    private static HardwareFact SummaryFact(int total, int readable, int decoded, List<string> versions,
        DateTimeOffset at)
    {
        string versionFace = versions.Count == 0
            ? "一個版本都沒讀到"
            : string.Join("、", versions.GroupBy(v => v).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"));
        string value = $"{total} 個 xHCI 控制器・能力區讀得到 {readable} 個、解得開 {decoded} 個"
            + $"・版本：{versionFace}"
            + "・用途：版本決定「這個控制器支不支援某個 xHCI 修訂的行為」，"
            + "但它**不告訴你哪個埠現在跑幾倍速**——那是埠狀態暫存器，本版不讀"
            + "・界線：版本讀不到時**不從驅動版本反推**（驅動版本是作業系統的數字，不是硬體的）";

        bool complete = decoded == total;
        return new HardwareFact(SummaryKey, Category, "xHCI 控制器版本與支援速度", value, "個", Source,
            complete ? FactTrustLevel.Measured : FactTrustLevel.Derived, false, at,
            complete ? decoded : null,
            complete ? FactAvailability.Present : FactAvailability.Unknown,
            complete ? null
                : $"只有 {decoded}/{total} 個控制器解得開；其餘缺 ring0 的 MMIO 存取。總數不完整——已知的只是下限");
    }

    private static HardwareFact ControllerFact(string key, int index, XhciControllerSample c,
        XhciCapabilities caps, IReadOnlyList<XhciProtocol> protocols, DateTimeOffset at)
    {
        string barFace = c.BarAddress is { } bar ? $"BAR 0x{bar:X}" : "BAR 位址未提供";
        string capFace = $"CAPLENGTH 0x{caps.Caplength:X2}・HCIVERSION 0x{caps.HciVersion:X4}（{caps.VersionText}）"
            + $"・最多 {caps.MaxSlots} slots／{caps.MaxInterrupters} interrupters／**{caps.MaxPorts} 個埠**"
            + $"・IST {caps.Ist}・ERST Max {caps.ErstMax}・scratchpad {caps.ScratchpadBuffers}"
            + $"・64 位元定址 {(caps.Ac64 ? "支援" : "不支援")}・context {(caps.Csz ? "64" : "32")} 位元組";

        string protocolFace;
        if (caps.Xecp == 0)
        {
            protocolFace = "**沒有擴充能力串列**（xECP＝0）——這一版控制器不宣告支援協定，所以也列不出速度";
        }
        else if (protocols.Count == 0)
        {
            protocolFace = $"有擴充能力串列（xECP 0x{caps.Xecp:X4}）但**沒有找到支援協定能力**——不猜";
        }
        else
        {
            var parts = new List<string>();
            foreach (var p in protocols)
            {
                string speeds = p.Speeds.Count == 0
                    ? "（PSI 表是空的）"
                    : string.Join("、", p.Speeds.Select(s =>
                        $"PSIV {s.SpeedId}（{s.LinkProtocolName}{(s.FullDuplex ? "、全雙工" : "")}，原值 0x{s.Raw:X8}）"));
                parts.Add($"{p.Name} 最小修訂 {p.MinorRevision}：{speeds}");
            }
            protocolFace = string.Join("；", parts);
        }

        string value = $"控制器 {index}：{barFace}・{capFace}・{protocolFace}"
            + "・界線：這是**控制器自述的支援能力**，不是「現在跑幾倍速」——"
            + "埠的實際速度在埠狀態暫存器，本版不讀；"
            + "速度值（PSIM／PSIS）**本版不換算成 Mb/s**——那兩個欄位的位界在 xHCI 規格表與 Linux 標頭互相矛盾，"
            + "讀不出唯一定義就不編公式，只給原始 DWORD；"
            + "支援速度位是規格宣告，某些晶片會宣告它實際上不支援的組合";

        return new HardwareFact(key, Category, $"xHCI 控制器 {index} 版本與支援速度", value, "", Source,
            FactTrustLevel.Measured, false, at, caps.MaxPorts);
    }

    private static HardwareFact BoundaryFact(DateTimeOffset at) =>
        new(BoundaryKey, Category, "xHCI 能力區的界線宣告",
            "① 能力區在 **PCI BAR 的 MMIO**，usermode 讀不到——沒有 ring0 後端時，"
            + "本版只說「讀不到」與「缺什麼」，**不從驅動版本或 USB 樹推測 xHCI 版本**；"
            + "② 只解能力區（CAPLENGTH／HCIVERSION／HCSPARAMS1／HCSPARAMS2／HCCPARAMS1）"
            + "與「支援協定」擴充能力（連同 PSI 速度表）；**作業暫存器、埠狀態、裝置插槽一律不讀**；"
            + "③ HCSPARAMS3 的解讀延遲（u1/u2）本版不解——單位與晶片實作有出入，"
            + "沒有可核的逐位元出處就不編；"
            + "④ **不寫任何暫存器**：這一整支只讀，沒有 write 路徑；"
            + "⑤ 「幾個埠」在這裡是 HCSPARAMS1 的**控制器自述上限**，"
            + "與 usb.hub.ports.*（根集線器實際回報的埠數）是兩件事——"
            + "前者是主控能管幾個，後者是 hub 說它有幾個；兩者不同不是矛盾，是不同層",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);
}
