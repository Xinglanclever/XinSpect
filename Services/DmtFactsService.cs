namespace XinSpect;

/// <summary>
/// DMT 顯示時序知識包的事實面（U10 第四圈批次二：VC-0001～0021、VC-0038、VC-0040、VC-0043）。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>第 276 輪的 VIC 包讓 CTA-861 的視訊識別碼有名字；這一支補上另一半——
/// VESA DMT 全表（88 條）與 EDID established timing 的兩張位圖對照（17＋44 條），
/// 讓 detailed timing／standard timing 裡的時序能被「叫出名字」。
/// </para>
/// <para>
/// <b>沒有新讀取面：</b>純知識包（與第 331 輪的 WHEA 節區表同款）——Collect 不做 I/O。
/// EDID 位元組的對照消費者留給 EDID 解碼側（Cta861.cs／EdidService）下一輪接。
/// </para>
/// </remarks>
public static class DmtFactsService
{
    public const string Category = "顯示";
    public const string SummaryKey = "dmt.kp.summary";
    public const string ModesKey = "dmt.kp.modes";
    public const string Est1Key = "dmt.kp.est1";
    public const string Est3Key = "dmt.kp.est3";
    public const string SelfCheckKey = "vic.kp.selfcheck";
    public const string HdmiVsCtaKey = "vic.hdmivs.cta";
    public const string VersionKey = "vic.kp.version";
    public const string BoundaryKey = "dmt.kp.boundary";

    private const string Source =
        "VESA DMT v1.0r12＋E-EDID v1.4 §3.8——經 Linux drivers/gpu/drm/drm_edid.c 逐欄對照（kernel.org，2026-11-15）";

