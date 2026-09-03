namespace XinSpect;

/// <summary>卡點所指的部位（顯示為徽章文字）。</summary>
public enum BottleneckKind { Thermal, Power, Cpu, Memory, Storage, Gpu, Driver, Policy, Platform }

/// <summary>
/// 一條卡點判定：<b>證據</b>（本機真實讀值）、<b>怎麼辦</b>（可執行的下一步）、
/// <b>去哪看</b>（哪一頁能把這件事量得更清楚）。
/// </summary>
/// <remarks>
/// 這裡刻意不放「預估提升 x %」之類的數字：卡點分析用的是當下讀值，沒有做過 A／B 對照，
/// 講幅度就是編的。<see cref="UpgradeSuggestion"/> 那邊談的是換硬體的經驗範圍，性質不同。
/// </remarks>
public sealed class BottleneckFinding
{
    public required BottleneckKind Kind { get; init; }
    public required string Title { get; init; }
    public required Severity Severity { get; init; }

    /// <summary>優先度（0–100，越大越該先處理），亦為排序鍵。</summary>
    public required int Score { get; init; }

    /// <summary>判斷依據：本機的真實讀值，含數字與來源。</summary>
    public required string Evidence { get; init; }

    /// <summary>具體怎麼辦。做不到的事不寫。</summary>
    public required string Advice { get; init; }

    /// <summary>要把這件事量得更清楚該去哪一頁。</summary>
    public string Where { get; init; } = "";

    public string KindText => Kind switch
    {
        BottleneckKind.Thermal => "散熱",
        BottleneckKind.Power => "功耗",
        BottleneckKind.Cpu => "處理器",
        BottleneckKind.Memory => "記憶體",
        BottleneckKind.Storage => "儲存",
        BottleneckKind.Gpu => "顯示卡",
        BottleneckKind.Driver => "驅動程式",
        BottleneckKind.Policy => "系統設定",
        _ => "平台",
    };

    public string PriorityText => Score >= 90 ? "最該先看" : Score >= 70 ? "明顯" : Score >= 50 ? "有跡象" : "留意";
    public bool HasWhere => Where.Length > 0;
}

/// <summary>
/// 餵給卡點規則引擎的事實：一份與硬體讀取完全脫鉤的純資料，由
/// <c>BottleneckFactsCollector</c> 從主檢視模型（畫面上同一份讀值）搬進來。
/// </summary>
/// <remarks>
/// 可為 null 的欄位一律代表「這台機器上沒有這項讀值」或「使用者還沒去量」，規則遇到 null
/// 直接略過該條，不以「一般情況」補值——補了就等於在替使用者編造他機器上的數字。
/// 缺哪些資料會被寫進 <see cref="BottleneckReport.Unknowns"/> 讓使用者自己決定要不要去量。
/// </remarks>
public sealed class BottleneckFacts
{
    // ── 當下讀值（感測引擎，每秒） ────────────────────────────────────────
    public double? CpuLoad { get; set; }
    public double? CpuTempC { get; set; }
    public double? CpuPowerW { get; set; }
    public double? CpuClockMhz { get; set; }
    public double? MemLoad { get; set; }
    public double? MemTotalGb { get; set; }
    public double? GpuLoad { get; set; }
    public double? GpuTempC { get; set; }
    public double? GpuVramUsedGb { get; set; }
    public double? GpuVramTotalGb { get; set; }
    public double? MaxDiskTempC { get; set; }
    public double? WorstDiskLifePercent { get; set; }
    /// <summary>系統磁碟區剩餘空間百分比。</summary>
    public double? SystemFreePercent { get; set; }

    // ── 逐核負載分佈（判斷「全核吃滿」還是「一顆核心卡住」） ──────────────
    public int CoreCount { get; set; }
    public double? MaxCoreLoad { get; set; }
    public double? MedianCoreLoad { get; set; }

    // ── 風扇 ──────────────────────────────────────────────────────────────
    public int FanCount { get; set; }
    /// <summary>有下輸出命令（&gt; 5 %）卻讀到 0 RPM 的風扇數。</summary>
    public int FansCommandedButStopped { get; set; }

