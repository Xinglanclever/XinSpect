using System;
using System.Collections.Generic;
using System.Globalization;

namespace XinSpect;

/// <summary>一張網卡的兩條鏈路實況：PCIe 側（匯流排）與乙太網路側（線路）。讀不到為 null／0。</summary>
/// <param name="Name">介面名稱（Windows 顯示的名稱）。</param>
/// <param name="Description">介面描述（含型號）。</param>
/// <param name="IsUp">介面是否已連線（Up）。</param>
/// <param name="MediaType">媒體類型（802.3／Native 802.11／IP 等）。</param>
/// <param name="PcieCurrentSpeed">PCIe 目前速度（GT/s）；非 PCIe 裝置為 0。</param>
/// <param name="PcieCurrentWidth">PCIe 目前寬度（條）；非 PCIe 裝置為 0。</param>
/// <param name="PcieMaxSpeed">PCIe 能力速度（GT/s）；讀不到為 0。</param>
/// <param name="PcieMaxWidth">PCIe 能力寬度（條）；讀不到為 0。</param>
/// <param name="EthCurrentBps">目前線路速率（bit/s）；未連線或非乙太為 0。</param>
/// <param name="SupportedSpeedsBps">驅動回報支援的速率清單（bit/s，已排序）；讀不到為空。</param>
public readonly record struct NicLinkSample(
    string Name,
    string Description,
    bool IsUp,
    string MediaType,
    int PcieCurrentSpeed,
    int PcieCurrentWidth,
    int PcieMaxSpeed,
    int PcieMaxWidth,
    ulong EthCurrentBps,
    IReadOnlyList<ulong> SupportedSpeedsBps);

/// <summary>
/// 網卡落差的判讀：把「這張卡宣稱的能力」與「兩條鏈路各自實際跑到的值」擺在一起。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼網卡要看兩條鏈路：</b>一張 PCIe 網卡同時受兩個上限約束——PCIe 匯流排（速度×寬度）
/// 與乙太網路線路（協商速率）。兩者都會降，而且成因完全不同：PCIe 降是插槽走線或通道分配，
/// 線路降是線材、對端設備或協商設定。<b>只報一個「1 Gbps」等於什麼都沒說</b>——
/// 看不出是插槽只給 x1、是插在 1G 的交換器上、還是驅動被鎖在 1G。
/// </para>
/// <para>
/// <b>最要緊的一條：PCIe 供給是否足以餵飽線路。</b>10G 線路需要約 10 Gbit/s，
/// 換算到 Gen3 x2（8 GT/s × 2 條、128b/130b 編碼）約 15.8 Gbit/s 單向——夠；
/// 但若插槽只給 x1（約 7.9 Gbit/s），10G 線路就餵不飽，這是真實的效能損失而不是顯示問題。
/// 本判讀會把這個比較算出來，並在供給不足時明說。
/// </para>
/// </remarks>
public static class NicLinkGap
{
    /// <summary>落差成因分類。</summary>
    public enum GapKind
    {
        /// <summary>PCIe 與線路都到位。</summary>
        None,
        /// <summary>PCIe 寬度低於能力：固定損失，插槽走線或通道分配造成。</summary>
        PcieWidthLimited,
        /// <summary>線路速率低於這張卡支援的最高速率：多半是線材、對端或協商設定。</summary>
        LineNegotiatedLower,
        /// <summary>PCIe 供給頻寬不足以餵飽目前線路速率——真實的效能上限。</summary>
        PcieStarvedForLine,
        /// <summary>介面未連線，無從比較。</summary>
        NotConnected,
        /// <summary>讀不到足夠欄位，如實不判。</summary>
        Unknown,
    }

    /// <summary>判讀結果。</summary>
    public readonly record struct Verdict(GapKind Kind, string Headline, string Evidence, bool Attention);

    /// <summary>把 PCIe 速度代碼與寬度換成單向可用頻寬（bit/s）。速度 0 或寬度 0 回 0。</summary>
    /// <remarks>
    /// GT/s 是「每秒傳輸次數」不是位元率，且要扣掉線路編碼：Gen1／Gen2 用 8b/10b（每 10 位元帶 8 位元資料）、
    /// Gen3 以上用 128b/130b。<see cref="PcieLink.GtPerSecond"/> 已把速度代碼轉成 GT/s，
    /// 這裡再乘寬度與編碼效率。
    /// </remarks>
    [SpecRef("PCI Express Base Specification：線路編碼 8b/10b 用於 2.5／5.0 GT/s、128b/130b 用於 8.0 GT/s 以上；單向頻寬 ＝ GT/s × 10^9 × 條數 × 編碼效率。GT/s 為電氣速率而非資料率，未扣編碼會高估 25%（8b/10b）或 1.6%（128b/130b）。")]
    public static double PcieBandwidthBps(int speedCode, int width)
    {
        if (speedCode <= 0 || width <= 0) return 0;
        double gt = PcieLink.GtPerSecond(speedCode);
        if (gt <= 0) return 0;
        double efficiency = speedCode <= 2 ? 8.0 / 10.0 : 128.0 / 130.0;
        return gt * 1e9 * width * efficiency;
    }

