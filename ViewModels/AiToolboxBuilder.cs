using System.Text;

namespace XinSpect;

/// <summary>
/// 為 AI 診斷代理組出本機工具箱。所有工具都是「唯讀查詢」：直接讀主檢視模型上、與畫面同一份的
/// 即時物件，整理成文字回給模型。沒有任何一個工具會寫入硬體、改設定或啟動外部程式——
/// 代理能「看」整台機器，但不能「動」它。
/// </summary>
internal static partial class AiToolboxBuilder
{
    public static AiToolbox Build(MainViewModel vm)
    {
        var box = new AiToolbox();

        AddSpecs(box, vm);
        AddLive(box, vm);
        AddSensors(box, vm);
        AddFans(box, vm);
        AddStorage(box, vm);
        AddEvents(box, vm);
        AddHistory(box, vm);
        AddTuning(box, vm);
        AddDiagnostics(box, vm);

        // 第二批工具（AiToolboxBuilder.Extra.cs）
        AddMemory(box, vm);
        AddNetwork(box, vm);
        AddDisplay(box, vm);
        AddRanking(box, vm);
        AddUpgrade(box, vm);
        AddPortable(box);
        AddStartup(box);
        AddBsod(box);

        // 第三批：硬核唯讀單元（AiToolboxBuilder.Hardcore.cs）。
        // 這批多半要使用者先開到對應頁面按量測，沒量過的工具會明說「尚未量測」而非回傳 0。
        AddHardcore(box, vm);

        return box;
    }

    // ── 共用小工具 ───────────────────────────────────────────────────────────