    // ── 黏滯節流位元（處理器頁讀過才有值） ────────────────────────────────
    public bool? ThermalLogSeen { get; set; }
    public bool? PowerLimitLogSeen { get; set; }
    public bool? ThrottlingNow { get; set; }
    /// <summary>整個暫存器為 0，讀值本身不可信（此時三個位元不採用）。</summary>
    public bool StickyUnreliable { get; set; }

    // ── 逐核歸因（CoreTime，取樣一秒） ────────────────────────────────────
    public double? MaxDpcPercent { get; set; }
    public double? MaxInterruptPercent { get; set; }
    public string WorstDpcCore { get; set; } = "";

    // ── 記憶體真實面貌（認可帳面） ────────────────────────────────────────
    public double? CommitGb { get; set; }
    public double? CommitLimitGb { get; set; }
    public double? CommitPeakGb { get; set; }
    public double? PhysicalGb { get; set; }

    // ── MCA ───────────────────────────────────────────────────────────────
    public int? McaUncorrected { get; set; }
    public int? McaCorrectedBanks { get; set; }

    // ── 電源政策（只轉述該服務自己標記過的項目，不重新判斷） ──────────────
    public string PowerPlanName { get; set; } = "";
    public List<(string Name, string Value)> PolicyFlags { get; } = [];

    // ── 效能天花板（跑過才有判定） ────────────────────────────────────────
    public bool CeilingHasVerdict { get; set; }
    public string CeilingHeadline { get; set; } = "";
    public string CeilingDetail { get; set; } = "";
    public Severity CeilingSeverity { get; set; }

    // ── Top-down（跑過才有值，單位為百分比） ──────────────────────────────
    public double? TdRetiring { get; set; }
    public double? TdBadSpec { get; set; }
    public double? TdFrontend { get; set; }
    public double? TdBackend { get; set; }

    // ── 長期樣本 ──────────────────────────────────────────────────────────
    public int HistoryMinutes { get; set; }
    public double? CpuLoadP95 { get; set; }
    public double? CpuTempP95 { get; set; }
    public double? MemLoadP95 { get; set; }
    public double? GpuLoadP95 { get; set; }
    public bool ThrottleEventSeen { get; set; }
    /// <summary>降頻事件的取樣視窗（分鐘）：事件紀錄是持久化的，沒有時間界線就會把上個月的事講成現在。</summary>
    public int HistoryWindowMinutes { get; set; } = 24 * 60;
    /// <summary>最近一次降頻事件的時間字樣，用在證據裡；沒有就空字串。</summary>
    public string ThrottleEventText { get; set; } = "";

    // ── 使用者自己設的門檻（判斷「燙不燙」要用他的標準，不是我的） ────────
    public double CpuTempThreshold { get; set; } = 90;
    public double MemLoadThreshold { get; set; } = 92;

    /// <summary>是否有足夠長期樣本（至少 30 分鐘）可下「長期表現」的結論。</summary>
    public bool HasLongTerm => HistoryMinutes >= 30;

    /// <summary>黏滯位元是否真的讀到且可信。</summary>
    public bool StickyUsable => !StickyUnreliable && ThermalLogSeen is not null;
}

/// <summary>一次卡點分析的結果。</summary>
public sealed class BottleneckReport
{
    /// <summary>一句話結論。</summary>
    public string Headline { get; init; } = "尚未分析。";

    /// <summary>結論的展開說明（含這個結論的界線）。</summary>
    public string Detail { get; init; } = "";

    public Severity Severity { get; init; } = Severity.Neutral;

    /// <summary>依優先度排序的卡點清單。</summary>
    public IReadOnlyList<BottleneckFinding> Findings { get; init; } = [];

    /// <summary>還沒量、因此本次分析沒把它算進來的項目（含該去哪一頁量）。</summary>
    public IReadOnlyList<string> Unknowns { get; init; } = [];

    /// <summary>這份結論的可信度：用了多少當下讀值、多久的長期樣本、哪些深入量測缺席。</summary>
    public string Confidence { get; init; } = "";

    public bool HasFindings => Findings.Count > 0;
    public bool HasUnknowns => Unknowns.Count > 0;
}

