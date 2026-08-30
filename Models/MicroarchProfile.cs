namespace XinSpect;

/// <summary>混合架構下的核心類型（CPUID 0x1A 的 EAX[31:24]）。</summary>
public enum CoreKind
{
    /// <summary>未知或非混合架構。</summary>
    Unknown = 0,
    /// <summary>小核（Atom 系，E-core）。</summary>
    Efficiency = 0x20,
    /// <summary>大核（Core 系，P-core）。</summary>
    Performance = 0x40,
}

/// <summary>Top-down Level 1 在這顆處理器上該用哪一套方法量測。</summary>
public enum TmaMethod
{
    /// <summary>不具備 Level 1 分解所需的事件，或無法確認。</summary>
    None = 0,
    /// <summary>
    /// 事件式 Level 1：4 個通用計數器（CPU_CLK_UNHALTED.THREAD_P、IDQ_UOPS_NOT_DELIVERED.CORE、
    /// UOPS_RETIRED.RETIRE_SLOTS、UOPS_ISSUED.ANY），分母為<c>管線寬度 × CLKS</c>。
    /// </summary>
    LegacyEvents = 1,
    /// <summary>
    /// Ice Lake 之後的正規做法：PERF_METRICS（MSR 0x329）搭配 TOPDOWN.SLOTS 固定計數器，
    /// 由硬體直接給出四桶比例，不再靠事件相減。
    /// </summary>
    PerfMetrics = 2,
    /// <summary>
    /// Atom 系（小核）專屬的 TOPDOWN_FE_BOUND／BE_BOUND／RETIRING／BAD_SPECULATION 事件族，
    /// 事件編碼與大核完全不同。
    /// </summary>
    AtomTopdown = 3,
}

/// <summary>一顆處理器（或混合架構下的一種核型）的微架構事實。</summary>
public sealed class MicroarchInfo
{
    public MicroarchInfo(string product, string uarch, int pipelineWidth, bool isHybrid, TmaMethod tma)
    { Product = product; Uarch = uarch; PipelineWidth = pipelineWidth; IsHybrid = isHybrid; Tma = tma; }

    /// <summary>產品代號，如「Skylake-X／Cascade Lake-SP」。未知時為空字串。</summary>
    public string Product { get; }
    /// <summary>微架構名，如「Skylake」、「Golden Cove」。未知時為空字串。</summary>
    public string Uarch { get; }
    /// <summary>每周期配發（allocation）插槽數，即 TMA 分母的 SLOTS 係數。未知時為 0。</summary>
    public int PipelineWidth { get; }
    /// <summary>是否為大小核混合部件。</summary>
    public bool IsHybrid { get; }
    /// <summary>Top-down Level 1 在此架構上該用的方法。</summary>
    public TmaMethod Tma { get; }

    /// <summary>是否查得到這顆處理器（型號在表內）。</summary>
    public bool IsKnown => PipelineWidth > 0 && Uarch.Length > 0;

    /// <summary>本專案的事件式 Level 1 是否能在此架構上給出正確的四桶。</summary>
    public bool LegacyTmaUsable => Tma == TmaMethod.LegacyEvents && PipelineWidth > 0;

    /// <summary>「Skylake-X（Skylake），4 插槽／周期」這樣的一行描述；未知時如實說未知。</summary>
    public string DisplayName => IsKnown
        ? (Product == Uarch ? Uarch : $"{Product}（{Uarch}）") + $"，{PipelineWidth} 插槽／周期"
        : "未知微架構";

    /// <summary>不支援時的具體原因；支援時說明用的是哪套公式。留給 UI 直接顯示。</summary>
    public string TmaNote => Tma switch
    {
        TmaMethod.LegacyEvents =>
            $"事件式 Level 1 可用：分母採 {PipelineWidth} × CPU_CLK_UNHALTED（{Uarch} 的配發寬度）。",
        TmaMethod.PerfMetrics =>
            $"{Uarch} 這一代 Intel 已改由 PERF_METRICS（MSR 0x329）搭配 TOPDOWN.SLOTS 固定計數器直接提供 Level 1，"
          + "舊的四事件配方在此架構上對不上，本頁不出數字——寧可空著，也不給一組看起來合理但是錯的比例。",
        TmaMethod.AtomTopdown =>
            $"{Uarch} 屬 Atom 系，用的是 TOPDOWN_FE_BOUND／BE_BOUND／RETIRING／BAD_SPECULATION 這一組專屬事件，"
          + "與大核編碼完全不同，套用大核事件只會得到亂數，故不出數字。",
        _ => "無法確認此型號的管線寬度與事件可用性，故不出數字：猜一個分母去除，比誠實說「不知道」更糟。",
    };
}