    private static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "—") sb.AppendLine($"{label}：{value}");
    }

    private static string Done(StringBuilder sb, string emptyText)
        => sb.Length == 0 ? emptyText : sb.ToString().TrimEnd();

    /// <summary>把跑分的「與上次相比／重複性／量測條件」串成尾註，空白項目自動略過。</summary>
    private static string Tail(params string?[] notes)
    {
        var kept = new List<string>(notes.Length);
        foreach (var n in notes)
            if (!string.IsNullOrWhiteSpace(n) && n != "—") kept.Add(n);
        return kept.Count == 0 ? "" : "；" + string.Join("・", kept);
    }

    // ── 規格 ────────────────────────────────────────────────────────────────

    private static void AddSpecs(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_specs",
            "取得本機完整硬體規格與即時數據快照：作業系統、機型、主機板、BIOS、處理器、記憶體模組與時序、"
            + "顯示卡、磁碟區與實體磁碟、健康總評。想了解這台電腦「是什麼」時先用這個。",
            _ => AiSnapshotBuilder.Build(vm));

        box.Add("get_cpu_topology",
            "取得處理器的拓樸與快取細節：實體封裝數、實體核心／邏輯處理器數、SMT、NUMA 節點、"
            + "處理器群組、各級快取組態，以及平台回報的指令集能力。",
            _ =>
            {
                var t = vm.CpuTopology;
                var sb = new StringBuilder();
                Line(sb, "處理器", vm.Cpu.Name);
                if (!t.Loaded) { sb.AppendLine("（拓樸資訊尚未讀取完成）"); return Done(sb, "（無資料）"); }
                Line(sb, "實體封裝", t.PackagesText);
                Line(sb, "實體核心", t.CoresText);
                Line(sb, "邏輯處理器", t.LogicalText);
                Line(sb, "同時多執行緒", t.SmtText);
                Line(sb, "NUMA 節點", t.NumaText);
                Line(sb, "處理器群組", t.GroupsText);
                foreach (var c in t.Caches) Line(sb, c.Label, c.Detail);
                Line(sb, "平台能力", t.FeaturesText);
                return Done(sb, "（無資料）");
            });
    }

    // ── 即時讀值 ────────────────────────────────────────────────────────────

    private static void AddLive(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_live_metrics",
            "取得此刻的即時讀值：處理器負載／溫度／頻率／功耗／電壓、記憶體使用、"
            + "各顯示卡的負載／溫度／頻率／顯示記憶體／功耗，以及各磁碟的溫度與活動時間。"
            + "要判斷「現在的狀態」時用這個。",
            _ =>
            {
                if (vm.Live is not SensorService live) return "（感測器尚未初始化，暫無即時讀值）";
                var sb = new StringBuilder();
                sb.AppendLine($"取樣時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Line(sb, "處理器", live.CpuName);
                Line(sb, "處理器負載", live.CpuLoadText);
                Line(sb, "處理器溫度", live.CpuTempText);
                Line(sb, "處理器頻率", live.CpuClockText);
                Line(sb, "處理器功耗", live.CpuPowerText);
                Line(sb, "處理器電壓", live.CpuVoltText);
                Line(sb, "外頻", live.CpuBusText);
                if (live.VrmTempC is double vrm) Line(sb, "供電模組溫度", $"{vrm:0} °C");
                Line(sb, "記憶體使用率", live.MemLoadText);
                Line(sb, "記憶體用量", live.MemUsageText);

                foreach (var g in live.Gpus)
                    sb.AppendLine($"顯示卡「{g.Name}」：負載 {g.LoadText}・溫度 {g.TempText}"
                                  + $"・核心 {g.CoreClockText}・顯示記憶體 {g.MemClockText}"
                                  + $"・用量 {g.VramText}・功耗 {g.PowerText}・風扇 {g.FanText}");

                foreach (var d in live.Drives)
                    sb.AppendLine($"磁碟「{d.DisplayModel}」：溫度 {d.TempText}・活動 {d.ActivityText}"
                                  + $"・剩餘壽命 {d.LifeText}");

                if (live.CpuCores.Count > 0)
                {
                    var hottest = live.CpuCores.Where(c => c.TempC.HasValue).OrderByDescending(c => c.TempC).FirstOrDefault();
                    if (hottest is not null) Line(sb, "最熱核心", $"{hottest.Name} {hottest.TempText}");
                    Line(sb, "核心頻率範圍",
                        $"{live.CpuCores.Min(c => c.ClockMHz):0} – {live.CpuCores.Max(c => c.ClockMHz):0} MHz");
                }
                return Done(sb, "（無可用讀值）");
            });
    }

    // ── 感測器總表 ──────────────────────────────────────────────────────────

    // 感測器總表平時不格式化（每秒最重的一段），僅在該分頁可見時更新。工具需要時暫時打開旗標
    // 重新格式化一次再還原：只是重新讀「已經取樣好」的值，不會多跑一次硬體輪詢。
    private static void RefreshSensorTable(SensorService live)
    {
        if (live.DetailedSensorsVisible) return;
        try
        {
            live.DetailedSensorsVisible = true;
            live.Publish();
        }
        finally { live.DetailedSensorsVisible = false; }
    }

    private static void AddSensors(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_temperatures",
            "取得所有溫度感測器的當前值（處理器封裝與各核心、主機板／供電模組、顯示卡、磁碟等），"
            + "含開機以來的最低與最高值。要診斷散熱時用這個。",
            _ =>
            {
                if (vm.Live is not SensorService live) return "（感測器尚未初始化）";
                RefreshSensorTable(live);
                var sb = new StringBuilder();
                foreach (var r in live.AllSensors.Where(r => r.TypeText == "溫度"))
                    sb.AppendLine($"{r.Group}／{r.Name}：現在 {r.ValueText}（最低 {r.MinText}、最高 {r.MaxText}）");
                return Done(sb, "（此機器未回報任何溫度感測器）");
            });

        box.Add("get_sensor_table",
            "查詢完整感測器總表（溫度、負載、頻率、電壓、功耗、風扇轉速、資料量等全部項目）。"
            + "可用 keyword 過濾群組或名稱，例如 \"電壓\"、\"風扇\"、\"GPU\"。項目很多，建議加上 keyword。",
            args =>
            {
                if (vm.Live is not SensorService live) return "（感測器尚未初始化）";
                RefreshSensorTable(live);
                string kw = AiToolbox.StringArg(args, "keyword") ?? "";
                var rows = live.AllSensors.AsEnumerable();
                if (kw.Length > 0)
                    rows = rows.Where(r => r.Group.Contains(kw, StringComparison.OrdinalIgnoreCase)
                                        || r.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                                        || r.TypeText.Contains(kw, StringComparison.OrdinalIgnoreCase));

                var sb = new StringBuilder();
                int n = 0;
                foreach (var r in rows.Take(220))
                {
                    sb.AppendLine($"{r.Group}／{r.Name}（{r.TypeText}）：{r.ValueText}"
                                  + $"（最低 {r.MinText}、最高 {r.MaxText}）");
                    n++;
                }
                if (n == 0) return kw.Length > 0 ? $"（沒有符合「{kw}」的感測器項目）" : "（感測器總表為空）";
                return sb.ToString().TrimEnd();
            },
            """{"type":"object","properties":{"keyword":{"type":"string","description":"過濾字串（群組、名稱或類型），留空則列出全部"}}}""");
    }

    // ── 風扇 ────────────────────────────────────────────────────────────────

    private static void AddFans(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_fan_state",
            "取得系統風扇的現況：每顆可控風扇的目前輸出％與轉速、是由 BIOS 自動或曦覽手動接管、"
            + "以及每條溫度→轉速曲線的啟用狀態、控制來源與轉折點。",
            _ =>
            {
                var sb = new StringBuilder();
                var fans = vm.Live?.FanControls;
                if (fans is null || fans.Count == 0) sb.AppendLine("可控風扇：未偵測到（主機板控制器未被支援，或需要管理員權限）");
                else
                    foreach (var f in fans)
                        sb.AppendLine($"風扇「{f.Name}」（{f.Location}）：輸出 {f.CurrentText}・轉速 {f.RpmText}"
                                      + $"・模式 {f.ModeText}・可設定範圍 {f.MinValue:0}–{f.MaxValue:0} %");

                var curves = vm.FanCurves;
                sb.AppendLine($"曲線引擎：{curves.StatusText}");
                foreach (var c in curves.Curves)
                {
                    string pts = string.Join("、", c.Points.Select(p => $"{p.TempC:0}°C→{p.Percent:0}%"));
                    sb.AppendLine($"曲線「{c.Name}」：{c.StateText}・依據 {c.SourceText}"
                                  + $"・目前依據溫度 {c.LiveTempC:0} °C・{c.TargetText}"
                                  + $"・遲滯 {c.Hysteresis:0.#} °C・轉折點 {pts}");
                }
                if (curves.HasCurves) sb.AppendLine($"允許完全停轉：{(curves.AllowStop ? "是" : "否（最低維持 20 %）")}");
                return Done(sb, "（無風扇資料）");
            });
    }

    // ── 儲存 ────────────────────────────────────────────────────────────────

    private static void AddStorage(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_disk_health",
            "取得每顆磁碟的健康與容量：型號、介面、韌體、容量、S.M.A.R.T. 剩餘壽命與健康判定、"
            + "溫度、活動時間，以及各磁碟區的已用／可用空間。要判斷儲存是否該換或空間是否吃緊時用這個。",
            _ =>
            {
                var sb = new StringBuilder();
                var drives = vm.Live?.Drives;
                if (drives is not null)
                    foreach (var d in drives)
                        sb.AppendLine($"磁碟「{d.DisplayModel}」：{d.TypeText}・{d.CapacityText}"
                                      + $"・健康 {SevText(d.HealthSeverity)}（{d.HealthText}）"
                                      + $"・溫度 {d.TempText}・活動 {d.ActivityText}"
                                      + $"・介面 {d.InterfaceType}・韌體 {d.Firmware}");

                foreach (var p in vm.PhysicalDisks)
                    sb.AppendLine($"實體磁碟「{p.Model}」：{p.CapacityText}・{p.TypeText}・介面 {p.InterfaceType}"
                                  + $"・{p.PartitionsText}・序號 {p.SerialNumber}"
                                  + (p.HealthDetail.Length > 0 ? $"・S.M.A.R.T. {p.HealthDetail}" : ""));

                foreach (var v in vm.Volumes.Volumes)
                    sb.AppendLine($"磁碟區 {v.CaptionText}（{v.TypeText}）：{v.SizeText}・{v.FreeText}"
                                  + $"・已用 {v.CenterPercentText}・空間狀態 {SevText(v.Severity)}");

                Line(sb, "整機儲存", vm.Volumes.SummaryText);
                return Done(sb, "（未偵測到磁碟）");
            });
    }

    private static string SevText(Severity s) => s switch
    {
        Severity.Good => "良好",
        Severity.Warning => "注意",
        Severity.Serious => "警告",
        Severity.Critical => "危險",
        _ => "一般",
    };

    // ── 事件時間軸 ──────────────────────────────────────────────────────────

    private static void AddEvents(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_events",
            "取得事件時間軸上最近的紀錄：溫度／負載警示、處理器降頻、磁碟壽命變化、藍屏、調校動作與啟動關閉。"
            + "要找出「什麼時候出過問題」時用這個。",
            args =>
            {
                int n = AiToolbox.IntArg(args, "count", 25, 1, 120);
                var sb = new StringBuilder();
                foreach (var e in vm.Events.All.Take(n))
                    sb.AppendLine($"{e.TimeText}（{e.AgoText}）[{e.KindText}／{SevText(e.Severity)}] {e.Title}"
                                  + (e.Detail.Length > 0 ? $" — {e.Detail}" : ""));
                return Done(sb, "（時間軸上尚無任何事件）");
            },
            """{"type":"object","properties":{"count":{"type":"integer","description":"要取回的事件筆數，1–120，預設 25"}}}""");
    }

    // ── 歷史統計 ────────────────────────────────────────────────────────────

    private static void AddHistory(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_history_stats",
            "取得指定時間窗內各項指標（處理器負載／溫度／頻率、記憶體使用、顯示卡負載／溫度／顯示記憶體）的"
            + "最小值、平均、最大值與 95 百分位。要判斷「長期表現」而非此刻瞬間值時用這個。",
            args =>
            {
                int hours = AiToolbox.IntArg(args, "hours", 24, 1, 2160);
                var to = DateTime.UtcNow;
                var from = to.AddHours(-hours);
                var series = vm.History.Query(from, to);
                var sb = new StringBuilder();
                sb.AppendLine($"時間窗：最近 {hours} 小時（取樣點 {series.Count} 個，"
                              + $"{(series.SecondLevel ? "秒級" : "分鐘級")}）");
                if (series.Count == 0)
                    return sb.AppendLine("（此時間窗內沒有歷史資料；歷史累積可能剛啟用或已被關閉）").ToString().TrimEnd();

                for (int m = 0; m < HistoryMetrics.Count; m++)
                {
                    if (!series.HasData(m)) continue;     // 此機器沒有這項讀值：不列，也不以 0 充數
                    var (min, avg, max, p95) = series.Summarize(m);
                    string u = HistoryMetrics.Units[m];
                    sb.AppendLine($"{HistoryMetrics.Titles[m]}：平均 {avg:0.#} {u}"
                                  + $"・最低 {min:0.#} {u}・最高 {max:0.#} {u}・95% {p95:0.#} {u}");
                }
                Line(sb, "歷史倉狀態", $"分鐘級 {vm.History.MinuteCount} 筆・秒級 {vm.History.SecondCount} 筆"
                                     + $"・保留 {vm.History.RetentionDays} 天・{vm.History.SizeText}");
                return sb.ToString().TrimEnd();
            },
            """{"type":"object","properties":{"hours":{"type":"integer","description":"往回查詢的小時數，1–2160（90 天），預設 24"}}}""");
    }

    // ── 調校現況 ────────────────────────────────────────────────────────────

    private static void AddTuning(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_gpu_control_state",
            "取得顯示卡超頻／限制的現況：NVML 與 NVAPI 是否可用、目前功耗上限與原廠值、"
            + "核心與顯示記憶體頻率偏移、溫度上限、風扇是否手動。也會說明缺少哪些能力。",
            _ =>
            {
                var g = vm.GpuOc;
                var sb = new StringBuilder();
                Line(sb, "顯示卡", g.GpuName);
                Line(sb, "可用性", g.AvailabilityText);
                sb.AppendLine($"介面：NVML {(g.NvmlAvailable ? "可用" : "不可用")}"
                              + $"・NVAPI {(g.NvapiAvailable ? "可用" : "不可用")}"
                              + $"・溫度上限控制 {(g.TempControlAvailable ? "可用" : "不可用")}"
                              + $"・管理員權限 {(g.IsAdmin ? "是" : "否")}");
                if (!g.Available) return Done(sb, "（顯示卡控制不可用）");
                sb.AppendLine($"即時：核心 {g.CoreClockMhz:0} MHz・顯示記憶體 {g.MemClockMhz:0} MHz"
                              + $"・溫度 {g.TempC:0} °C・功耗 {g.PowerW:0.#} W・風扇 {g.FanPercent:0} %");
                Line(sb, "功耗上限", g.PowerStatus);
                Line(sb, "核心偏移", g.CoreStatus);
                Line(sb, "顯示記憶體偏移", g.MemStatus);
                Line(sb, "溫度上限", g.TempStatus);
                Line(sb, "風扇", g.FanStatus);
                return Done(sb, "（無資料）");
            });

        box.Add("get_scene_state",
            "取得場景設定檔的現況：目前使用中的場景、Windows 電源計劃、上次套用的結果說明，"
            + "以及自訂場景會動到哪些部分。",
            _ =>
            {
                var p = vm.Profiles;
                var sb = new StringBuilder();
                Line(sb, "使用中場景", p.ActiveName);
                Line(sb, "Windows 電源計劃", p.PowerPlanText);
                Line(sb, "狀態", p.StatusText);
                var c = p.Custom;
                sb.AppendLine($"自訂場景：風扇 {(c.ApplyFan ? FanCurveService.PresetNames[Math.Clamp(c.FanPreset, 0, 2)] + " 樣板" : "不變更")}"
                              + $"・電源計劃 {(c.ApplyPowerPlan ? ProfileService.PowerPlanNames[c.PowerPlanIndex] : "不變更")}"
                              + $"・顯示卡 {(c.ApplyGpu ? $"功耗 {c.GpuPowerPercent:0} %／溫度 {c.GpuTempLimitC:0} °C" : "不變更")}");
                return Done(sb, "（無資料）");
            });

        box.Add("get_cpu_oc_state",
            "取得處理器超頻模組（Intel XTU 引擎）的現況：引擎是否就緒與可寫入、處理器家族、"
            + "即時有效頻率／電壓／溫度／功耗限制與供電模組溫度，以及體質評分。",
            _ =>
            {
                var o = vm.Overclock;
                var sb = new StringBuilder();
                Line(sb, "引擎", o.EngineName);
                Line(sb, "引擎狀態", o.EngineStatusText);
                sb.AppendLine($"就緒：{(o.EngineReady ? "是" : "否")}・可寫入：{(o.CanWrite ? "是" : "否")}");
                Line(sb, "處理器家族", o.ProcessorFamilyText);
                if (!o.EngineReady) return Done(sb, "（處理器超頻引擎不可用）");
                Line(sb, "有效頻率", o.EffectiveClockText);
                Line(sb, "核心電壓", o.VoltageValueText);
                Line(sb, "核心溫度", o.CoreTempText);
                Line(sb, "供電模組溫度", o.VrmTempText);
                Line(sb, "電流", o.CurrentText);
                Line(sb, "PL1", o.Pl1Text);
                Line(sb, "PL2", o.Pl2Text);
                Line(sb, "體質評分", $"{o.SiliconScore}（{o.SiliconVerdict}）");
                return Done(sb, "（無資料）");
            });
    }

    // ── 診斷 ────────────────────────────────────────────────────────────────

    private static void AddDiagnostics(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_health_report",
            "取得健康總評：綜合分數、一句總結與建議，以及各檢查項（處理器溫度、負載、記憶體、"
            + "磁碟溫度與空間等）的狀態燈與說明。",
            _ =>
            {
                var h = vm.Health;
                var sb = new StringBuilder();
                sb.AppendLine($"綜合分數：{h.ScoreText}／100（{SevText(h.ScoreSeverity)}）");
                Line(sb, "總結", h.Summary);
                Line(sb, "建議", h.Advice);
                foreach (var i in h.Items)
                    sb.AppendLine($"{i.Label}：{i.ValueText}（{i.StatusText}）"
                                  + (string.IsNullOrWhiteSpace(i.Detail) ? "" : $" — {i.Detail}"));
                return Done(sb, "（健康總評尚未計算）");
            });

        box.Add("get_env_check",
            "取得環境自檢結果：各功能所需的執行階段、驅動與服務是否就緒（例如 NVML／NVAPI、"
            + "Intel XTU、WebView2、winget、管理員權限）。要解釋「某個功能為何不能用」時用這個。",
            _ =>
            {
                var e = vm.EnvCheck;
                var sb = new StringBuilder();
                if (!e.HasRun) sb.AppendLine("（環境自檢尚未執行；以下為目前已知項目）");
                Line(sb, "總結", e.Summary);
                foreach (var i in e.Items)
                    sb.AppendLine($"{i.Name}：{i.StatusText}（{SevText(i.Severity)}）"
                                  + (i.HasDetail ? $" — {i.Detail}" : ""));
                return Done(sb, "（環境自檢尚未執行，目前沒有項目）");
            });

        box.Add("get_benchmark_scores",
            "取得本機已跑過的效能測試成績：綜合跑分（單／多執行緒與記憶體頻寬）、SuperPI 圓周率、"
            + "快取與記憶體延遲、磁碟讀寫、棋類 perft 節點吞吐（含運算正確性核對）、Windows 體驗指數，以及最近一次烤機的穩定度。"
            + "未跑過的項目會如實說明沒有成績。本程式不內建任何其他機器的參考分數，"
            + "成績後方的「與上次相比／重複性／量測條件」都來自本機歷次紀錄，請勿據此推測與別台電腦的高下。",
            _ =>
            {
                var sb = new StringBuilder();
                var b = vm.Bench;
                sb.AppendLine(b.Composite is null
                    ? "綜合跑分：尚未測試"
                    : $"綜合跑分：{b.CompositeText}（單執行緒 {b.SingleText}・多執行緒 {b.MultiText}"
                      + $"・記憶體頻寬 {b.MemText}）"
                      + Tail(b.DeltaText, b.RepeatText, b.ConditionText));

                Line(sb, "SuperPI 圓周率", vm.SuperPi.ElapsedText is { Length: > 0 } et && et != "—"
                    ? $"{vm.SuperPi.Digits} 位數 耗時 {et}"
                      + Tail(vm.SuperPi.DeltaText, vm.SuperPi.RepeatText, vm.SuperPi.ConditionText) : null);

                var c = vm.Cache;
                if (c.Rows.Count > 0)
                    sb.AppendLine($"快取／記憶體延遲：L1 {c.L1Text}・L2 {c.L2Text}・L3 {c.L3Text}・記憶體 {c.RamText}");
                else sb.AppendLine("快取／記憶體延遲：尚未測試");

                var d = vm.DiskBench;
                sb.AppendLine($"磁碟效能：循序讀 {d.SeqReadText}・循序寫 {d.SeqWriteText}"
                              + $"・隨機 4K 讀 {d.RandReadText}・隨機 4K 寫 {d.RandWriteText}");

                var ch = vm.Chess;
                sb.AppendLine(ch.MultiKNps is null
                    ? "棋類 perft 節點吞吐：尚未測試"
                    : $"棋類 perft 節點吞吐（{ch.EngineName}）：單執行緒 {ch.SingleText}・多執行緒 {ch.MultiText}"
                      + $"・加速比 {ch.SpeedupText}・{ch.EfficiencyText}"
                      + Tail(ch.MultiDeltaText, ch.RepeatText, ch.ConditionText)
                      + (string.IsNullOrEmpty(ch.IntegrityText) ? "" : "・" + ch.IntegrityText));

                var w = vm.Winsat;
                sb.AppendLine(w.HasData
                    ? $"Windows 體驗指數：基礎分數 {w.BaseScoreText}（處理器 {w.CpuText}・記憶體 {w.MemoryText}"
                      + $"・磁碟 {w.DiskText}・圖形 {w.GraphicsText}・遊戲圖形 {w.D3DText}），評分時間 {w.AssessedText}"
                    : $"Windows 體驗指數：{w.StateText}");

                var s = vm.Stress;
                if (s.MaxTempC is not null)
                    sb.AppendLine($"烤機結果：最高溫 {s.MaxTempText}・頻率 {s.MinClockText}–{s.MaxClockText}"
                                  + $"・{s.StabilityText}・持續 {s.ElapsedText}");
                else sb.AppendLine("烤機：尚未測試");

                return sb.ToString().TrimEnd();
            });
    }
}
