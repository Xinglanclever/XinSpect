namespace XinSpect;

/// <summary>
/// PCI Base Address Register 的純解碼器（V7 WP30／A13）：type0 標頭 0x10–0x24 的六個 BAR
/// 與 0x30 的 Expansion ROM BAR，解出資源型別與基底位址。
/// 誠實界線：<b>資源大小無法唯讀取得</b>——標準做法是把全 F 寫回 BAR 讀回長度遮罩，那是寫入；
/// 本服務只報型別與基底，大小不出值（不是待辦，是唯讀界線）。
/// 64-bit 記憶體 BAR 佔用下一個 BAR 槽（高位 dword），依規格配對消費。
/// </summary>
public static class PciBars
{
    /// <summary>單一資源：型別、可預取（僅記憶體）、基底位址。</summary>
    public sealed record BarResource(string Kind, bool Prefetchable, ulong Base);

    /// <summary>
    /// 解 type0 標頭的六個 BAR dword（0x10–0x24）與 expansion ROM（0x30；null＝不讀）。
    /// 全 0 的 BAR 視為未配置（實務上基位址不會是 0），跳過如實計數。
    /// </summary>
    [SpecRef("PCI Local Bus Spec 3.0 §6.2.5（Base Address Registers）：bit0 型別、bits[2:1] 記憶體定址寬度、bit3 prefetchable、§6.2.5.1 I/O 佈局；Expansion ROM BAR 同章標頭佈局表 0x30（bit0 啟用、基底 bits[31:11]）")]
    public static (IReadOnlyList<BarResource> Resources, int Unconfigured) DecodeHeader(
        IReadOnlyList<uint> bars, uint? expansionRom = null)
    {
        var resources = new List<BarResource>();
        int unconfigured = 0;
        int i = 0;
        while (i < bars.Count)
        {
            uint raw = bars[i];
            if (raw == 0)
            {
                unconfigured++;
                i++;
                continue;
            }
            if ((raw & 0x1) != 0)
            {
                // I/O BAR：bits[31:2] 為基底（4-byte 對齊）
                resources.Add(new BarResource("I/O", false, raw & 0xFFFFFFFCu));
                i++;
                continue;
            }
            // 記憶體 BAR：bits[2:1] 型別（00＝32-bit、10＝64-bit）、bit3 prefetchable
            bool prefetchable = (raw & 0x8) != 0;
            ulong type = (raw >> 1) & 0x3;
            ulong baseLow = raw & 0xFFFFFFF0u;
            if (type == 0x2 && i + 1 < bars.Count)
            {
                ulong high = bars[i + 1];
                resources.Add(new BarResource("記憶體（64-bit）", prefetchable, baseLow | (high << 32)));
                i += 2; // 64-bit BAR 佔兩槽，依規格配對消費
                continue;
            }
            resources.Add(new BarResource(type == 0x0 ? "記憶體（32-bit）" : "記憶體（型別保留值，照 32-bit 報）",
                prefetchable, baseLow));
            i++;
        }

        if (expansionRom is { } rom && rom != 0)
            resources.Insert(0, new BarResource("Expansion ROM", false, rom & 0xFFFFF800u));

        return (resources, unconfigured);
    }

    /// <summary>把資源清單轉成一行事實文字（全部段落可從原始值稽核）。</summary>
    [SpecRef("本專案呈現方法學：型別與基底照 PCI Local Bus Spec 3.0 §6.2.5 解碼；大小需寫入探測故不出值（唯讀界線）")]
    public static string Describe(IReadOnlyList<BarResource> resources, int unconfigured)
    {
        if (resources.Count == 0)
            return unconfigured > 0 ? $"無已配置資源（{unconfigured} 個 BAR 為 0——未實作或未配置）" : "無資源資訊";
        var parts = resources.Select(r => $"{r.Kind} 0x{r.Base:X}{(r.Prefetchable ? "（可預取）" : "")}").ToList();
        if (unconfigured > 0) parts.Add($"另有 {unconfigured} 個未配置 BAR");
        return string.Join("；", parts);
    }
}
