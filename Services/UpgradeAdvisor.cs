namespace XinSpect;

/// <summary>升級建議所指的部位（顯示為徽章文字）。</summary>
public enum UpgradePart { Storage, Memory, Cpu, Gpu, Cooling, System }

/// <summary>
/// 一條升級建議：為什麼（證據）、要做什麼（行動）、值不值得（預期效益）。
/// 證據一律來自本機真實讀值；預期效益是同類升級的經驗範圍，不是本機實測結果。
/// </summary>
public sealed class UpgradeSuggestion
{
    public required UpgradePart Part { get; init; }
    public required string Title { get; init; }
    public required Severity Severity { get; init; }

    /// <summary>優先度權重（0–100，越大越該先做），亦為排序鍵。</summary>
    public required int Score { get; init; }

    /// <summary>預期效益（經驗範圍）。</summary>
    public required string Gain { get; init; }

    /// <summary>判斷依據：本機的真實讀值。</summary>
    public required string Evidence { get; init; }

    /// <summary>具體怎麼做。</summary>
    public required string Action { get; init; }

    /// <summary>花費層級：免費（只改設定）／低／中／高。</summary>
    public string Cost { get; init; } = "中";

    public string PartText => Part switch
    {
        UpgradePart.Storage => "儲存",
        UpgradePart.Memory => "記憶體",
        UpgradePart.Cpu => "處理器",
        UpgradePart.Gpu => "顯示卡",
        UpgradePart.Cooling => "散熱",
        _ => "系統",
    };

    /// <summary>優先度文字（依 <see cref="Score"/> 分級）。</summary>
    public string PriorityText => Score >= 90 ? "最優先" : Score >= 70 ? "建議" : Score >= 45 ? "可考慮" : "選配";

    public string CostText => "花費：" + Cost;
    public string GainText => "預期效益：" + Gain;

}

/// <summary>
/// 餵給規則引擎的事實：一份與硬體讀取完全脫鉤的純資料。
/// 由 <c>UpgradeFactsBuilder</c> 從主檢視模型（畫面上同一份真實讀值）填入，
/// 因此規則本身可以離線單元測試，不必有硬體。
/// </summary>
/// <remarks>可為 null 的欄位代表「本機沒有這項資料」，規則遇到 null 一律略過，不猜測。</remarks>
public sealed class UpgradeFacts
{
    // ── 記憶體 ──────────────────────────────────────────────────────────
    public double MemTotalGb { get; set; }
    public int MemModules { get; set; }
    public string MemChannelsText { get; set; } = "";
    public int MemSpeedMhz { get; set; }
    public int MemRatedMhz { get; set; }
    /// <summary>長期記憶體使用率 95 百分位（%）。</summary>
    public double? MemLoadP95 { get; set; }

    // ── 處理器 ──────────────────────────────────────────────────────────
    public string CpuName { get; set; } = "";
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public double? CpuLoadP95 { get; set; }
    public double? CpuTempP95 { get; set; }
    public double? CpuTempMax { get; set; }
    /// <summary>近期是否記錄到熱降頻事件。</summary>
    public bool ThrottleSeen { get; set; }
    /// <summary>天梯名次與該子榜總筆數（0 表示未在榜單命中）。</summary>
    public int CpuRank { get; set; }
    public int CpuRankTotal { get; set; }

    // ── 顯示卡 ──────────────────────────────────────────────────────────
    public string GpuName { get; set; } = "";
    public bool HasGpu { get; set; }
    public bool HasDiscreteGpu { get; set; }
    public double GpuVramGb { get; set; }
    public double? GpuLoadP95 { get; set; }
    public double? GpuTempP95 { get; set; }
    public int GpuRank { get; set; }
    public int GpuRankTotal { get; set; }

    // ── 儲存 ────────────────────────────────────────────────────────────
    /// <summary>磁碟 0（一般即系統碟）是否為機械硬碟。</summary>
    public bool SystemDiskIsHdd { get; set; }
    public int HddCount { get; set; }
    public int DiskCount { get; set; }
    /// <summary>系統磁碟區剩餘空間百分比（-1 表示未知）。</summary>
    public double SystemFreePercent { get; set; } = -1;
    /// <summary>所有磁碟中最低的剩餘壽命（%）。</summary>
    public double? WorstDiskLife { get; set; }
    public bool DiskHealthWarning { get; set; }
    public string DiskHealthDetail { get; set; } = "";
    public double? MaxDiskTempC { get; set; }