/// <summary>
/// 卡點規則引擎：把散在各頁的讀值合起來看一次，回答「現在是什麼在拖住這台機器」。
/// 純函式——輸入 <see cref="BottleneckFacts"/>、輸出 <see cref="BottleneckReport"/>，
/// 不碰硬體、不碰 UI，所以規則本身可以在沒有硬體的機器上單元測試。
/// </summary>
/// <remarks>
/// <para>
/// 三條自律：<b>沒讀到就不談</b>（null 一律略過並登記進 Unknowns）；<b>不預估幅度</b>（沒做過
/// A／B 對照，講「提升 15 %」就是編的）；<b>結論指得出證據</b>（每一條都附上算出它的那些數字）。
/// </para>
/// <para>
/// 「沒有明顯卡點」是合法且常見的結論，不會為了讓畫面有東西而硬湊一條。
/// </para>
/// </remarks>
public static class BottleneckAnalyzer
{
    public static BottleneckReport Analyze(BottleneckFacts f)
    {
        var found = new List<BottleneckFinding>();
        Thermal(f, found);
        Cpu(f, found);
        Memory(f, found);
        Storage(f, found);
        Gpu(f, found);
        Driver(f, found);
        Policy(f, found);
        Platform(f, found);

        var ranked = found.OrderByDescending(x => x.Score).ThenByDescending(x => (int)x.Severity).ToList();
        var unknowns = Unknowns(f);
        var top = ranked.FirstOrDefault();

        return new BottleneckReport
        {
            Findings = ranked,
            Unknowns = unknowns,
            Confidence = Confidence(f, unknowns.Count),
            // 整頁的嚴重度取「最嚴重的那一條」，不是「分數最高的那一條」——
            // 分數排的是該先看的順序，不是壞的程度，兩者不一定同一條
            Severity = ranked.Count == 0 ? Severity.Good : ranked.Max(x => x.Severity),
            Headline = top is null
                ? "量到的讀值裡沒有明顯卡點。"
                : $"最可能的卡點：{top.KindText}——{top.Title}",
            Detail = top is null
                ? "這是「本次讀到的數字沒有踩到任何一條判斷線」，不是「這台機器沒有極限」。"
                  + "卡點常常只在負載上來的那幾秒出現，閒置時看不到；要抓那一瞬間，請在跑得動的時候回來看這一頁，"
                  + "或到效能天花板頁做逐窗撞牆量測。"
                : ranked.Count == 1
                    ? "只找到這一條。下面的證據就是判斷依據，數字都是本機讀到的。"
                    : $"另外還有 {ranked.Count - 1} 條跡象列在下面，依該先看的順序排。"
                      + "同時出現多條時，通常上面那條是因、下面那些是果。",
        };
    }

    private static void Add(List<BottleneckFinding> to, BottleneckKind kind, string title, Severity sev,
        int score, string evidence, string advice, string where = "")
        => to.Add(new BottleneckFinding
        {
            Kind = kind, Title = title, Severity = sev, Score = score,
            Evidence = evidence, Advice = advice, Where = where,
        });

    // ── 散熱與功耗 ──────────────────────────────────────────────────────────

