namespace XinSpect;

/// <summary>一段被指派給裝置的記憶體視窗（PCI BAR 在系統位址空間裡的落點）。</summary>
public readonly record struct BarRange(ulong Base, ulong End)
{
    /// <summary>視窗大小（位元組）。描述元給的是起訖位址，兩端都含在內。</summary>
    public ulong Bytes => End >= Base ? End - Base + 1 : 0;

    public string RangeText => $"0x{Base:X} – 0x{End:X}";
    public string SizeText => ResizableBarDecoder.SizeText(Bytes);
}

/// <summary>一張顯示卡的視窗盤點結果。</summary>
public sealed class BarDeviceRow
{
    public required string Name { get; init; }
    public required string Location { get; init; }
    /// <summary>被指派到的所有記憶體視窗，由大到小。</summary>
    public required IReadOnlyList<BarRange> Ranges { get; init; }
    /// <summary>顯示記憶體總量（位元組）；0 ＝ 讀不到。</summary>
    public ulong VramBytes { get; init; }
    /// <summary>
    /// 是不是真的 PCI 裝置。虛擬顯示卡（模擬器、串流、遠端桌面裝出來的那些）也算顯示卡類別，
    /// 但它們沒有 BAR 也沒有顯示記憶體，對它們談 ReBAR 沒有意義。
    /// </summary>
    public bool IsPci { get; init; }

    public ulong LargestBytes => Ranges.Count == 0 ? 0 : Ranges.Max(r => r.Bytes);
    public string LargestText => ResizableBarDecoder.SizeText(LargestBytes);
    public string VramText => VramBytes > 0 ? ResizableBarDecoder.SizeText(VramBytes) : "—";
    public string RangeCountText => $"{Ranges.Count} 段";

    /// <summary>「覆蓋率」＝最大視窗 ÷ 顯示記憶體。純事實，不做判斷（見 <see cref="ResizableBarDecoder"/>）。</summary>
    public string CoverageText => VramBytes > 0 && LargestBytes > 0
        ? $"{(double)LargestBytes / VramBytes * 100:0.#} %"
        : "—";

    private (string Text, Severity Severity, string Detail) Eval
        => ResizableBarDecoder.Verdict(LargestBytes, VramBytes, IsPci);

    public string Verdict => Eval.Text;
    public Severity Severity => Eval.Severity;
    public string Detail => Eval.Detail;
}

/// <summary>
/// Resizable BAR 的判讀（純函式，不碰硬體也不碰系統 API，故可完整測試）。
/// </summary>
/// <remarks>
/// <para>
/// 要回答的問題只有一個：<b>顯示卡的記憶體視窗還停在傳統的 256 MB，還是已經被撐開了</b>。
/// ReBAR 要 BIOS 開啟、驅動支援、CSM 關掉、以 UEFI 開機，四個條件缺一個就不會生效，
/// 而 Windows 沒有任何地方告訴你現況；驅動控制台說「支援」也不等於現在真的生效。
/// </para>
/// <para>
/// <b>刻意不做的判斷：</b>不宣稱「完全生效」或「只生效一半」。BAR 尺寸一律是 2 的次方，
/// 所以 12 GB 顯示記憶體的卡最大也只能拿到 8 GB 的視窗（覆蓋率 67%）——那是規格使然，
/// 不是設定沒開好。把覆蓋率當成分數去打，會把正常的機器判成有問題。
/// 因此覆蓋率只當<b>事實</b>列出來，判定只回答「撐開了沒有」這個二分問題。
/// </para>
/// </remarks>
public static class ResizableBarDecoder
{
    /// <summary>
    /// 傳統視窗的上界。歷史上的預設孔徑是 256 MB，少數平台給 512 MB；
    /// 超過這個數就代表視窗確實被撐開過，那正是 ReBAR 在做的事。
    /// </summary>
    public const ulong LegacyApertureCeiling = 512UL * 1024 * 1024;