    /// <summary>
    /// 判讀一張網卡。順序：未連線 → PCIe 寬度不足 → PCIe 餵不飽線路 → 線路協商低於能力 → 相符。
    /// </summary>
    /// <remarks>
    /// 「PCIe 餵不飽線路」排在「線路協商較低」之前，是因為它描述的是<b>目前這條鏈路的真實上限</b>；
    /// 若 PCIe 已經不夠，就算線路協商到滿也沒用，先講這個才有意義。
    /// </remarks>
    [SpecRef("PCI Express Base Specification（Link Capabilities／Link Status 的速度與寬度欄位語意，見 PcieLink）；乙太網路速率協商依 IEEE 802.3（含 2.5GBASE-T／5GBASE-T 的 NBASE-T 補充）。PCIe 供給與線路需求的比較是推論，依據欄會列出兩邊的實算值供核對；判讀僅比較唯讀欄位。")]
    public static Verdict Judge(NicLinkSample s)
    {
        if (s.Name.Length == 0)
            return new Verdict(GapKind.Unknown, "—（沒有介面資訊）", "介面名稱為空，無從判讀。", false);

        if (!s.IsUp)
            return new Verdict(GapKind.NotConnected,
                "未連線——無從比較鏈路",
                $"{s.Name}（{s.Description}）目前未連線（媒體類型 {MediaText(s)}）。"
                + "未連線時驅動回報的速率為 0，比較它沒有意義。",
                false);

        // 非 PCIe 裝置（虛擬網卡、Wintun、Wi-Fi 走不同的匯流排）不套 PCIe 段落
        bool isPcie = s.PcieCurrentWidth > 0 || s.PcieMaxWidth > 0;

        if (isPcie && s.PcieCurrentSpeed > 0 && s.PcieMaxWidth > 0 && s.PcieCurrentWidth < s.PcieMaxWidth)
        {
            double cur = PcieBandwidthBps(s.PcieCurrentSpeed, s.PcieCurrentWidth);
            double max = PcieBandwidthBps(s.PcieMaxSpeed > 0 ? s.PcieMaxSpeed : s.PcieCurrentSpeed, s.PcieMaxWidth);
            string line = s.EthCurrentBps > 0
                ? $"目前線路速率 {Bps(s.EthCurrentBps)}，PCIe 現有單向頻寬約 {Bps((ulong)cur)}"
                  + (cur < s.EthCurrentBps * 1.2
                     ? "——<b>匯流排供給已經貼近甚至低於線路需求，這條 10G 線路跑不滿</b>"
                     : "——匯流排供給足以餵飽這條線路")
                : "";
            return new Verdict(GapKind.PcieWidthLimited,
                $"PCIe 寬度只有 x{s.PcieCurrentWidth}，這張卡支援 x{s.PcieMaxWidth}",
                $"{s.Name}：PCIe 目前 {PcieLink.SpeedName(s.PcieCurrentSpeed)} x{s.PcieCurrentWidth}"
                + $"、能力 {PcieLink.SpeedName(s.PcieMaxSpeed)} x{s.PcieMaxWidth}"
                + $"，單向頻寬約為能力的 {Fraction(cur, max)}。"
                + "網卡的 PCIe 寬度在開機訓練時就定了，通常是插槽走線、通道與其他裝置共用或 BIOS 分流設定造成的；"
                + "要確認請查主機板手冊的通道分配表，或把卡換到另一條插槽再量一次。"
                + line, true);
        }

        // PCIe 沒有寬度落差，但供給仍可能餵不飽線路（例如 x1 插槽配 10G 線路而寬度剛好等於能力）
        if (isPcie && s.EthCurrentBps > 0 && s.PcieCurrentWidth > 0)
        {
            double supply = PcieBandwidthBps(
                s.PcieCurrentSpeed > 0 ? s.PcieCurrentSpeed : s.PcieMaxSpeed, s.PcieCurrentWidth);
            if (supply > 0 && supply < s.EthCurrentBps * 1.1)
                return new Verdict(GapKind.PcieStarvedForLine,
                    $"PCIe 供給（約 {Bps((ulong)supply)}）低於線路速率（{Bps(s.EthCurrentBps)}）——這條線路跑不滿",
                    $"{s.Name}：PCIe {PcieLink.SpeedName(s.PcieCurrentSpeed)} x{s.PcieCurrentWidth}"
                    + $"（單向約 {Bps((ulong)supply)}）對上線路 {Bps(s.EthCurrentBps)}。"
                    + "乙太網路的速率是雙向同時的，PCIe 的單向頻寬必須能同時承載送與收才算足夠；"
                    + "供給不足時，實際吞吐會被匯流排限制住而不是被線路限制住——這條線路跑不滿。",
                    true);
        }

        // 線路協商低於這張卡支援的最高速率
        ulong best = 0;
        foreach (ulong v in s.SupportedSpeedsBps) if (v > best) best = v;
        if (best > 0 && s.EthCurrentBps > 0 && s.EthCurrentBps < best)
        {
            return new Verdict(GapKind.LineNegotiatedLower,
                $"線路協商在 {Bps(s.EthCurrentBps)}，這張卡支援到 {Bps(best)}",
                $"{s.Name}（{s.Description}）：目前線路 {Bps(s.EthCurrentBps)}，驅動回報支援"
                + $"（{string.Join("／", Names(s.SupportedSpeedsBps))}）。"
                + "線路速率由兩端協商決定，取雙方與線材都能接受的上限——"
                + "最常見的三個原因是線材（Cat5e 跑不了 2.5G 以上、Cat6 短距離才上 10G）、"
                + "對端設備（交換器埠只到 1G）、或驅動被鎖在固定速率。"
                + "要分辨請換一條已知規格的線、或改接另一個埠再量一次。",
                false);
        }

        // 沒有可比較的基準就誠實說不適用：虛擬介面（Wintun／隧道）沒有 PCIe 端點，
        // 速率也是名目值——此時說「相符」是假陳述，因為根本沒有能力值可比。
        if (!isPcie)
            return new Verdict(GapKind.Unknown, "—（沒有可比較的鏈路）",
                $"{s.Name}（{s.Description}）：媒體類型 {MediaText(s)}"
                + (s.EthCurrentBps > 0 ? $"，回報速率 {Bps(s.EthCurrentBps)}" : "，未回報線路速率")
                + "。這不是 PCIe 端點（虛擬介面或軟體通道），沒有匯流排能力值可比對，"
                + "本判讀不適用——如實標示，不套用 PCIe 的結論。", false);

        return new Verdict(GapKind.None,
            s.EthCurrentBps > 0
                ? $"鏈路到位（{Bps(s.EthCurrentBps)}"
                  + (isPcie && s.PcieCurrentWidth > 0
                     ? $"，PCIe {PcieLink.SpeedName(s.PcieCurrentSpeed)} x{s.PcieCurrentWidth}" : "")
                  + "）"
                : "已連線，未回報線路速率",
            $"{s.Name}（{s.Description}）："
            + (isPcie && s.PcieCurrentWidth > 0
               ? $"PCIe {PcieLink.SpeedName(s.PcieCurrentSpeed)} x{s.PcieCurrentWidth}／能力"
                 + $" {PcieLink.SpeedName(s.PcieMaxSpeed)} x{s.PcieMaxWidth}；" : "")
            + (s.EthCurrentBps > 0 ? $"線路 {Bps(s.EthCurrentBps)}。" : "線路速率未回報。"),
            false);
    }