    private static void Thermal(BottleneckFacts f, List<BottleneckFinding> to)
    {
        var t = new List<BottleneckFinding>();

        if (f.StickyUsable && f.ThrottlingNow == true)
            Add(t, BottleneckKind.Thermal, "此刻正在因為過熱降頻", Severity.Critical, 100,
                $"IA32_PACKAGE_THERM_STATUS bit0（即時封裝熱狀態）為 1"
                + (f.CpuTempC is double t0 ? $"，當下封裝溫度 {t0:0} °C" : "") + "。",
                "降頻正在發生，跑什麼都會慢。先確認風扇有在轉、進出風口沒被擋住；散熱器與矽脂用了幾年就該回頭檢查一次。",
                "處理器頁 → 黏滯節流位元");

        if (f.StickyUsable && f.ThermalLogSeen == true && f.ThrottlingNow == false)
            Add(t, BottleneckKind.Thermal, "開機至今撞過溫度牆", Severity.Warning, 72,
                "封裝熱狀態 log 位元（bit1）為 1——這是黏滯紀錄，代表「發生過」而不是「現在正在發生」。",
                "現在沒在降頻，但重負載時會。要看撞牆的深度與時間點，到效能天花板頁做逐窗量測。",
                "效能天花板");

        if (f.StickyUsable && f.PowerLimitLogSeen == true)
            Add(t, BottleneckKind.Power, "開機至今撞過功耗牆（PL2）", Severity.Warning, 68,
                "封裝 PL2 功耗限制 log 位元（bit11）為 1"
                + (f.CpuPowerW is double w ? $"，當下封裝功耗 {w:0.0} W" : "") + "。",
                "功耗牆是韌體設定的，不是壞掉。想解開得動 BIOS 的 PL1／PL2 與 Tau，而那會連帶提高溫度與耗電——先確認散熱撐得住再說。",
                "效能天花板");

        if (f.CpuTempC is double now)
        {
            double thr = f.CpuTempThreshold;
            if (now >= thr)
                Add(t, BottleneckKind.Thermal, "處理器溫度已超過你設的門檻", Severity.Serious, 86,
                    $"當下 {now:0} °C，你設的警示門檻是 {thr:0} °C。",
                    "溫度貼著上限時，頻率會被壓在保守值。先看風扇轉速與機殼氣流；門檻本身也可以在設定頁調整。",
                    "感測器");
            else if (now >= thr - 8)
                Add(t, BottleneckKind.Thermal, "處理器溫度接近門檻", Severity.Warning, 54,
                    $"當下 {now:0} °C，距離你設的 {thr:0} °C 只差 {thr - now:0} °C。",
                    "現在還沒被壓，但負載再上去就會。這一條是預告，不是故障。",
                    "感測器");
        }

        if (f.FansCommandedButStopped > 0)
            Add(t, BottleneckKind.Thermal, "有風扇被下了命令卻沒在轉", Severity.Serious, 90,
                $"{f.FansCommandedButStopped} 顆風扇的輸出百分比大於 5 % 但轉速讀到 0 RPM"
                + $"（共 {f.FanCount} 顆可控風扇）。",
                "這通常是接頭鬆脫、風扇卡死或軸承壽命到了。也可能是那顆接頭沒有轉速回報線——"
                + "先確認該風扇實際上到底有沒有在轉，再決定是換風扇還是忽略。",
                "系統風扇");

        if (f.HasLongTerm && f.CpuTempP95 is double p95 && p95 >= f.CpuTempThreshold - 5)
            Add(t, BottleneckKind.Thermal, "長期而言溫度一直很高", Severity.Warning, 58,
                $"過去 {Span(f.HistoryMinutes)} 的處理器溫度 95 百分位為 {p95:0} °C。",
                "95 百分位＝有 5 % 的時間比這更高。這不是偶爾一次，是常態。",
                "歷史回放");

        if (f.ThrottleEventSeen)
            Add(t, BottleneckKind.Thermal, "事件紀錄裡有降頻紀錄", Severity.Warning, 52,
                f.ThrottleEventText.Length > 0
                    ? $"事件紀錄在最近 {Span(f.HistoryWindowMinutes)} 內記錄到降頻事件（{f.ThrottleEventText}）。"
                    : $"事件紀錄在最近 {Span(f.HistoryWindowMinutes)} 內記錄到降頻事件。",
                // 1.9.0 移掉了獨立的「事件時間軸」分頁，所以這裡改指向歷史回放——
                // 同一段時間的溫度與頻率曲線在那裡看得到，指一個不存在的分頁比不指更糟。
                "到歷史回放拉到那段時間，對照當時的溫度與頻率曲線。",
                "歷史回放");

        // 同一個根因不要重複算：一台過熱的機器可以同時踩到四條散熱規則，全列出來只會稀釋掉重點。
        // 已經有更強的散熱證據時，這些分數 ≤58 的次級跡象就是同一件事的側面，不再單獨占一條。
        int strongest = t.Where(x => x.Kind == BottleneckKind.Thermal).Select(x => x.Score).DefaultIfEmpty(0).Max();
        to.AddRange(t.Where(x => x.Kind != BottleneckKind.Thermal || x.Score > 58 || x.Score >= strongest));
    }

    // ── 處理器 ──────────────────────────────────────────────────────────────

