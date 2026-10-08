using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>
/// PCIe 鏈路能力與現況的落差分析結果。
/// </summary>
/// <param name="CapSpeed">裝置宣告的最大速度代碼。</param>
/// <param name="CapWidth">裝置宣告的最大寬度。</param>
/// <param name="CurSpeed">目前協商到的速度代碼。</param>
/// <param name="CurWidth">目前協商到的寬度。</param>
/// <param name="MaxSpeedUpstream">上游連接埠（若已知）宣告的最大速度；未知為 0。</param>
/// <param name="MaxWidthUpstream">上游連接埠（若已知）宣告的最大寬度；未知為 0。</param>
/// <param name="TargetSpeed">Link Control 2（+0x30）的 Target Link Speed 欄位；跨過 Gen2 才有意義，未編碼為 0。</param>
/// <param name="TargetSpeedProgrammable">Link Control 2 是否存在（PCIe 2.0 以上才有這個暫存器）。</param>
public readonly record struct PcieLinkGap(
    int CapSpeed, int CapWidth, int CurSpeed, int CurWidth,
    int MaxSpeedUpstream, int MaxWidthUpstream,
    int TargetSpeed, bool TargetSpeedProgrammable);

/// <summary>
/// PCIe 鏈路落差的判讀：把「裝置宣告的能力」與「實際協商到的鏈路」之間的落差，
/// 依成因分類成人看得懂的一句話。
/// </summary>
/// <remarks>
/// <para>
/// <b>本類別只判讀，不寫任何暫存器。</b>PCIe 有一條可以主動把鏈路「重新協商」到更高速度的路徑
/// （把 Link Control 2 的 Target Link Speed 設高，再觸發 Link Retrain），但那會動到正在運作的
/// 鏈路；本專案的全部硬體存取都以唯讀為原則，是否要跨出那條線是一個產品方向的決定，
/// 不由這個類別代為決定。因此這裡回答的是「差距有多大、可能是什麼造成的、要怎麼自己確認」，
/// 而不是「我幫你升上去」。
/// </para>
/// <para>
/// <b>為什麼「速度低於能力」不能直接說成故障：</b>PCIe 的鏈路速度是動態電源管理的一部分——
/// 顯示卡與 NVMe 在閒置時會降到 Gen1 省電，負載一來才升回去。這件事在靜態快照裡看起來
/// 和「真的只跑得動 Gen1」一模一樣，只有負載中重量才能分辨。寬度則不同：協商寬度通常在
/// 開機訓練時就定了，x16 的卡跑在 x4 幾乎都是插槽走線、M.2 佔用通道或 BIOS 分流設定造成的，
/// 那是固定損失、不會自己恢復。
/// </para>
/// </remarks>
public static class PcieNegotiationGap
{
    /// <summary>落差的成因分類。</summary>
    public enum GapKind
    {
        /// <summary>速度與寬度都與能力相符。</summary>
        None,
        /// <summary>寬度低於能力：固定損失，通常是插槽走線／通道分配造成。</summary>
        WidthLimited,
        /// <summary>速度低於能力，但仍在 PCIe 動態電源管理的合理範圍內（可能是閒置降速）。</summary>
        SpeedMaybeIdle,
        /// <summary>速度低於能力，且裝置本身就是省電能力的來源——降速是設計行為，不是故障。</summary>
        SpeedByDesign,
        /// <summary>上游連接埠的能力低於裝置——瓶頸在走線或插槽，不是這張卡。</summary>
        UpstreamLimit,
        /// <summary>讀不到足夠的欄位，如實不判。</summary>
        Unknown,
    }

    /// <summary>判讀的嚴重度。刻意不沿用主專案的 <c>Severity</c>（那是 UI 色語意），
    /// 純解碼器類別庫不依賴主專案型別——由服務層負責對應。</summary>
    public enum GapSeverity
    {
        /// <summary>沒有異常。</summary>
        None,
        /// <summary>值得查（固定的損失，例如寬度不足）。</summary>
        WorthChecking,
    }

    /// <summary>判讀結果。</summary>
    /// <param name="Kind">成因分類。</param>
    /// <param name="Headline">一行結論（繁中原文，交由語言層翻譯）。</param>
    /// <param name="Evidence">依據：實際欄位值與推論步驟，供使用者自行核對。</param>
    /// <param name="Severity">嚴重度。</param>
    public readonly record struct Verdict(GapKind Kind, string Headline, string Evidence, GapSeverity Severity);

    // Link Capabilities（+0x0C）的省電宣告旗標
    private const uint CapL0sSupport = 1u << 10;
    private const uint CapL1Support = 1u << 11;
    private const uint CapAspmSupport = 0x7u << 12;   // bits 14:12；非 0 表示有支援的 ASPM 等級
    private const uint CapClockPowerManagement = 1u << 9;

