using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>從 SMBIOS 插槽命名推斷出來的一組通道證據。</summary>
/// <param name="SlotLocators">實際裝了模組的插槽名稱（原樣，供核對）。</param>
/// <param name="DistinctChannels">這些插槽分屬幾個不同的通道；推不出來時為 0。</param>
/// <param name="ChannelsKnown">通道命名是否推得出來。</param>
/// <param name="Modules">模組數。</param>
public readonly record struct MemoryChannelEvidence(
    IReadOnlyList<string> SlotLocators,
    int DistinctChannels,
    bool ChannelsKnown,
    int Modules);

/// <summary>
/// 記憶體通道配置的判讀：把「插槽命名推斷出的通道數」與「插槽總數」對起來，
/// 並判斷每通道 2 條（DPC2）這種配置對頻寬的影響。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼不能只數插槽：</b>「每支模組各佔一個通道」是<see cref="MemBandwidthMath.AssumedPeakGbps"/>
/// 的假設，但這個假設在四種常見情況下會失準，而且失準的方向不一样：
/// </para>
/// <list type="bullet">
/// <item><b>通道數少於插槽數</b>（例如 8 個插槽只有 4 個通道）：全插滿時每通道 2 條（2 DIMM per channel），
/// 理論上限不變，但實測通常比每通道 1 條低 5–15%——這是插法造成的，不是硬體故障。</item>
/// <item><b>通道數多於插槽數</b>（例如 4 個插槽、主機板宣告 8 通道的板子只做 4 個 DIMM 位置）：
/// 上限會高估。</item>
/// <item><b>模組全擠在同一個通道</b>：頻寬會掉到接近單通道，這是最該提醒的插錯。</item>
/// <item><b>單插槽平台</b>：通道數就是該平台的全部，插滿與否只影響容量不影響通道數。</item>
/// </list>
/// <para>
/// <b>本判讀不猜測記憶體控制器的實際通道數</b>——那是 CPU 規格，不是 SMBIOS 欄位。
/// 能講的只有兩件：插槽命名推斷出的分組，以及插槽總數。要真的知道走幾個通道，
/// 看「記憶體真實面貌」的實測頻寬。
/// </para>
/// </remarks>
public static class MemoryChannelJudge
{
    /// <summary>判讀結果的分類。</summary>
    public enum ChannelKind
    {
        /// <summary>通道分組推不出來——只能數插槽。</summary>
        Unknown,
        /// <summary>每個有模組的通道各 1 條，且插槽沒插滿。</summary>
        OnePerChannelRoomLeft,
        /// <summary>每個有模組的通道各 1 條，插槽已插滿。</summary>
        OnePerChannelFull,
        /// <summary>有通道裝了 2 條以上（DPC2）——容量優先的插法，頻寬通常略低。</summary>
        TwoPerChannel,
        /// <summary>全部模組擠在同一個通道——頻寬會掉到接近單通道。</summary>
        SingleChannelOnly,
    }

    /// <summary>判讀結果。</summary>
    public readonly record struct Verdict(ChannelKind Kind, string Headline, string Evidence, bool Attention);