    private static void Cpu(BottleneckFacts f, List<BottleneckFinding> to)
    {
        if (f.MedianCoreLoad is double med && f.CoreCount > 0 && med >= 85)
            Add(to, BottleneckKind.Cpu, "全核都吃滿了", Severity.Warning, 76,
                $"{f.CoreCount} 顆核心的負載中位數 {med:0} %"
                + (f.MaxCoreLoad is double mx ? $"，最高 {mx:0} %" : "")
                + (f.CpuClockMhz is double c ? $"，當下時脈 {c:0} MHz" : "") + "。",
                "處理器本身就是現在的上限。要知道「吃滿」是真的在算還是在等記憶體，去跑 Top-down 分析——"
                + "後端受限代表在等資料，前端受限代表在等指令，兩者要做的事完全不同。",
                "效能 → Top-down 微架構分析");

        // 一顆滿載、其餘閒著＝單執行緒卡住：加核心不會有幫助，這件事值得單獨講
        if (f.MaxCoreLoad is double hot && f.MedianCoreLoad is double rest
            && f.CoreCount >= 4 && hot >= 90 && rest <= 35)
            Add(to, BottleneckKind.Cpu, "卡在單一執行緒", Severity.Warning, 70,
                $"最忙的核心 {hot:0} %，但 {f.CoreCount} 顆核心的中位數只有 {rest:0} %。",
                "有一顆核心在滿載而其他都閒著：這種情況加核心數不會變快，只有單核效能（時脈／IPC）有用。"
                + "先確認是哪個程式——工作管理員按「CPU」排序就看得到。",
                "處理器 → 逐核歸因");

        if (f.TdBackend is double back && back >= 40)
            Add(to, BottleneckKind.Cpu, "微架構上是後端受限（在等資料）", Severity.Neutral, 64,
                $"Top-down：後端受限 {back:0.0} %"
                + (f.TdRetiring is double r ? $"、有效退休 {r:0.0} %" : "") + "。",
                "指令發不出去是因為在等記憶體或執行單元。這種負載吃的是記憶體延遲與頻寬，"
                + "換更快的記憶體／調時序通常比換 CPU 有感。",
                "記憶體 → 真實面貌");
        else if (f.TdFrontend is double front && front >= 30)
            Add(to, BottleneckKind.Cpu, "微架構上是前端受限（在等指令）", Severity.Neutral, 60,
                $"Top-down：前端受限 {front:0.0} %。",
                "取指與解碼跟不上。這多半是程式碼本身的問題（指令快取失誤、跳來跳去），使用者端能做的不多。",
                "效能 → Top-down 微架構分析");
        else if (f.TdBadSpec is double bad && bad >= 20)
            Add(to, BottleneckKind.Cpu, "微架構上大量分支預測失敗", Severity.Neutral, 58,
                $"Top-down：錯誤推測 {bad:0.0} %。",
                "有兩成以上的發射槽被丟掉重做。這是程式的分支行為造成的，換硬體幫助有限。",
                "效能 → Top-down 微架構分析");

        // 天花板量測「沒撞到任何牆」也是一種判定，但那不是卡點——只有量到限制才轉述成一條
        if (f.CeilingHasVerdict && f.CeilingHeadline.Length > 0 && f.CeilingSeverity >= Severity.Warning)
            Add(to, BottleneckKind.Power, "效能天花板量測的判定", f.CeilingSeverity, 74,
                f.CeilingHeadline,
                f.CeilingDetail.Length > 0 ? f.CeilingDetail : "到效能天花板頁看逐窗量測的細節。",
                "效能天花板");

        if (f.HasLongTerm && f.CpuLoadP95 is double lp95 && lp95 >= 90)
            Add(to, BottleneckKind.Cpu, "長期而言處理器一直很滿", Severity.Warning, 56,
                $"過去 {Span(f.HistoryMinutes)} 的處理器負載 95 百分位為 {lp95:0} %。",
                "常態性滿載。若這台機器的工作就是這樣，那是它在盡職；若不是，去找是誰在跑。",
                "歷史回放");
    }

    // ── 記憶體 ──────────────────────────────────────────────────────────────