    /// <summary>
    /// 由 Link Capabilities（+0x0C）取出裝置自己宣告的省電能力旗標。
    /// </summary>
    /// <remarks>
    /// <para>
    /// bits 11:10 ＝ ASPM Support（00 不支援／01 L0s／10 L1／11 L0s+L1）、bit 12 ＝ L1 Substates、
    /// bit 18 ＝ Clock Power Management。有這些旗標而目前速度較低時，降速傾向是設計行為而非故障——
    /// 這是本判讀最重要的區分線，少了它就會把每一張正常閒置的顯卡都報成有問題。
    /// </para>
    /// <para>
    /// <b>bit 18（Clock Power Management）單獨不算數：</b>它與 bit 9（Clock Power Management 的
    /// 支援宣告）在近年平台上普遍為 1，若把它當成「會降速」的證據，幾乎每條鏈路都會被判成有省電能力，
    /// 這個旗標就失去區分力。這裡只認 ASPM 相關的位元，並在依據欄如實寫出看到的是哪一個旗標。
    /// </para>
    /// </remarks>
    [SpecRef("PCI Express Base Specification, Link Capabilities Register（PCIe 能力結構 +0x0C）：bits 11:10 ＝ ASPM Support（00＝不支援、01＝L0s、10＝L1、11＝L0s+L1）、bit 12 ＝ L1 Substates（bits 12 與 11:10 合併判讀 L1.1／L1.2 支援）、bit 18 ＝ Clock Power Management。位元位置與 PcieLinkDecoder 的 Link Capabilities 解碼同源；ASPM 與動態鏈路速度的關係見規格 §7.5.3。")]
    public static bool DeclaresPowerSaving(uint linkCap)
        => (linkCap & (CapL0sSupport | CapL1Support | CapAspmSupport)) != 0
           || (linkCap & CapClockPowerManagement) != 0 && (linkCap & (CapL0sSupport | CapL1Support)) != 0;

    /// <summary>
    /// Link Control 2（+0x30）的 Target Link Speed 欄位（bits 3:0）。
    /// </summary>
    /// <remarks>
    /// 這個欄位是「協商的上限」——鏈路訓練時不會超過它。它預設等於裝置支援的最高世代，
    /// 因此<b>讀到比裝置能力低的值，代表上限被誰壓低了</b>（BIOS、驅動或先前的工具）。
    /// 反過來讀到與能力相同的值，不代表鏈路一定跑到那個速度——閒置降速是另一回事。
    /// 這個暫存器只在 PCIe 2.0 以上的裝置存在；PCIe 1.1 的裝置讀到的會是能力結構之外的位址，
    /// 因此呼叫端必須先確認能力版本，不能無條件解讀。
    /// </remarks>
    [SpecRef("PCI Express Base Specification, Link Control 2 Register（PCIe 能力結構 +0x30）：bits 3:0 ＝ Target Link Speed（0001＝2.5 GT/s、0010＝5 GT/s、0011＝8 GT/s、0100＝16 GT/s、0101＝32 GT/s），僅在 Link Capabilities 回報的版本 ≥ 2 時存在；寫入此欄位後須設定 Link Control 的 Retrain Link 才會重新協商（本程式唯讀，不執行此動作）。")]
    public static int DecodeTargetLinkSpeed(uint linkControl2) => (int)(linkControl2 & 0xF);

    /// <summary>能力結構的版本（PCIe 能力暫存器 +0x02 的 bits 3:0）是否 ≥ 2，決定 Link Control 2 是否存在。</summary>
    [SpecRef("PCI Express Base Specification, PCI Express Capabilities Register（PCIe 能力結構 +0x02）：bits 3:0 ＝ Capability Version。Link Control 2／Link Status 2 自 version 2（PCIe 2.0）起才存在。")]
    public static bool HasLinkControl2(uint pcieCapRegister) => (pcieCapRegister & 0xF) >= 2;

