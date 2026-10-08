using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>一個（區塊大小, 佇列深度）點位的量測值。</summary>
/// <param name="BlockBytes">區塊大小（位元組）。</param>
/// <param name="QueueDepth">同時未完成的 I/O 數。</param>
/// <param name="ReadIops">讀取 IOPS。</param>
/// <param name="WriteIops">寫入 IOPS。</param>
/// <param name="ReadLatencyUs">讀取完成延遲（微秒）。</param>
/// <param name="WriteLatencyUs">寫入完成延遲（微秒）。</param>
public readonly record struct StorageQdPoint(
    int BlockBytes,
    int QueueDepth,
    double ReadIops,
    double WriteIops,
    double ReadLatencyUs,
    double WriteLatencyUs);

/// <summary>一個區塊大小下，佇列深度梯次的掃描結果。</summary>
/// <param name="Points">該區塊大小的各 QD 點位，依 QD 排序。</param>
public readonly record struct StorageQdScan(IReadOnlyList<StorageQdPoint> Points);

/// <summary>
/// 儲存佇列深度（QD）掃描的判讀：把 IOPS 與延遲隨 QD 的變化讀成人看得懂的話。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要看 QD 而不是只看單一數字：</b>同一顆 SSD 在 QD1 與 QD32 下的 IOPS 可以差一個數量級。
/// 只報「這顆 SSD 有 50 萬 IOPS」等於沒說——那是 QD32 以上的並行值，而單一執行緒的日常使用
/// 大多落在 QD1–4。反過來說，<b>QD1 的差距才是「開程式快不快」的差距</b>，
/// 而 QD32 的差距只在使用者真的同時壓上大量 I/O 時才感受得到。
/// </para>
/// <para>
/// <b>延遲隨 QD 上升是必然的，不是故障：</b>佇列越深，每一筆的平均等待越久（排隊理論的基本結果）。
/// 真正該注意的是<b>IOPS 上升是否明顯趨緩</b>——那代表裝置或它所連的通道已經飽和，
/// 再把 QD 加上去只會拉長延遲而不增加吞吐。
/// </para>
/// <para>
/// <b>本判讀只描述這條掃描曲線，不外推裝置的規格上限。</b>量到的值受本次使用的檔案、
/// 檔案系統、驅動與電源狀態影響；每一項都會在來源欄寫明。
/// </para>
/// </remarks>
public static class StorageQdJudge
{
    /// <summary>曲線形狀的分類。</summary>
    public enum QdKind
    {
        /// <summary>QD 增加時 IOPS 明顯上升且未見平台——裝置還有餘裕。</summary>
        ScalesWithDepth,
        /// <summary>IOPS 在某個 QD 之後明顯不再上升——該點之後就是這顆裝置在本次條件下的實用上限。</summary>
        Saturates,
        /// <summary>QD 增加時 IOPS 反而下降——值得查（過熱降速、SLC 快取用盡、驅動或通道問題）。</summary>
        Regresses,
        /// <summary>只有單一 QD 點，無從比較趨勢。</summary>
        SinglePoint,
        /// <summary>讀不到或樣本不足，如實不判。</summary>
        Unknown,
    }

    /// <summary>判讀結果。</summary>
    /// <param name="Kind">曲線形狀。</param>
    /// <param name="Headline">一行結論。</param>
    /// <param name="Evidence">依據：各 QD 的實測值與推論步驟（含飽和點所在的 QD）。</param>
    /// <param name="SaturationQd">推得的飽和點 QD；未飽和或無從判斷為 0。</param>
    /// <param name="Access">是否值得進一步確認。</param>
    public readonly record struct Verdict(
        QdKind Kind, string Headline, string Evidence, int SaturationQd, bool Access);

    /// <summary>
    /// IOPS 從前一個 QD 到這一個 QD 的成長率低於此值即視為平台。
    /// 10% 是寬鬆的門檻：小樣本變異、溫度飄移都會造成幾個百分點的差異，
    /// 門檻訂太緊會把正常曲線判成飽和。
    /// </summary>
    public const double PlateauThreshold = 0.10;

    /// <summary>
    /// 判讀一個區塊大小的 QD 掃描。以<b>讀取 IOPS</b> 為代表（寫入受 SLC 快取與磨損平衡影響更大，
    /// 同一顆碟的寫入曲線重複性較差）。
    /// </summary>
    [SpecRef("儲存裝置的佇列深度與吞吐關係依排隊理論（Little's Law：未完成 I/O 數 ＝ 到達率 × 平均延遲，因此 QD ＝ IOPS × 延遲）。門檻值 PlateauThreshold 為本程式的判讀約定，非規格值；量測方法為原生 IOCP completion（見 DeepBench IOCP engine 的 Limitations 宣告）。")]
    public static Verdict Judge(StorageQdScan scan)
    {
        var pts = scan.Points.Where(p => p.ReadIops > 0).OrderBy(p => p.QueueDepth).ToList();
        if (pts.Count == 0)
            return new Verdict(QdKind.Unknown, "—（沒有可用的量測點）",
                "這個區塊大小沒有任何有效的讀取樣本，無從判讀。", 0, false);

        if (pts.Count == 1)
            return new Verdict(QdKind.SinglePoint,
                $"只有 QD{pts[0].QueueDepth} 一點（{pts[0].ReadIops:0} IOPS）",
                $"只有單一佇列深度的量測點（QD{pts[0].QueueDepth}，"
                + $"{pts[0].ReadIops:0} IOPS、延遲 {pts[0].ReadLatencyUs:0} µs），"
                + "沒有第二點可以比較趨勢——單點看不出這顆裝置有沒有餘裕。"
                + "要看趨勢請跑完整梯次（Quick 至少有 QD1 與 QD16）。",
                0, false);

        // 找第一個成長率低於門檻的點
        int saturation = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double prev = pts[i - 1].ReadIops, cur = pts[i].ReadIops;
            double growth = prev > 0 ? (cur - prev) / prev : 0;
            if (growth < PlateauThreshold) { saturation = pts[i].QueueDepth; break; }
        }