    private static void Memory(BottleneckFacts f, List<BottleneckFinding> to)
    {
        if (f.MemLoad is double load)
        {
            double thr = f.MemLoadThreshold;
            if (load >= thr)
                Add(to, BottleneckKind.Memory, "記憶體使用率已超過你設的門檻", Severity.Serious, 84,
                    $"當下 {load:0} %"
                    + (f.MemTotalGb is double tot ? $"（共 {tot:0.0} GB）" : "")
                    + $"，你設的門檻是 {thr:0} %。",
                    "接近滿的時候系統會開始把東西挪到分頁檔，那一挪就是磁碟速度而不是記憶體速度。"
                    + "先看是哪個程式在吃；真的不夠就是加容量，調時序救不了容量。",
                    "記憶體");
            else if (load >= thr - 10)
                Add(to, BottleneckKind.Memory, "記憶體使用率接近門檻", Severity.Warning, 50,
                    $"當下 {load:0} %，你設的門檻是 {thr:0} %。", "還有餘裕，但不多。", "記憶體");
        }

        if (f.CommitGb is double commit && f.PhysicalGb is double phys && phys > 0 && commit > phys)
            Add(to, BottleneckKind.Memory, "此刻的認可量已超過實體記憶體", Severity.Serious, 82,
                $"認可 {commit:0.0} GB > 實體 {phys:0.0} GB"
                + (f.CommitLimitGb is double lim ? $"（認可上限 {lim:0.0} GB，含分頁檔）" : "") + "。",
                "超出的那部分只能靠分頁檔撐著。這是認可帳面、不代表當下真的在寫磁碟，"
                + "但一旦真的要用到那些頁面，速度會掉到磁碟等級。",
                "記憶體 → 真實面貌");
        else if (f.CommitPeakGb is double peak && f.PhysicalGb is double p2 && p2 > 0 && peak > p2)
            Add(to, BottleneckKind.Memory, "開機至今認可尖峰超過實體記憶體", Severity.Warning, 60,
                $"認可尖峰 {peak:0.0} GB > 實體 {p2:0.0} GB。",
                "曾經有一段時間記憶體是不夠的（尖峰是開機至今的累積最大值，不是現在的狀態）。"
                + "如果那段時間你有感覺到卡，答案就在這裡。",
                "記憶體 → 真實面貌");

        if (f.HasLongTerm && f.MemLoadP95 is double mp95 && mp95 >= f.MemLoadThreshold - 5)
            Add(to, BottleneckKind.Memory, "長期而言記憶體一直很滿", Severity.Warning, 55,
                $"過去 {Span(f.HistoryMinutes)} 的記憶體使用率 95 百分位為 {mp95:0} %。",
                "常態性接近滿載，加容量會是最直接的改善。", "歷史回放");
    }

    // ── 儲存 ────────────────────────────────────────────────────────────────

    private static void Storage(BottleneckFacts f, List<BottleneckFinding> to)
    {
        if (f.SystemFreePercent is double free && free >= 0)
        {
            if (free < 8)
                Add(to, BottleneckKind.Storage, "系統磁碟區快滿了", Severity.Serious, 88,
                    $"系統磁碟區剩餘 {free:0.0} %。",
                    "SSD 剩餘空間太少時寫入放大會讓速度整體下降，Windows 也會沒地方放分頁檔與更新暫存。"
                    + "先清出至少 10 % 的空間。",
                    "實用工具 → 垃圾清理");
            else if (free < 15)
                Add(to, BottleneckKind.Storage, "系統磁碟區餘量偏低", Severity.Warning, 56,
                    $"系統磁碟區剩餘 {free:0.0} %。", "還能動，但已經該清了。", "實用工具 → 大檔掃描");
        }

        if (f.WorstDiskLifePercent is double life)
        {
            if (life <= 10)
                Add(to, BottleneckKind.Storage, "有磁碟的剩餘壽命見底", Severity.Critical, 94,
                    $"最低剩餘壽命 {life:0} %（SMART 回報）。",
                    "先備份，再談其他。剩餘壽命是廠商自己算的耐寫度估計，不是保證還能撐多久。",
                    "儲存裝置");
            else if (life <= 25)
                Add(to, BottleneckKind.Storage, "有磁碟的剩餘壽命偏低", Severity.Warning, 62,
                    $"最低剩餘壽命 {life:0} %。", "開始安排備份與汰換計畫，還不用急。", "儲存裝置");
        }

        if (f.MaxDiskTempC is double dt && dt >= 65)
            Add(to, BottleneckKind.Storage, "有磁碟過熱", Severity.Warning, 64,
                $"最高磁碟溫度 {dt:0} °C。",
                "NVMe 過熱會主動降速保護自己（thermal throttling），連續讀寫時特別明顯。加散熱片或改善機殼氣流。",
                "儲存裝置");
    }

    // ── 顯示卡 ──────────────────────────────────────────────────────────────