/// <summary>
/// 由 CPUID 簽章判斷微架構、管線寬度，以及 Top-down Level 1 該走哪條路。純查表，不接觸硬體。
/// </summary>
/// <remarks>
/// 這張表存在的理由是一個具體的正確性問題：TMA Level 1 的分母是
/// <c>SLOTS ＝ 管線寬度 × CPU_CLK_UNHALTED</c>，而<b>管線寬度不是常數</b>——
/// Skylake 世代是 4，Sunny／Willow／Cypress Cove 是 5，Golden／Raptor／Redwood Cove 是 6，
/// Lion Cove 是 8。把 4 寫死，在 Ice Lake 之後的機器上會讓分母偏小，四桶百分比整組偏大，
/// 而畫面上看起來仍然是「一個合理的數字」——這種錯比讀不到值危險得多。
///
/// 更進一步：Golden Cove 之後，Intel 已把 Level 1 改由 PERF_METRICS（MSR 0x329）直接提供，
/// 舊的四事件配方在大小核混合的部件上根本對不上（小核用的是另一組 TOPDOWN_* 事件）。
/// 因此本表除了寬度，也回報「該用哪套方法」；<see cref="TopDownService"/> 在不是
/// <see cref="TmaMethod.LegacyEvents"/> 時一律拒絕出數字，並說明原因，而不是硬算一組錯的比例。
///
/// 未列出的型號一律回 <see cref="TmaMethod.None"/>：寬度與事件可用性都無法確認時，
/// 猜一個分母去除比誠實說「不知道」更糟。
/// </remarks>
public static class MicroarchProfile
{
    /// <summary>
    /// 拆解 CPUID leaf 1 的 EAX 簽章成顯示用的 family／model（含 extended 欄位）。
    /// </summary>
    /// <remarks>
    /// Intel 的編碼規則：Family ＝ base[11:8] ＋ extended[27:20]，
    /// Model ＝ base[7:4] ｜ (extended[19:16] ≪ 4)。Family 6 的 base 已是 6，
    /// 加上 extended（0）仍為 6；這個「相加」而非「相接」的細節寫錯，型號會整批對不上。
    /// </remarks>
    public static (int Family, int Model) DecodeSignature(uint sig)
    {
        int family = (int)((sig >> 8) & 0xF) + (int)((sig >> 20) & 0xFF);
        int model = (int)((sig >> 4) & 0xF) | (int)((sig >> 12) & 0xF0);
        return (family, model);
    }

    /// <summary>由 CPUID 0x1A 的 EAX 判斷核心類型；leaf 不存在（EAX＝0）時回 <see cref="CoreKind.Unknown"/>。</summary>
    public static CoreKind CoreKindFromCpuid1A(uint eax) => ((eax >> 24) & 0xFF) switch
    {
        0x20 => CoreKind.Efficiency,
        0x40 => CoreKind.Performance,
        _ => CoreKind.Unknown,
    };

    /// <summary>核心類型的中文短標籤。</summary>
    public static string CoreKindText(CoreKind kind) => kind switch
    {
        CoreKind.Performance => "大核（P-core）",
        CoreKind.Efficiency => "小核（E-core）",
        _ => "單一核型",
    };

    /// <summary>未知型號的統一結果：寬度 0、方法 None，UI 據此拒絕出數字。</summary>
    public static MicroarchInfo Unknown { get; } = new("", "", 0, false, TmaMethod.None);