    /// <summary>
    /// 判讀一條鏈路的落差。
    /// </summary>
    /// <remarks>
    /// 判讀順序刻意由「確定的原因」排到「不確定的原因」：
    /// ① 讀不到能力或現況 → 如實說讀不到；
    /// ② 寬度不足 → 固定的損失，成因明確；
    /// ③ 有上游連接埠且它的能力就低於裝置 → 瓶頸在走線／插槽，這條最容易被誤判成顯卡有問題；
    /// ④ 裝置自己宣告省電能力而速度較低 → 設計行為，不是故障；
    /// ⑤ 其餘速度較低 → 可能閒置降速，也可能真的上不去，只有負載中重量能分辨。
    /// </remarks>
    [SpecRef("PCI Express Base Specification §7.5.3（Link Capabilities／Link Status／Link Control 2 的欄位語意）；動態鏈路速度與電源管理的關係見 PCIe 規格 §5.4（Link Training and Status State Machine 的 L0s／L1 低功耗態與 Active State Power Management）；上游連接埠限制的推論依 PCIe 拓撲（根埠→交換器→端點）中「鏈路速度取雙方較低者」的協商規則。判讀僅比較唯讀欄位，不寫任何暫存器。")]
    public static Verdict Judge(PcieLinkGap gap)
    {
        if (gap.CapSpeed <= 0 || gap.CapWidth <= 0)
            return new Verdict(GapKind.Unknown, "—（裝置沒回報鏈路能力）",
                "Link Capabilities 的速度或寬度欄位為 0，無從比較。", GapSeverity.None);
        if (gap.CurSpeed <= 0 || gap.CurWidth <= 0)
            return new Verdict(GapKind.Unknown, "—（讀不到目前鏈路狀態）",
                "Link Status 的速度或寬度欄位為 0——鏈路可能未建立。", GapSeverity.None);

        string cap = $"{PcieLink.SpeedName(gap.CapSpeed)} x{gap.CapWidth}";
        string cur = $"{PcieLink.SpeedName(gap.CurSpeed)} x{gap.CurWidth}";
        string core = $"裝置宣告 {cap}、目前 {cur}";

        // ② 寬度不足：固定的損失。若上游寬度也剛好等於目前寬度，成因就是上游而非這張卡，
        //    此時分類仍為 WidthLimited（寬度損失是最需要講清楚的事），但在標題與依據裡指出上游。
        if (gap.CurWidth < gap.CapWidth)
        {
            int slack = gap.CurWidth * 5 / 4;   // 容差 25%：上游宣告略高於目前值時仍視為同一級
            bool upstreamBlamed = gap.MaxWidthUpstream > 0 && gap.MaxWidthUpstream <= slack;
            string upstream = gap.MaxWidthUpstream > 0
                ? $"；上游連接埠最大 x{gap.MaxWidthUpstream}"
                : "";
            string blame = upstreamBlamed
                ? $"成因在上游：上游連接埠本身只到 x{gap.MaxWidthUpstream}，這張卡不是瓶頸。"
                : "寬度在開機訓練時就定了，通常是插槽走線、M.2／SATA 佔用通道或 BIOS 的通道分流設定造成的，"
                  + "不會自己恢復；要確認請查主機板手冊的通道分配表，或把卡換到另一條插槽再量一次。";
            return new Verdict(GapKind.WidthLimited,
                $"⚠ 鏈路寬度只有 x{gap.CurWidth}，裝置支援 x{gap.CapWidth}",
                core + upstream + "。" + blame
                + "（頻寬約為能力的 "
                + $"{PcieLink.BandwidthFraction(gap.CurSpeed, gap.CurWidth, gap.CapSpeed, gap.CapWidth) * 100:0}%）",
                GapSeverity.WorthChecking);
        }

        // ③ 上游限制：瓶頸不在這張卡
        if (gap.MaxSpeedUpstream > 0 && gap.MaxSpeedUpstream < gap.CapSpeed && gap.CurSpeed >= gap.MaxSpeedUpstream)
        {
            return new Verdict(GapKind.UpstreamLimit,
                $"鏈路跑在上游連接埠的上限：{PcieLink.SpeedName(gap.MaxSpeedUpstream)}",
                core + $"；上游連接埠最大 {PcieLink.SpeedName(gap.MaxSpeedUpstream)}"
                + "。裝置本身支援更高，但上游（走線、插槽或交換器）只到這裡——這不是這張卡的問題，"
                + "換插槽或換平台才會改善。",
                GapSeverity.None);
        }

        if (gap.CurSpeed >= gap.CapSpeed)
        {
            string target = gap.TargetSpeedProgrammable && gap.TargetSpeed > 0
                ? $"；Link Control 2 的協商上限為 {PcieLink.SpeedName(gap.TargetSpeed)}"
                : "";
            return new Verdict(GapKind.None,
                $"已達裝置與上游允許的上限（{cur}）",
                core + target + "。", GapSeverity.None);
        }

        // ④ 其餘速度較低：只有負載中重量才能分辨。裝置是否宣告省電能力由 Evidence 補述
        //（見 DeclaresPowerSaving），不改變這裡的嚴重度——無論成因，處置都是「負載中重量一次」。
        return new Verdict(GapKind.SpeedMaybeIdle,
            $"目前 {PcieLink.SpeedName(gap.CurSpeed)}，裝置支援 {PcieLink.SpeedName(gap.CapSpeed)}",
            core
            + "。PCIe 的鏈路速度是動態電源管理的一部分——顯示卡與 NVMe 閒置時會降到 Gen1 省電，"
            + "負載一來才升回去；靜態快照分不出「閒置降速」與「真的只跑得動這個速度」。"
            + "要確認請在負載中（跑遊戲、大量讀寫）再量一次，或看本頁重新掃描後的結果。"
            + (gap.TargetSpeedProgrammable && gap.TargetSpeed > 0 && gap.TargetSpeed < gap.CapSpeed
                ? $"另注意：Link Control 2 的協商上限被設為 {PcieLink.SpeedName(gap.TargetSpeed)}，"
                  + $"低於裝置能力的 {PcieLink.SpeedName(gap.CapSpeed)}——"
                  + "上限是被 BIOS、驅動或先前的工具壓低的，這種情況下再怎麼操也不會上去。"
                : ""),
            GapSeverity.None);
    }
}