    /// <summary>媒體類型的顯示文字（空字串如實標「未知」，不編一個）。</summary>
    private static string MediaText(NicLinkSample s)
        => string.IsNullOrWhiteSpace(s.MediaType) ? "未知" : s.MediaType;

    /// <summary>目前值佔能力的百分比（能力為 0 時如實回「—」，不編一個 0% 出來）。</summary>
    private static string Fraction(double current, double max)
        => max > 0 ? $"{current / max * 100:0}%" : "—";

    /// <summary>速率清單 → 人看得懂的名稱（1 Gbps／2.5 Gbps…）。</summary>
    [SpecRef("IEEE 802.3 與 NBASE-T（2.5GBASE-T／5GBASE-T）的線路速率命名：以十進位 SI 前綴表示（1 Gbps ＝ 10^9 bit/s），非二進位。格式化只做單位換算，不改變量到的值。")]
    public static IEnumerable<string> Names(IReadOnlyList<ulong> speeds)
    {
        foreach (ulong v in speeds) yield return Bps(v);
    }

    /// <summary>bit/s → 人看得懂的字串。10 Gbps 以下用 Gbps／Mbps，避免出現「0.001 Gbps」。</summary>
    [SpecRef("IEEE 802.3 與 NBASE-T 的線路速率命名：十進位 SI 前綴（kbps＝10^3、Mbps＝10^6、Gbps＝10^9 bit/s），與儲存容量的二進位前綴不同。0 如實顯示為「—」，不編一個 0 bps 出來。")]
    public static string Bps(ulong bps) => bps switch
    {
        0 => "—",
        >= 1_000_000_000 => (bps / 1_000_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Gbps",
        >= 1_000_000 => (bps / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps",
        >= 1_000 => (bps / 1_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " kbps",
        _ => bps + " bps",
    };
}