    private static void Gpu(BottleneckFacts f, List<BottleneckFinding> to)
    {
        if (f.GpuLoad is double g && f.CpuLoad is double c && g >= 95 && c < 70)
            Add(to, BottleneckKind.Gpu, "卡在顯示卡", Severity.Neutral, 68,
                $"顯示卡負載 {g:0} % 而處理器只有 {c:0} %"
                + (f.GpuTempC is double gt ? $"，顯示卡溫度 {gt:0} °C" : "") + "。",
                "畫面端已經滿載、處理器還有餘裕：這是遊戲與繪圖的正常狀態，也代表降低畫質或解析度會直接換到幀數。",
                "顯示卡");

        if (f.GpuVramUsedGb is double used && f.GpuVramTotalGb is double total && total > 0 && used / total >= 0.92)
            Add(to, BottleneckKind.Gpu, "顯示記憶體幾乎用光", Severity.Warning, 66,
                $"已用 {used:0.0} / {total:0.0} GB（{used / total * 100:0} %）。",
                "顯示記憶體滿了之後會改用系統記憶體頂替，那條路慢很多，通常表現成突然的頓挫而不是平均掉幀。"
                + "把材質品質降一級最有效。",
                "顯示卡");
    }

    // ── 驅動程式 ────────────────────────────────────────────────────────────

    private static void Driver(BottleneckFacts f, List<BottleneckFinding> to)
    {
        if (f.MaxDpcPercent is double dpc && dpc >= 5)
            Add(to, BottleneckKind.Driver, "有核心把時間花在 DPC 上", Severity.Warning, 72,
                $"最忙的核心有 {dpc:0.0} % 的時間在跑 DPC"
                + (f.WorstDpcCore.Length > 0 ? $"（{f.WorstDpcCore}）" : "") + "。",
                "DPC 是驅動程式的延後處理。佔比高會表現成音訊爆音、滑鼠停頓、串流掉幀——"
                + "平均效能看起來正常，但體感很差。去 DPC 延遲頁看是哪個模組。",
                "實用工具 → DPC 延遲");

        if (f.MaxInterruptPercent is double isr && isr >= 10)
            Add(to, BottleneckKind.Driver, "有核心被中斷佔掉大量時間", Severity.Warning, 66,
                $"最忙的核心有 {isr:0.0} % 的時間在處理中斷。",
                "中斷率異常高通常來自某個裝置在瘋狂回報（網卡、USB 裝置、有問題的儲存控制器）。",
                "處理器 → 逐核歸因");
    }

    // ── 系統設定 ────────────────────────────────────────────────────────────

    private static void Policy(BottleneckFacts f, List<BottleneckFinding> to)
    {
        if (f.PolicyFlags.Count == 0) return;
        string list = string.Join("；", f.PolicyFlags.Select(p => $"{p.Name}＝{p.Value}"));
        Add(to, BottleneckKind.Policy, "電源計劃裡有壓住效能的設定", Severity.Warning, 58,
            (f.PowerPlanName.Length > 0 ? $"目前計劃「{f.PowerPlanName}」。" : "")
            + $"被標記的項目：{list}。",
            "這些是 Windows 的設定，不是硬體限制——改了立刻生效、也隨時改回來。"
            + "但改成全開會提高耗電與溫度，筆電上還會直接吃掉續航。",
            "處理器 → 電源政策");
    }

    // ── 平台 ────────────────────────────────────────────────────────────────

    private static void Platform(BottleneckFacts f, List<BottleneckFinding> to)
    {
        if (f.McaUncorrected is > 0)
            Add(to, BottleneckKind.Platform, "MCA 有不可修正的機器檢查事件", Severity.Critical, 98,
                $"{f.McaUncorrected} 個銀行回報不可修正事件。",
                "這是硬體層級的錯誤紀錄，比任何效能問題都優先。先備份，再對照 WHEA 紀錄與記憶體測試"
                + "（記憶體、供電、超頻設定都是常見來源）。",
                "健康 → 機器檢查架構");
        else if (f.McaCorrectedBanks is > 0)
            Add(to, BottleneckKind.Platform, "MCA 有已修正的錯誤計數", Severity.Warning, 60,
                $"{f.McaCorrectedBanks} 個銀行有已修正錯誤。",
                "已修正＝這次被硬體救回來了。單看一次不代表壞掉，但計數往上跑是記憶體／記憶體控制器劣化的最早訊號，"
                + "值得每隔一段時間回來對照數字。",
                "健康 → 機器檢查架構");
    }

