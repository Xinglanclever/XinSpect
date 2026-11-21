namespace XinSpect;

/// <summary>把 EDID 宣告的實際時序接到桌面／CLI 共用事實集；沿用既有登錄檔快照。</summary>
public static class EdidTimingFactsService
{
    public const string TimingsKey = "edid.timings";
    private const string Source = "登錄檔 EDID 快照＋E-EDID/DMT 解碼（唯讀）";

    public static IReadOnlyList<HardwareFact> Collect(EdidReadSnapshot snapshot, DateTimeOffset at)
    {
        var lines = new List<string>();
        var issues = new List<string>(snapshot.Errors);
        int valid = 0;
        foreach (var (label, edid) in snapshot.Edids)
        {
            var decoded = EdidTimings.Parse(edid);
            if (decoded.BaseValid) valid++;
            issues.AddRange(decoded.Issues.Select(i => label + "：" + i));
            if (!decoded.BaseValid) continue;
            lines.Add($"{label}：{decoded.Timings.Count} 條時序宣告（相同模式可在多個欄位重複，不是唯一模式數）");
            foreach (var mode in decoded.Timings.Take(128))
            {
                string match = mode.DmtCode is int code ? $"DMT 0x{code:X2}" : "未對到唯一 DMT，保留宣告值";
                string rate = mode.RefreshHz == "未確認" ? "刷新率未確認" : $"{mode.RefreshHz} {(mode.Interlaced ? "幀/s（交錯）" : "Hz")}";
                string text = $"  0x{mode.Offset:X2} {mode.Encoding}：{mode.Width}x{mode.Height} @ {rate}；{match}；{mode.MatchBasis}";
                if (mode.Detailed is { } d)
                    text += $"；{d.ClockKhz} kHz，h {d.HActive}/{d.HSyncStart}/{d.HSyncEnd}/{d.HTotal} {d.HSync}，"
                          + $"v{(mode.Interlaced ? "（每場）" : "")} {d.VActive}/{d.VSyncStart}/{d.VSyncEnd}/{d.VTotal} {d.VSync}，"
                          + $"影像 {((d.ImageWidthMm == 0 || d.ImageHeightMm == 0) ? "尺寸未宣告" : $"{d.ImageWidthMm}x{d.ImageHeightMm} mm")}，"
                          + $"flags 0x{d.Flags:X2}，border {d.HBorder}/{d.VBorder}";
                lines.Add(text);
            }
            if (decoded.Timings.Count > 128)
                lines.Add($"  另有 {decoded.Timings.Count - 128} 條未展開（顯示上限 128）");
        }
        FactAvailability availability = valid == 0
            ? (issues.Count > 0 ? FactAvailability.ReadError : FactAvailability.Unknown)
            : issues.Count > 0 ? FactAvailability.Unknown : FactAvailability.Present;
        string? reason = issues.Count > 0 ? string.Join("；", issues)
            : valid == 0 ? "登錄檔沒有 EDID 資料，不能推斷沒有顯示器" : null;
        lines.Add("來源是 Windows 登錄檔快照，可能包含已拔除顯示器且按內容去重，不代表目前連線數。"
            + "只列面板／轉接器宣告，不代表目前輸出模式或實測可用性；CTA VIC 與 DisplayID 不包含在本清單。"
            + "Standard 的 DMT 僅為幾何候選，DTD 才比對完整時序。");
        return [new HardwareFact(TimingsKey, "顯示", "EDID 宣告時序與 DMT 對照", string.Join("\n", lines), "",
            Source, FactTrustLevel.Reported, false, at, null, availability, reason)];
    }
}