    /// <summary>
    /// 判讀通道配置。<paramref name="slotsPerChannel"/> 是同一通道內模組數的最大值，
    /// 由呼叫端（<see cref="DimmLayout"/>）算好傳入。
    /// </summary>
    [SpecRef("SMBIOS Specification, Memory Device (Type 17) 的 Device Locator 與 Bank Locator 欄位（offset 0x10／0x11）——本判讀僅依韌體填寫的插槽命名分組，不宣稱記憶體控制器架構。每通道 2 條（2 DIMM per Channel）對頻寬的影響屬實測經驗值，非規格保證的固定比例。")]
    public static Verdict Judge(MemoryChannelEvidence e, int slotsPerChannel, int totalSlots)
    {
        if (e.Modules <= 0)
            return new Verdict(ChannelKind.Unknown, "—（沒有已安裝的模組）",
                "沒有模組可分析。", false);

        if (!e.ChannelsKnown || e.DistinctChannels <= 0)
            return new Verdict(ChannelKind.Unknown,
                $"裝了 {e.Modules} 支模組，但插槽命名看不出通道編號",
                $"已安裝插槽：{string.Join("、", e.SlotLocators)}。這塊主機板的插槽命名不含通道字母，"
                + "所以分不出哪幾支共用同一個通道——本判讀不猜，請看「記憶體真實面貌」量到的實際頻寬。",
                false);

        string where = $"裝了 {e.Modules} 支、分屬 {e.DistinctChannels} 個通道"
                     + $"，插槽共 {totalSlots} 個（已用 {e.Modules}）";

        if (e.DistinctChannels == 1 && e.Modules >= 2)
            return new Verdict(ChannelKind.SingleChannelOnly,
                $"⚠ {e.Modules} 支模組全在同一個通道——頻寬會掉到接近單通道",
                where + $"。模組全集中在通道 {ChannelLetter(e.SlotLocators)}，"
                + "多通道主機板這樣插，實際頻寬通常只有分散插的一半左右；"
                + "如果手上有兩支以上，把第二支移到另一個通道的同號插槽就能改善。"
                + "這是依插槽命名推斷，實際交錯狀況請以實測頻寬為準。",
                true);

        if (slotsPerChannel >= 2)
            return new Verdict(ChannelKind.TwoPerChannel,
                $"每通道 2 條（{e.Modules} 支／{e.DistinctChannels} 通道）——容量優先的插法",
                where + "。同一個通道上掛兩支模組時，理論上限不變，"
                + "但實測頻寬通常比每通道 1 條低一些——這是插法造成的，不是故障，"
                + "要換取的是總容量。若頻寬比預期低，先把每通道減到 1 條再量一次即可確認。",
                false);

        bool full = totalSlots > 0 && e.Modules >= totalSlots;
        return full
            ? new Verdict(ChannelKind.OnePerChannelFull,
                $"每通道 1 條、插槽已插滿（{e.Modules} 支／{e.DistinctChannels} 通道）",
                where + "。這是多通道平台最理想的插法：每條通道各一支模組，"
                + "沒有同通道分載的問題。實際頻寬仍要看時序與 XMP／EXPO 有沒有啟用。",
                false)
            : new Verdict(ChannelKind.OnePerChannelRoomLeft,
                $"每通道 1 條，還有 {totalSlots - e.Modules} 個空插槽（{e.Modules} 支／{e.DistinctChannels} 通道）",
                where + "。目前每條有模組的通道各一支，沒有分載問題。"
                + "要加記憶體時，先把成對的模組放在不同通道（通常是同號插槽），頻寬才不會因為同通道分載而下降。",
                false);
    }

    /// <summary>從插槽名稱取通道字母，取不到回「—」。</summary>
    private static string ChannelLetter(IReadOnlyList<string> locators)
    {
        foreach (string loc in locators)
        {
            int i = loc.IndexOf('_');
            if (i >= 0 && i + 1 < loc.Length) return loc[(i + 1)..(i + 2)];
            if (loc.Length > 0 && char.IsLetter(loc[0])) return loc[..1];
        }
        return "—";
    }

    /// <summary>
    /// 「假設每支各佔一個通道」這個假設是否站得住。
    /// 站得住＝每通道至多 1 條；站不住＝有通道掛了 2 條以上（上限不變）、或通道數不明（無法判斷）。
    /// </summary>
    /// <remarks>
    /// 這個方法存在的理由：<see cref="MemBandwidthMath.AssumedPeakGbps"/> 用的是「每支各佔一個通道」，
    /// 而它的呼叫端過去沒有能力檢查這個前提。<b>假設不成立時要說出來</b>——不然算出來的達成率
    /// 會被當成事實。
    /// </remarks>
    [SpecRef("推論陳述，非規格欄位：由 SMBIOS Type 17 插槽命名推得的每通道模組數，判斷「每支各佔一個通道」的假設是否成立。假設不成立時，理論上限的意義改變（上限不變但實測通常較低），必須在呈現時說明。")]
    public static string PeakAssumptionNote(MemoryChannelEvidence e, int slotsPerChannel)
    {
        if (!e.ChannelsKnown || e.DistinctChannels <= 0)
            return "理論上限以「每支模組各佔一個通道」推算；本機插槽命名看不出通道編號，"
                 + "這個假設無法驗證——僅供參考，請以實測值為準。";
        if (slotsPerChannel >= 2)
            return $"理論上限以「每支模組各佔一個通道」推算，但本機有通道掛了 {slotsPerChannel} 支模組"
                 + "（每通道 2 條）——上限值不變，不過這種插法的實測頻寬通常比每通道 1 條低一些，"
                 + "達成率會偏低是預期中的。";
        return "理論上限以「每支模組各佔一個通道」推算——本機每通道至多 1 支，該假設成立。";
    }
}