    // ── 系統 ────────────────────────────────────────────────────────────
    public string PowerPlan { get; set; } = "";
    public bool IsLaptop { get; set; }
    /// <summary>歷史樣本涵蓋的分鐘數（0 表示沒有歷史資料，長期判斷一律略過）。</summary>
    public int HistoryMinutes { get; set; }

    /// <summary>是否有足夠的長期樣本可下「長期表現」的結論（至少 30 分鐘）。</summary>
    public bool HasLongTerm => HistoryMinutes >= 30;
}

/// <summary>一次分析的結果：瓶頸判定、可信度說明，以及依優先度排序的建議清單。</summary>
public sealed class UpgradeReport
{
    public string Bottleneck { get; init; } = "";
    public string BottleneckDetail { get; init; } = "";
    public Severity BottleneckSeverity { get; init; } = Severity.Neutral;
    /// <summary>結論的可信度說明（樣本涵蓋多久、哪些項目缺資料）。</summary>
    public string Confidence { get; init; } = "";
    public IReadOnlyList<UpgradeSuggestion> Items { get; init; } = [];

    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// 升級建議規則引擎：吃一份 <see cref="UpgradeFacts"/>，吐出依優先度排序的建議。
/// 純函式、無副作用、不碰硬體，因此可完整單元測試。
/// </summary>
/// <remarks>
/// 兩條鐵則：
/// 一、沒有資料就不下結論——欄位為 null 或缺歷史樣本時直接略過該規則，絕不以「一般情況」補值；
/// 二、預期效益寫的是同類升級的經驗範圍（並標明如此），不假裝是本機實測。
/// </remarks>
public static class UpgradeAdvisor
{
    /// <summary>視為「長期吃滿」的負載門檻（95 百分位，%）。</summary>
    private const double Saturated = 88;

    /// <summary>視為「相對閒置」的負載門檻（95 百分位，%）。</summary>
    private const double Idle = 60;

    public static UpgradeReport Analyze(UpgradeFacts f)
    {
        var items = new List<UpgradeSuggestion>();

        Storage(f, items);
        Memory(f, items);
        Thermal(f, items);
        Compute(f, items);
        System(f, items);

        // 同分時維持加入順序（穩定排序），讓輸出可預期
        var sorted = items.OrderByDescending(s => s.Score).ToList();
        var (name, detail, sev) = Bottleneck(f);

        return new UpgradeReport
        {
            Bottleneck = name,
            BottleneckDetail = detail,
            BottleneckSeverity = sev,
            Confidence = Confidence(f),
            Items = sorted,
        };
    }

    // ── 儲存規則 ────────────────────────────────────────────────────────