    /// <summary>純函式：知識包內容不隨機器變化。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at)
    {
        int modes = DmtKnowledge.Modes.Count;
        int est1 = DmtKnowledge.EstablishedTiming1.Count;
        int est3 = DmtKnowledge.EstablishedTiming3.Count;

        // 自校（VC-0040）：est3 每一條都要對到 DMT；est1 對到的是「多數」，對不到的如實列名。
        int est3Resolved = 0;
        var est3Misses = new List<string>();
        foreach (var e in DmtKnowledge.EstablishedTiming3)
        {
            int? code = DmtKnowledge.ResolveEst3(e);
            if (code is int c) { est3Resolved++; }
            else est3Misses.Add($"bit{e.Bit}（{e.Width}x{e.Height}@{e.RefreshHz}）");
        }

        int est1Resolved = 0;
        var est1Misses = new List<string>();
        foreach (var e in DmtKnowledge.EstablishedTiming1)
        {
            int? code = DmtKnowledge.ResolveEst1(e);
            if (code is int) est1Resolved++;
            else est1Misses.Add($"{e.Width}x{e.Height}@{e.RefreshHz}{(e.Interlaced ? "i" : "")}");
        }

        var rows = new List<string>();
        foreach (var m in DmtKnowledge.Modes)
            rows.Add($"0x{m.Code:X2}＝{m.Name}@{m.RefreshHz}{(m.ReducedBlanking ? " RB" : "")}{(m.Interlaced ? "i" : "")}"
                + $"（{m.ClockKhz} kHz，h {m.HActive}/{m.HSyncStart}/{m.HSyncEnd}/{m.HTotal} {m.HSync}，"
                + $"v {m.VActive}/{m.VSyncStart}/{m.VSyncEnd}/{m.VTotal} {m.VSync}）");

        var est1Rows = new List<string>();
        foreach (var e in DmtKnowledge.EstablishedTiming1)
        {
            int? code = DmtKnowledge.ResolveEst1(e);
            string tag = code is int c ? $"DMT 0x{c:X2}" : "**非 DMT**（DMT 之前的遺產模式）";
            est1Rows.Add($"bit{e.Bit}＝{e.Width}x{e.Height}@{e.RefreshHz}{(e.Interlaced ? "i" : "")}→{tag}");
        }

        var est3Rows = new List<string>();
        foreach (var e in DmtKnowledge.EstablishedTiming3)
        {
            int? code = DmtKnowledge.ResolveEst3(e);
            est3Rows.Add($"bit{e.Bit}＝{e.Width}x{e.Height}@{e.RefreshHz}{(e.ReducedBlanking ? " RB" : "")}"
                + $"→{(code is int c2 ? $"DMT 0x{c2:X2}" : "**未對到**")}");
        }

        return
        [
            new HardwareFact(SummaryKey, Category, "DMT 時序知識包彙總",
                $"收錄 {modes} 條 VESA DMT 模式（0x01–0x58，含 {DmtKnowledge.Modes.Count(m => m.ReducedBlanking)} 條 RB 變體與 "
                + $"{DmtKnowledge.Modes.Count(m => m.Interlaced)} 條交錯式）、{est1} 條 established timing I、{est3} 條 established timing III"
                + $"・這是**解碼表**（讓 EDID 時序有名字），不是行為——EDID 位元組的消費者在 EDID 解碼側，本輪不接",
                "條", Source, FactTrustLevel.Measured, false, at, modes),
            new HardwareFact(ModesKey, Category, "DMT 逐模式對照表",
                string.Join("\n", rows), "條", Source, FactTrustLevel.Measured, false, at, modes),
            new HardwareFact(Est1Key, Category, "Established Timing I 位圖對照（EDID 位元組 32–34）",
                string.Join("\n", est1Rows) + $"・24 位中 {24 - est1} 位保留未定義，如實不列",
                "條", Source, FactTrustLevel.Measured, false, at, est1),
            new HardwareFact(Est3Key, Category, "Established Timing III 描述元位圖對照",
                string.Join("\n", est3Rows), "條", Source, FactTrustLevel.Measured, false, at, est3),
            new HardwareFact(SelfCheckKey, Category, "時序知識包自校（VC-0040）",
                $"est3 {est3Resolved}/{est3} 條對到 DMT"
                + (est3Misses.Count > 0 ? "・未對到：" + string.Join("、", est3Misses) : "（全數通過）")
                + $"・est1 {est1Resolved}/{est1} 條對到 DMT"
                + (est1Misses.Count > 0 ? "・非 DMT（遺產模式，如實列名）：" + string.Join("、", est1Misses) : "")
                + "・「對不到」是**對照的結果**，不是失敗——遺產模式本來就不在 DMT 表裡",
                "條", Source, FactTrustLevel.Derived, false, at, est3Resolved),
            new HardwareFact(HdmiVsCtaKey, Category, "HDMI VIC 與 CTA VIC 分命名空間（VC-0038）",
                "CTA-861 的 VIC（第 276 輪已收 154 條）與 HDMI 1.3+ VSDB 的 HDMI_VIC（僅 4 個專屬值，1–4）"
                + "是**兩個命名空間**：VIC 95 與 HDMI VIC 1 各指各的時序，不可互換、不可加總"
                + "・EDID 對帳時兩邊要分開列出；本包只管 DMT 與 established timing，VIC 解碼在 Cta861.cs",
                "", Source, FactTrustLevel.Measured, false, at, null),
            new HardwareFact(VersionKey, Category, "時序知識包版本與來源宣告（VC-0043）",
                DmtKnowledge.Version + "・更新通道：規格修訂時以公開源逐欄重對照，版本字串隨之更新",
                "", Source, FactTrustLevel.Measured, false, at, null),
            new HardwareFact(BoundaryKey, Category, "DMT 時序知識包的界線宣告",
                "① 本包是**解碼表**：DMT 全表與 established timing 兩張位圖的對照，不含任何 EDID 讀取——"
                + "EDID 位元組的消費者在 EDID 解碼側，本輪不接；"
                + "② **CVT-RB 生成式時序明文不做**（沿主目錄 VC-0039／NX 立場）：本表只收 VESA DMT 已定案的 88 條，"
                + "不生成任何「應該存在」的時序；"
                + "③ est1 的 17 條中有 " + est1Misses.Count + " 條**非 DMT 遺產模式**（640x480@67、832x624@75、720x400@88、"
                + "1024x768@43i 等）——對不到 DMT 不是錯，是歷史；"
                + "④ HDMI VIC（VSDB）與 CTA VIC 分命名空間（見 vic.hdmivs.cta），本表不收 HDMI VIC；"
                + "⑤ 刷新率字串原樣保留（59.94 不捨入成 60）——查找時全欄位都對才命中",
                "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null),
        ];
    }
}