    /// <summary>
    /// 查表。<paramref name="kind"/> 只在混合架構部件上有意義（用來報出當下這顆核心是哪一種微架構）；
    /// 非混合部件會忽略它。
    /// </summary>
    /// <remarks>
    /// 只認 Intel Family 6。AMD 的 family/model 空間完全不同，同號事件意義也不同，
    /// 在此一律回未知——本表的用途是替 Intel PMU 的事件配方把關，不是通用的 CPU 資料庫。
    /// </remarks>
    public static MicroarchInfo Identify(int family, int model, CoreKind kind = CoreKind.Unknown)
    {
        if (family != 6) return Unknown;

        // ── 混合架構：同一顆晶片上兩種微架構、兩種寬度、兩套事件編碼 ────────────
        // 這正是「一個寫死的分母」最會出錯的地方，故先攔下來按核型分流。
        switch (model)
        {
            case 0x97 or 0x9A or 0xBE:   // Alder Lake-S/P、Alder Lake-N（僅小核）
                return kind == CoreKind.Efficiency || model == 0xBE
                    ? Gracemont("Alder Lake")
                    : GoldenCove("Alder Lake");
            case 0xB7 or 0xBA or 0xBF:   // Raptor Lake / Raptor Lake-S refresh
                return kind == CoreKind.Efficiency
                    ? Gracemont("Raptor Lake")
                    : new MicroarchInfo("Raptor Lake", "Raptor Cove", 6, true, TmaMethod.PerfMetrics);
            case 0xAA or 0xAC:           // Meteor Lake
                return kind == CoreKind.Efficiency
                    ? new MicroarchInfo("Meteor Lake", "Crestmont", 6, true, TmaMethod.AtomTopdown)
                    : new MicroarchInfo("Meteor Lake", "Redwood Cove", 6, true, TmaMethod.PerfMetrics);
            case 0xBD:                   // Lunar Lake
                return kind == CoreKind.Efficiency
                    ? new MicroarchInfo("Lunar Lake", "Skymont", 8, true, TmaMethod.AtomTopdown)
                    : new MicroarchInfo("Lunar Lake", "Lion Cove", 8, true, TmaMethod.PerfMetrics);
            case 0xC6 or 0xC5 or 0xB5:   // Arrow Lake-S / -H / -U
                return kind == CoreKind.Efficiency
                    ? new MicroarchInfo("Arrow Lake", "Skymont", 8, true, TmaMethod.AtomTopdown)
                    : new MicroarchInfo("Arrow Lake", "Lion Cove", 8, true, TmaMethod.PerfMetrics);
        }

        return model switch
        {
            // ── Nehalem／Westmere：IDQ_UOPS_NOT_DELIVERED 尚不存在，四事件配方湊不齊 ──
            0x1A or 0x1E or 0x1F or 0x2E => new("Nehalem", "Nehalem", 4, false, TmaMethod.None),
            0x25 or 0x2C or 0x2F => new("Westmere", "Westmere", 4, false, TmaMethod.None),

            // ── Sandy Bridge → Comet Lake：事件式 Level 1 的適用區間，寬度 4 ────────
            0x2A or 0x2D => new("Sandy Bridge", "Sandy Bridge", 4, false, TmaMethod.LegacyEvents),
            0x3A or 0x3E => new("Ivy Bridge", "Ivy Bridge", 4, false, TmaMethod.LegacyEvents),
            0x3C or 0x45 or 0x46 or 0x3F => new("Haswell", "Haswell", 4, false, TmaMethod.LegacyEvents),
            0x3D or 0x47 or 0x56 or 0x4F => new("Broadwell", "Broadwell", 4, false, TmaMethod.LegacyEvents),
            0x4E or 0x5E => new("Skylake", "Skylake", 4, false, TmaMethod.LegacyEvents),
            0x55 => new("Skylake-X／Cascade Lake-SP／Cooper Lake", "Skylake", 4, false, TmaMethod.LegacyEvents),
            0x8E or 0x9E => new("Kaby Lake／Coffee Lake", "Skylake", 4, false, TmaMethod.LegacyEvents),
            0xA5 or 0xA6 => new("Comet Lake", "Skylake", 4, false, TmaMethod.LegacyEvents),
            0x66 => new("Cannon Lake", "Palm Cove", 4, false, TmaMethod.LegacyEvents),

            // ── Ice Lake → Rocket Lake：寬度變了（5），舊事件仍在，但分母不能再用 4 ──
            0x7D or 0x7E => new("Ice Lake", "Sunny Cove", 5, false, TmaMethod.LegacyEvents),
            0x6A or 0x6C => new("Ice Lake-SP／-DE", "Sunny Cove", 5, false, TmaMethod.LegacyEvents),
            0x8C or 0x8D => new("Tiger Lake", "Willow Cove", 5, false, TmaMethod.LegacyEvents),
            0xA7 => new("Rocket Lake", "Cypress Cove", 5, false, TmaMethod.LegacyEvents),

            // ── Golden Cove 之後的伺服器部件：改走 PERF_METRICS ────────────────────
            0x8F => new("Sapphire Rapids", "Golden Cove", 6, false, TmaMethod.PerfMetrics),
            0xCF => new("Emerald Rapids", "Raptor Cove", 6, false, TmaMethod.PerfMetrics),
            0xAD or 0xAE => new("Granite Rapids", "Redwood Cove", 6, false, TmaMethod.PerfMetrics),

            // ── Atom 系（單一核型的部件）：事件族完全不同 ─────────────────────────
            0x5C or 0x5F => new("Goldmont", "Goldmont", 3, false, TmaMethod.AtomTopdown),
            0x7A => new("Goldmont Plus", "Goldmont Plus", 4, false, TmaMethod.AtomTopdown),
            0x86 or 0x96 or 0x9C => new("Tremont", "Tremont", 4, false, TmaMethod.AtomTopdown),
            0xAF or 0xB6 => new("Sierra Forest／Grand Ridge", "Crestmont", 6, false, TmaMethod.AtomTopdown),
            0xDD => new("Clearwater Forest", "Darkmont", 8, false, TmaMethod.AtomTopdown),

            _ => Unknown,
        };
    }

    private static MicroarchInfo GoldenCove(string product)
        => new(product, "Golden Cove", 6, true, TmaMethod.PerfMetrics);

    private static MicroarchInfo Gracemont(string product)
        => new(product, "Gracemont", 5, true, TmaMethod.AtomTopdown);
}