    // ── 誠實界線 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 本次沒有納入判斷的資料源，一項一行，附上該去哪一頁量。
    /// 這一段的存在本身就是結論的一部分：讀者才分得出「檢查過沒問題」與「根本沒檢查」。
    /// </summary>
    private static List<string> Unknowns(BottleneckFacts f)
    {
        var u = new List<string>();

        if (f.StickyUnreliable)
            u.Add("黏滯節流位元整個暫存器讀成 0，可信度不足——本次沒有用它判斷溫度牆／功耗牆（處理器頁 → 黏滯節流位元）。");
        else if (f.ThermalLogSeen is null)
            u.Add("還沒讀黏滯節流位元，因此不知道開機至今有沒有撞過溫度牆或功耗牆（處理器頁 → 黏滯節流位元）。");

        if (f.CpuTempC is null)
            u.Add("讀不到處理器溫度——溫度相關的判斷全部略過（多半是缺少驅動權限或該平台沒有暴露此感測）。");

        if (f.CpuPowerW is null)
            u.Add("讀不到封裝功耗，無法看出離功耗牆多遠（感測器）。");

        if (f.MaxDpcPercent is null)
            u.Add("還沒做逐核歸因，因此不知道有沒有驅動程式在 DPC／中斷上吃掉時間（處理器 → 逐核歸因）。");

        if (f.TdBackend is null)
            u.Add("還沒跑 Top-down 微架構分析，無法分辨處理器是「算不完」還是「在等記憶體」（效能 → Top-down 微架構分析）。");

        if (!f.CeilingHasVerdict)
            u.Add("還沒做效能天花板量測，因此沒有逐窗撞牆的證據，只有此刻的即時讀值（效能天花板）。");

        if (f.McaUncorrected is null)
            u.Add("還沒讀 MCA 銀行，因此不知道有沒有硬體層級的機器檢查事件（健康 → 機器檢查架構）。");

        if (f.CommitGb is null)
            u.Add("還沒讀記憶體真實面貌，因此只看得到使用率、看不到認可量與認可尖峰（記憶體 → 真實面貌）。");

        if (f.GpuLoad is null)
            u.Add("讀不到顯示卡負載——無法判斷是不是卡在畫面端（顯示卡）。");

        if (!f.HasLongTerm)
            u.Add(f.HistoryMinutes <= 0
                ? "沒有歷史樣本，所以只看得到此刻。卡點常常只出現在負載上來的那幾秒——讓程式在背景多跑一陣子會準得多。"
                : $"歷史樣本只有 {Span(f.HistoryMinutes)}，還不足以談長期趨勢（本頁的長期判斷需要 30 分鐘以上）。");

        return u;
    }

    /// <summary>可信度說明：講清楚這份結論建立在多少資料上，而不是給一個沒有依據的百分比。</summary>
    private static string Confidence(BottleneckFacts f, int unknownCount)
    {
        int live = 0;
        if (f.CpuLoad is not null) live++;
        if (f.CpuTempC is not null) live++;
        if (f.CpuPowerW is not null) live++;
        if (f.MemLoad is not null) live++;
        if (f.GpuLoad is not null) live++;
        if (f.MaxDiskTempC is not null || f.WorstDiskLifePercent is not null) live++;

        string deep = f.StickyUsable || f.MaxDpcPercent is not null || f.TdBackend is not null
                      || f.CeilingHasVerdict || f.McaUncorrected is not null || f.CommitGb is not null
            ? "已有深入量測進帳"
            : "只有即時讀值，沒有任何深入量測";

        return $"本次用了 {live} 類即時讀值、"
             + (f.HasLongTerm ? $"{Span(f.HistoryMinutes)} 的歷史樣本" : "沒有足夠的歷史樣本")
             + $"，{deep}"
             + (unknownCount > 0 ? $"；另有 {unknownCount} 項尚未納入（列在下面）。" : "。");
    }

    /// <summary>把分鐘數講成人話：未滿一小時講分鐘，超過講小時。</summary>
    internal static string Span(int minutes)
        => minutes <= 0 ? "不到 1 分鐘"
         : minutes < 60 ? $"{minutes} 分鐘"
         : minutes % 60 == 0 ? $"{minutes / 60} 小時"
         : $"{minutes / 60} 小時 {minutes % 60} 分鐘";
}