    /// <summary>把位元組換成人看的大小。刻意用 1024 進位（BAR 尺寸一律是 2 的次方）。</summary>
    public static string SizeText(ulong bytes)
    {
        if (bytes == 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v == Math.Floor(v) ? $"{v:0} {units[u]}" : $"{v:0.#} {units[u]}";
    }

    /// <summary>判定。<paramref name="vramBytes"/> 為 0 代表讀不到顯示記憶體總量。</summary>
    public static (string Text, Severity Severity, string Detail) Verdict(
        ulong largestBytes, ulong vramBytes, bool isPci = true)
    {
        // 虛擬顯示卡（模擬器、串流、遠端桌面裝出來的那些）也算顯示卡類別，但它們不是 PCI 裝置，
        // 沒有 BAR 也沒有顯示記憶體。對它們報「讀不到視窗」是把正常情形講成疑似故障。
        if (!isPci)
            return ("不適用", Severity.Neutral,
                "這是虛擬顯示裝置（不是 PCI 裝置），沒有記憶體視窗也沒有專屬顯示記憶體，"
                + "Resizable BAR 對它沒有意義。");

        if (largestBytes == 0)
            return ("讀不到記憶體視窗", Severity.Neutral,
                "系統沒有回報這個裝置被指派的記憶體範圍。裝置被停用、或驅動還沒配置資源時會這樣。");

        if (largestBytes <= LegacyApertureCeiling)
            return ("未生效", Severity.Warning,
                $"最大視窗只有 {SizeText(largestBytes)}，仍是傳統孔徑的量級。可能的原因由上而下檢查："
                + "①這張卡的世代本來就沒有這個功能——NVIDIA 要 GeForce RTX 30 系（Ampere）以後、"
                + "AMD 要 Radeon RX 6000 系以後才支援，更早的卡不論怎麼設定都不會生效；"
                + "②BIOS 沒開 Resizable BAR（部分主機板叫 Smart Access Memory，而且要先關掉 CSM 才會出現這個選項）；"
                + "③以傳統 CSM／Legacy 開機而不是純 UEFI；④開機碟是 MBR 分割而不是 GPT；⑤驅動版本太舊。");

        if (vramBytes == 0)
            return ("已撐開", Severity.Good,
                $"最大視窗 {SizeText(largestBytes)}，遠大於傳統孔徑，所以視窗確實被撐開了。"
                + "讀不到顯示記憶體總量，因此不列覆蓋率。");

        return ("已撐開", Severity.Good,
            $"最大視窗 {SizeText(largestBytes)}，顯示記憶體 {SizeText(vramBytes)}。"
            + "BAR 尺寸一律是 2 的次方，所以覆蓋率不一定是 100%——例如 12 GB 的卡最大只拿得到 8 GB 的視窗，"
            + "那是規格使然而不是設定沒開好，因此覆蓋率只當事實列出，不拿去打分數。");
    }

    /// <summary>整體摘要。沒有任何裝置時明說，不留白。</summary>
    public static string Summarize(IReadOnlyList<BarDeviceRow> rows)
    {
        if (rows.Count == 0)
            return "沒有列出任何顯示卡：這張卡片只看顯示卡（ReBAR 只在顯示卡上有意義），"
                 + "而系統沒有回報任何顯示卡類別的裝置。";

        var pci = rows.Where(r => r.IsPci).ToList();
        int virt = rows.Count - pci.Count;
        int open = pci.Count(r => r.LargestBytes > LegacyApertureCeiling);
        int legacy = pci.Count(r => r.LargestBytes is > 0 and <= LegacyApertureCeiling);
        int unknown = pci.Count - open - legacy;

        var parts = new List<string>();
        if (open > 0) parts.Add($"{open} 張已撐開");
        if (legacy > 0) parts.Add($"{legacy} 張仍是傳統孔徑");
        if (unknown > 0) parts.Add($"{unknown} 張讀不到視窗");
        if (virt > 0) parts.Add($"{virt} 個虛擬顯示裝置（不適用）");

        return $"共 {rows.Count} 個顯示裝置：" + string.Join("、", parts)
             + "。資料來自 Windows 回報的「已指派資源」，是實際生效的視窗，不是能力宣稱值。";
    }
}