    private static void Storage(UpgradeFacts f, List<UpgradeSuggestion> items)
    {
        // 壽命或健康告警優先於一切效能升級：資料安全先於速度
        if (f.WorstDiskLife is double life && life < 10)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Storage, Title = "更換即將壽終的固態硬碟", Severity = Severity.Critical, Score = 100,
                Gain = "避免資料損失（非效能升級）", Cost = "低",
                Evidence = $"S.M.A.R.T. 回報剩餘壽命僅 {life:0} %",
                Action = "先完整備份，再換一顆新的 NVMe SSD；舊碟不要繼續當系統碟使用。",
            });
        else if (f.DiskHealthWarning)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Storage, Title = "磁碟健康狀態異常，建議更換", Severity = Severity.Serious, Score = 95,
                Gain = "避免資料損失（非效能升級）", Cost = "低",
                Evidence = f.DiskHealthDetail.Length > 0 ? $"磁碟健康：{f.DiskHealthDetail}" : "S.M.A.R.T. 回報健康狀態異常",
                Action = "立刻備份重要資料，並安排更換該顆磁碟。",
            });

        if (f.SystemDiskIsHdd)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Storage, Title = "系統碟換成 NVMe 固態硬碟", Severity = Severity.Serious, Score = 92,
                Gain = "開機與程式啟動快 5–10 倍（同類升級經驗值）", Cost = "低",
                Evidence = "磁碟 0（系統碟）為機械硬碟",
                Action = "換一顆 500 GB 以上的 NVMe SSD 當系統碟，機械硬碟留作資料碟。",
            });
        else if (f.HddCount > 0 && f.DiskCount > f.HddCount)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Storage, Title = "把常用資料從機械硬碟搬到固態硬碟", Severity = Severity.Warning, Score = 48,
                Gain = "常用資料讀取延遲降低一個數量級", Cost = "低",
                Evidence = $"本機共 {f.DiskCount} 顆磁碟，其中 {f.HddCount} 顆為機械硬碟",
                Action = "把遊戲、專案與素材等常讀取的資料移到 SSD，機械硬碟只放冷資料與備份。",
            });

        if (f.SystemFreePercent >= 0 && f.SystemFreePercent < 10)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Storage, Title = "系統碟空間已接近用盡", Severity = Severity.Serious, Score = 88,
                Gain = "回復正常寫入速度與系統穩定度", Cost = "免費",
                Evidence = $"系統磁碟區剩餘空間僅 {f.SystemFreePercent:0.#} %",
                Action = "先用工具箱的空間清理釋出容量；長期不足則加大容量。固態硬碟低於一成空間時寫入速度會明顯衰退。",
            });
        else if (f.SystemFreePercent >= 0 && f.SystemFreePercent < 20)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Storage, Title = "系統碟空間偏緊", Severity = Severity.Warning, Score = 42,
                Gain = "保留固態硬碟的寫入餘裕", Cost = "免費",
                Evidence = $"系統磁碟區剩餘空間 {f.SystemFreePercent:0.#} %",
                Action = "清理暫存與休眠檔，把剩餘空間拉回兩成以上。",
            });

        if (f.MaxDiskTempC is double dt && dt >= 65)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Storage, Title = "為固態硬碟加裝散熱片", Severity = Severity.Warning, Score = 55,
                Gain = "避免高溫降速（NVMe 過熱會主動限速）", Cost = "低",
                Evidence = $"磁碟最高溫達 {dt:0} °C",
                Action = "加裝 M.2 散熱片或改善機殼風道，讓磁碟溫度回到 60 °C 以下。",
            });
    }

    // ── 記憶體規則 ──────────────────────────────────────────────────────

    private static void Memory(UpgradeFacts f, List<UpgradeSuggestion> items)
    {
        if (f.MemTotalGb <= 0) return;   // 沒讀到容量就不談記憶體

        bool pressure = f.HasLongTerm && f.MemLoadP95 is double p && p >= 85;
        string pressureText = f.MemLoadP95 is double mp
            ? $"，長期使用率 95% 為 {mp:0} %"
            : "";

        if (f.MemTotalGb <= 8.5)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Memory, Title = "記憶體加到 16 GB", Severity = pressure ? Severity.Serious : Severity.Warning,
                Score = pressure ? 90 : 72,
                Gain = "多工與大型軟體不再因換頁卡頓", Cost = "低",
                Evidence = $"目前總容量 {f.MemTotalGb:0.#} GB（{f.MemModules} 支）{pressureText}",
                Action = "加購同規格記憶體到 16 GB；插滿兩通道效果最好。",
            });
        else if (f.MemTotalGb <= 16.5 && pressure)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Memory, Title = "記憶體加到 32 GB", Severity = Severity.Warning, Score = 68,
                Gain = "消除長期記憶體壓力造成的卡頓", Cost = "中",
                Evidence = $"總容量 {f.MemTotalGb:0.#} GB，但長期使用率 95% 已達 {f.MemLoadP95:0} %",
                Action = "加到 32 GB；若板上只有兩槽，直接換一組 2×16 GB。",
            });

        // 單支記憶體＝單通道，頻寬直接砍半，對內顯機器影響尤其大
        if (f.MemModules == 1)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Memory, Title = "補第二支記憶體組成雙通道",
                Severity = f.HasDiscreteGpu ? Severity.Warning : Severity.Serious,
                Score = f.HasDiscreteGpu ? 74 : 86,
                Gain = f.HasDiscreteGpu ? "記憶體頻寬翻倍，遊戲與轉檔約 5–15 %" : "內顯效能提升可達 20–40 %",
                Cost = "低",
                Evidence = $"僅偵測到 1 支記憶體模組（{f.MemTotalGb:0.#} GB）"
                           + (f.MemChannelsText.Length > 0 ? $"，通道回報：{f.MemChannelsText}" : ""),
                Action = "加一支容量／規格相同的記憶體，插在主機板標示的雙通道對位插槽。",
            });

        // XMP/EXPO 沒開：買到的頻率沒吃到，屬於免費效能
        if (f.MemRatedMhz > 0 && f.MemSpeedMhz > 0 && f.MemSpeedMhz < f.MemRatedMhz * 0.9)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Memory, Title = "在 BIOS 開啟 XMP / EXPO", Severity = Severity.Warning, Score = 64,
                Gain = "免費拿回已購買的頻率，遊戲與轉檔約 3–10 %", Cost = "免費",
                Evidence = $"目前執行 {f.MemSpeedMhz} MHz，模組標定 {f.MemRatedMhz} MHz",
                Action = "進 BIOS 開啟 XMP（Intel）或 EXPO / DOCP（AMD）設定檔，開機後回本頁確認頻率已提升。",
            });
    }

    // ── 散熱規則 ────────────────────────────────────────────────────────

    private static void Thermal(UpgradeFacts f, List<UpgradeSuggestion> items)
    {
        if (f.ThrottleSeen)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Cooling, Title = "先解決熱降頻，再談換硬體", Severity = Severity.Serious, Score = 94,
                Gain = "拿回被溫度壓掉的效能（通常 10–30 %）", Cost = "低",
                Evidence = "事件紀錄裡有熱降頻"
                           + (f.CpuTempMax is double m ? $"，處理器最高溫 {m:0} °C" : ""),
                Action = f.IsLaptop
                    ? "清理出風口與散熱鰭片、重塗散熱膏，並確認進風口沒有被墊住。"
                    : "重塗散熱膏、升級塔散或水冷，並整理機殼風道（前進後出）。",
            });
        else if (f.HasLongTerm && f.CpuTempP95 is double t95 && t95 >= 88)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Cooling, Title = "加強處理器散熱", Severity = Severity.Warning, Score = 76,
                Gain = "溫度牆解除後可維持較高的全核頻率", Cost = "低",
                Evidence = $"長期處理器溫度 95% 為 {t95:0} °C（已貼近溫度牆）",
                Action = "重塗散熱膏並升級散熱器；桌機亦可補機殼風扇改善風道。",
            });

        if (f.HasLongTerm && f.GpuTempP95 is double g95 && g95 >= 83)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Cooling, Title = "改善顯示卡散熱", Severity = Severity.Warning, Score = 58,
                Gain = "避免顯示卡因高溫自動降頻", Cost = "低",
                Evidence = $"長期顯示卡溫度 95% 為 {g95:0} °C",
                Action = "清理顯示卡風扇與鰭片，調整風扇曲線，或在機殼側面補一顆進風扇。",
            });
    }

    // ── 運算瓶頸規則（處理器 / 顯示卡）──────────────────────────────────

    private static void Compute(UpgradeFacts f, List<UpgradeSuggestion> items)
    {
        if (f.HasGpu && !f.HasDiscreteGpu)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Gpu, Title = "加一張獨立顯示卡", Severity = Severity.Warning, Score = 70,
                Gain = "3D 與影像編碼效能提升數倍（視型號而定）", Cost = "高",
                Evidence = $"目前只有內建顯示：{(f.GpuName.Length > 0 ? f.GpuName : "內建顯示")}",
                Action = f.IsLaptop
                    ? "筆電無法加裝顯示卡；有需要可外接雷電顯卡盒，或在換機時選有獨顯的機型。"
                    : "確認電源瓦數與機殼長度後，選一張與處理器等級相稱的獨立顯示卡。",
            });

        if (!f.HasLongTerm) return;   // 以下全部依賴長期樣本，沒樣本就不猜

        bool cpuHot = f.CpuLoadP95 is double c && c >= Saturated;
        bool gpuHot = f.GpuLoadP95 is double g && g >= Saturated;
        bool cpuCool = f.CpuLoadP95 is double c2 && c2 < Idle;
        bool gpuCool = f.GpuLoadP95 is double g2 && g2 < Idle;

        if (cpuHot && (gpuCool || !f.HasGpu))
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Cpu, Title = "處理器是主要瓶頸，建議升級", Severity = Severity.Warning,
                Score = 82,
                Gain = "換到高一級的處理器，受限工作可快 20–50 %", Cost = "高",
                Evidence = $"長期處理器負載 95% 達 {f.CpuLoadP95:0} %"
                           + (f.GpuLoadP95 is double gv ? $"，同期顯示卡僅 {gv:0} %" : "")
                           + $"（{f.CpuName}，{f.CpuCores} 核 {f.CpuThreads} 執行緒）",
                Action = "先確認主機板可支援的最高型號，優先在同腳位內換更高核心數的處理器；無路可走再考慮換平台。",
            });

        if (gpuHot && cpuCool)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Gpu, Title = "顯示卡是主要瓶頸，建議升級", Severity = Severity.Warning,
                Score = 80,
                Gain = "換到高一級的顯示卡，畫面效能提升 30–70 %", Cost = "高",
                Evidence = $"長期顯示卡負載 95% 達 {f.GpuLoadP95:0} %，同期處理器僅 {f.CpuLoadP95:0} %"
                           + (f.GpuName.Length > 0 ? $"（{f.GpuName}）" : ""),
                Action = "以現有電源瓦數與機殼空間為上限挑選新顯示卡；處理器還很閒，換卡的收益最直接。",
            });

        // 顯示記憶體不足：負載滿載但容量偏小，先點出來
        if (gpuHot && f.GpuVramGb > 0 && f.GpuVramGb <= 4.5)
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.Gpu, Title = "顯示記憶體偏小，選卡時以容量優先", Severity = Severity.Warning, Score = 60,
                Gain = "避免材質爆量造成的掉幀與貼圖延遲", Cost = "高",
                Evidence = $"顯示記憶體 {f.GpuVramGb:0.#} GB，長期負載 95% 已達 {f.GpuLoadP95:0} %",
                Action = "下一張卡至少選 8 GB 以上顯示記憶體；顯示記憶體無法單獨加裝。",
            });

        // 天梯定位：只有真的在榜單命中才談名次
        if (f.CpuRank > 0 && f.CpuRankTotal > 0)
        {
            double pct = 100.0 * f.CpuRank / f.CpuRankTotal;
            if (pct > 70)
                items.Add(new UpgradeSuggestion
                {
                    Part = UpgradePart.Cpu, Title = "處理器在天梯偏後段", Severity = Severity.Neutral, Score = 40,
                    Gain = "換到榜單中段可有明顯世代差", Cost = "高",
                    Evidence = $"{f.CpuName} 在天梯排第 {f.CpuRank} / {f.CpuRankTotal}（後 {100 - pct:0} %）",
                    Action = "先確認主機板支援清單，再挑同腳位可換的較高型號；天梯分數僅供參考。",
                });
        }

        if (f.GpuRank > 0 && f.GpuRankTotal > 0)
        {
            double pct = 100.0 * f.GpuRank / f.GpuRankTotal;
            if (pct > 70)
                items.Add(new UpgradeSuggestion
                {
                    Part = UpgradePart.Gpu, Title = "顯示卡在天梯偏後段", Severity = Severity.Neutral, Score = 38,
                    Gain = "換到榜單中段的畫面效能差距最有感", Cost = "高",
                    Evidence = $"{f.GpuName} 在天梯排第 {f.GpuRank} / {f.GpuRankTotal}（後 {100 - pct:0} %）",
                    Action = "以電源瓦數與機殼空間為上限選卡；天梯分數僅供參考。",
                });
        }
    }

    // ── 系統設定規則（免費項目）──────────────────────────────────────────

    private static void System(UpgradeFacts f, List<UpgradeSuggestion> items)
    {
        if (f.PowerPlan.Contains("節能"))
            items.Add(new UpgradeSuggestion
            {
                Part = UpgradePart.System, Title = "電源計劃改回平衡或高效能", Severity = Severity.Warning, Score = 66,
                Gain = "免費拿回被限制的頻率（可達 10–30 %）", Cost = "免費",
                Evidence = $"目前電源計劃為「{f.PowerPlan}」",
                Action = "到場景頁一鍵切換到均衡或效能取向；插電使用時沒有必要停在節能。",
            });
    }

    // ── 瓶頸判定與可信度 ────────────────────────────────────────────────

    private static (string Name, string Detail, Severity Sev) Bottleneck(UpgradeFacts f)
    {
        if (f.WorstDiskLife is double life && life < 10)
            return ("磁碟壽命", $"S.M.A.R.T. 剩餘壽命僅 {life:0} %，資料安全優先於任何效能升級。", Severity.Critical);

        if (f.SystemDiskIsHdd)
            return ("系統碟", "系統安裝在機械硬碟上，這是目前最拖慢整機體感的環節。", Severity.Serious);

        if (f.ThrottleSeen)
            return ("散熱", "已記錄到熱降頻——硬體的效能正被溫度壓住，換零件前先處理散熱。", Severity.Serious);

        if (f.SystemFreePercent >= 0 && f.SystemFreePercent < 10)
            return ("系統碟空間", $"剩餘空間僅 {f.SystemFreePercent:0.#} %，固態硬碟在此水位會明顯掉速。", Severity.Serious);

        if (f.MemTotalGb > 0 && f.MemTotalGb <= 8.5)
            return ("記憶體容量", $"總容量 {f.MemTotalGb:0.#} GB，現代軟體下最容易先撞到的就是這一項。", Severity.Warning);

        if (f.MemModules == 1 && f.MemTotalGb > 0)
            return ("記憶體通道", "只有單支記憶體，頻寬僅有雙通道的一半。", Severity.Warning);

        if (!f.HasLongTerm)
            return ("尚無定論", "長期樣本不足（少於 30 分鐘），還無法判定運算瓶頸。先讓曦覽持續累積歷史，再回來看這一頁。",
                    Severity.Neutral);

        bool cpuHot = f.CpuLoadP95 is double c && c >= Saturated;
        bool gpuHot = f.GpuLoadP95 is double g && g >= Saturated;

        if (cpuHot && gpuHot)
            return ("處理器與顯示卡同時吃滿",
                    $"長期處理器 95% {f.CpuLoadP95:0} %、顯示卡 95% {f.GpuLoadP95:0} %——兩邊都在極限，屬於整機等級不足。",
                    Severity.Warning);

        if (cpuHot)
            return ("處理器", $"長期處理器負載 95% 達 {f.CpuLoadP95:0} %，顯示卡尚有餘裕。", Severity.Warning);

        if (gpuHot)
            return ("顯示卡", $"長期顯示卡負載 95% 達 {f.GpuLoadP95:0} %，處理器尚有餘裕。", Severity.Warning);

        if (f.MemLoadP95 is double mp && mp >= 85)
            return ("記憶體", $"長期記憶體使用率 95% 為 {mp:0} %，容量已成為限制。", Severity.Warning);

        return ("無明顯瓶頸",
                "在已累積的樣本裡，處理器、顯示卡與記憶體都還有餘裕——目前的用法下這台機器並不缺效能。",
                Severity.Good);
    }

    private static string Confidence(UpgradeFacts f)
    {
        var parts = new List<string>();

        parts.Add(f.HistoryMinutes <= 0
            ? "沒有歷史樣本，長期判斷全部略過"
            : f.HistoryMinutes < 60
                ? $"歷史樣本僅涵蓋約 {f.HistoryMinutes} 分鐘，長期結論仍偏粗略"
                : f.HistoryMinutes < 1440
                    ? $"歷史樣本涵蓋約 {f.HistoryMinutes / 60} 小時"
                    : $"歷史樣本涵蓋約 {f.HistoryMinutes / 1440} 天");

        if (!f.HasGpu) parts.Add("未偵測到顯示卡讀值");
        if (f.MemRatedMhz <= 0) parts.Add("記憶體未提供標定頻率，無法判斷 XMP");
        if (f.WorstDiskLife is null) parts.Add("磁碟未提供剩餘壽命");
        if (f.CpuRank <= 0) parts.Add("處理器未在天梯榜單命中，跳過名次比較");

        return string.Join("；", parts) + "。預期效益為同類升級的經驗範圍，非本機實測。";
    }
}