        string curve = string.Join("、", pts.Select(p =>
            $"QD{p.QueueDepth} {p.ReadIops:0} IOPS／{p.ReadLatencyUs:0} µs"));
        string block = $"{pts[0].BlockBytes / 1024} KiB 區塊";

        // 回歸：後面的 QD 反而比前面低
        bool regressed = false;
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].ReadIops < pts[i - 1].ReadIops * 0.9) regressed = true;

        if (regressed)
        {
            var worst = pts.Zip(pts.Skip(1), (a, b) => (a, b))
                           .OrderByDescending(x => x.a.ReadIops - x.b.ReadIops).First();
            return new Verdict(QdKind.Regresses,
                $"⚠ QD 加深後 IOPS 反而下降（QD{worst.a.QueueDepth} → QD{worst.b.QueueDepth}）",
                $"{block}：{curve}。加深佇列照理只會讓 IOPS 持平或上升；"
                + "實測出現下降，常見的三個原因是固態硬碟的 SLC 快取用盡（寫滿快取後速度掉到 TLC 直寫）、"
                + "過熱降速、或驅動／通道層的排程問題。"
                + "要分辨請看連續寫入的曲線（SLC 用盡會隨寫入量呈階梯狀下降）與本頁的溫度讀值。"
                + "本判讀只描述這次掃描，不推論原因。",
                0, true);
        }

        if (saturation > 0)
        {
            var sat = pts.First(p => p.QueueDepth == saturation);
            var prev = pts[pts.IndexOf(sat) - 1];
            double latencyGrowth = prev.ReadLatencyUs > 0
                ? (sat.ReadLatencyUs - prev.ReadLatencyUs) / prev.ReadLatencyUs : 0;
            return new Verdict(QdKind.Saturates,
                $"{block}：QD{saturation} 之後 IOPS 不再明顯上升（{sat.ReadIops:0} IOPS）",
                $"{block}：{curve}。從 QD{prev.QueueDepth} 到 QD{saturation} 的 IOPS 只增加 "
                + $"{(sat.ReadIops - prev.ReadIops) / prev.ReadIops * 100:0}%（低於 {PlateauThreshold:0%} 門檻），"
                + $"但延遲從 {prev.ReadLatencyUs:0} µs 升到 {sat.ReadLatencyUs:0} µs"
                + $"（增加 {latencyGrowth * 100:0}%）。"
                + "這代表裝置或它所連的通道在這個佇列深度附近已經飽和：再加深只會拉長延遲而不增加吞吐，"
                + "所以「這顆裝置在本次條件下的實用上限」大約就是這個值。"
                + "延遲隨 QD 上升是排隊的必然結果（QD ＝ IOPS × 延遲），不是故障。",
                saturation, false);
        }

        var last = pts[^1];
        return new Verdict(QdKind.ScalesWithDepth,
            $"{block}：到 QD{last.QueueDepth} 為止 IOPS 仍在上升（{last.ReadIops:0} IOPS）——裝置還有餘裕",
            $"{block}：{curve}。每一點都比前一級明顯上升（成長率都在 {PlateauThreshold:0%} 以上），"
            + $"所以到 QD{last.QueueDepth} 為止還沒看到平台——這顆裝置在本次條件下還有餘裕。"
            + "要找到它的實用上限需要更高的佇列深度（本程式的梯次上限為 QD32）。",
            0, false);
    }

    /// <summary>
    /// 同一個區塊大小下，QD1 與最高 QD 的差距——這是「單執行緒的體感」與「滿載吞吐」的分野。
    /// </summary>
    [SpecRef("排隊理論（Little's Law）：QD1 的 IOPS 反映單一未完成 I/O 的來回延遲，是可感受的單執行緒效能；高 QD 的 IOPS 反映裝置的並行處理上限。兩者比值為本判讀的推論陳述。")]
    public static string SingleThreadGap(IReadOnlyList<StorageQdPoint> points)
    {
        var pts = points.Where(p => p.ReadIops > 0).OrderBy(p => p.QueueDepth).ToList();
        if (pts.Count < 2)
            return "只有一個佇列深度，無法比較單執行緒與滿載的差距。";

        // 沒有 QD1 就找不到單執行緒的基準——First 會擲回例外，必須用 FirstOrDefault
        var one = pts.FirstOrDefault(p => p.QueueDepth == 1);
        var top = pts[^1];
        if (one.ReadIops <= 0 || one.QueueDepth == top.QueueDepth)
            return "沒有 QD1 的量測點，無法比較單執行緒與滿載的差距。";

        double ratio = top.ReadIops / one.ReadIops;
        return $"QD1 {one.ReadIops:0} IOPS（延遲 {one.ReadLatencyUs:0} µs）對上 "
             + $"QD{top.QueueDepth} {top.ReadIops:0} IOPS——差 {ratio:0.#} 倍。"
             + "日常的開程式、載入檔案大多落在低佇列深度，所以 QD1 的數字比較接近體感；"
             + $"QD{top.QueueDepth} 那個數字只在使用者真的同時壓上大量 I/O 時才感受得到。";
    }
}
