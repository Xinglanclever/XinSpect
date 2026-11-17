using System.Text;

namespace XinSpect;

/// <summary>
/// AI 診斷代理的第二批唯讀工具：記憶體時序與 SPD、網路組態與流量、螢幕色域、
/// 升級建議、電池健康、開機啟動項、藍屏記錄。與第一批同樣只讀不寫，
/// 讀不到就如實回報「無資料」，絕不讓模型有機會編造數值。
/// </summary>
internal static partial class AiToolboxBuilder
{
    // ── 記憶體 ──────────────────────────────────────────────────────────────

    private static void AddMemory(AiToolbox box, MainViewModel vm) =>
        AddMemory(box, () => vm.Timings, () =>
        {
            var sb = new StringBuilder();
            if (vm.Modules.Count == 0) sb.AppendLine("實體模組：未讀到（虛擬機或權限不足時 SMBIOS 可能為空）");
            foreach (var m in vm.Modules)
                sb.AppendLine($"模組「{m.Slot}」：{m.Manufacturer} {m.PartNumber}・{m.CapacityText}"
                              + $"・{m.MemoryType} {m.FormFactor}・{m.SpeedText}・電壓 {m.Voltage}");

            foreach (var s in vm.SpdModules)
            {
                sb.AppendLine($"SPD「{s.Slot}」：{s.Manufacturer} {s.PartNumber}・{s.MemoryType} {s.ModuleFormat}"
                              + $"・{s.Size}・最高頻寬 {s.MaxBandwidth}・JEDEC 上限 {s.MaxJedec}"
                              + $"・標稱電壓 {s.NominalVoltage}・製造日期 {s.ManufacturingDate}");
                if (s.HasXmp) sb.AppendLine($"　XMP／EXPO 設定檔：{s.XmpSummary}");
            }

            if (vm.Live is { } live && live.MemTotalGB > 0)
                sb.AppendLine($"目前用量：{live.MemUsageText}（{live.MemLoadText}）");

            return sb.ToString();
        });

    // The same registration/delegate is used by production and hardware-free injected tests.
    internal static void AddMemory(AiToolbox box, Func<MemoryTimings> timings, Func<string> inventory)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(inventory);
        box.Add("get_memory_detail",
            "提供記憶體型別、通道與模組摘要；對時序和速率逐項標示來源、可用性與信任；"
            + "區分來源回報的目前值、Windows 設定值與 SPD JEDEC 基準，未確認值不能視為目前運行值。"
            + "另附每支模組與 SPD 的 JEDEC／XMP／EXPO 資料。",
            _ =>
            {
                var sb = new StringBuilder(FormatMemoryTimingDescription(timings()));
                sb.Append(inventory());
                return Done(sb, "（未讀到任何記憶體資訊）");
            });
    }

    /// <summary>Pure description of the selected memory quantities; no VM, clock, hardware or external I/O.</summary>
    internal static string FormatMemoryTimingDescription(MemoryTimings timings)
    {
        ArgumentNullException.ThrowIfNull(timings);
        var sb = new StringBuilder();
        // Loaded only means timing fields were populated. It does not establish current-value scope.
        sb.AppendLine($"時序讀取狀態：{timings.Status}");
        Line(sb, "記憶體型別", timings.MemoryTypeText);
        Line(sb, "通道組態", timings.ChannelsText);
        Line(sb, "總容量", timings.MemorySizeText);
        Line(sb, "主橋", timings.HostBridge);

        // Share R380's quantity/state rules. The timestamp is not output or used as a source update time.
        var facts = EvidenceLabService.CollectMemoryTimingFacts(timings, DateTimeOffset.UnixEpoch);
        HardwareFact? timing = facts.FirstOrDefault(f => f.Key == "memory.current");
        HardwareFact? rate = facts.FirstOrDefault(f => f.Key == "memory.rate");
        if (timing is not null)
        {
            AppendQuantity(timing.Name, timing.Value, timing.Source, timing.Trust,
                timing.Availability, timing.UnavailableReason);
        }
        else
            AppendQuantity("目前時序", "—", Source(timings.TimingFactSource),
                timings.TimingFactTrust ?? FactTrustLevel.Unknown,
                FactStateLattice.Weakest(timings.TimingFactAvailability, FactAvailability.Unknown),
                timings.TimingFactReason ?? "尚未取得時序欄位；不代表沒有記憶體。");

        if (Known(timings.TRFC)) AppendTimingExtra("tRFC（沿用時序範圍）", timings.TRFC);
        if (Known(timings.CommandRate)) AppendTimingExtra("命令率（沿用時序範圍）", timings.CommandRate);

        if (rate is not null)
            AppendQuantity(rate.Name, rate.Value, rate.Source, rate.Trust,
                rate.Availability, rate.UnavailableReason);
        else
            AppendQuantity("資料速率", "—", Source(timings.RateFactSource),
                timings.RateFactTrust ?? FactTrustLevel.Unknown,
                FactStateLattice.Weakest(timings.RateFactAvailability, FactAvailability.Unknown),
                timings.RateFactReason ?? "未取得可用資料速率；沒有以零或舊值補入。");

        if (double.IsFinite(timings.DramFrequencyMHz) && timings.DramFrequencyMHz > 0)
        {
            bool reference = timings.RateIsJedecReference
                || timings.IsJedecReference && !Known(timings.RateFactSource);
            string name = reference ? "JEDEC 參考時脈（非目前運行值）"
                : timings.RateIsConfigured ? "設定時脈換算（非即時量測）"
                : "DRAM 時脈（沿用資料速率來源與範圍）";
            FactTrustLevel clockTrust = rate?.Trust ?? timings.RateFactTrust ?? FactTrustLevel.Unknown;
            if (clockTrust is not (FactTrustLevel.Unknown or FactTrustLevel.Speculative)
                && (reference || timings.RateIsConfigured)) clockTrust = FactTrustLevel.Derived;
            string? clockReason = rate is null
                ? (string.IsNullOrWhiteSpace(timings.RateFactReason) ? "" : timings.RateFactReason + "；")
                    + "缺少對應資料速率文字的確認資訊；時脈不是獨立量測。"
                : timings.RateIsConfigured && !reference
                    ? "由所選設定速率換算；不是即時或獨立量測。"
                        + (string.IsNullOrWhiteSpace(rate.UnavailableReason) ? "" : "；" + rate.UnavailableReason)
                    : rate.UnavailableReason;
            AppendQuantity(name,
                timings.DramFrequencyMHz.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MHz",
                rate?.Source ?? Source(timings.RateFactSource),
                clockTrust,
                rate?.Availability ?? FactStateLattice.Weakest(timings.RateFactAvailability, FactAvailability.Unknown),
                clockReason);
        }

        // R380 has no separate provenance for this legacy extra field.
        if (Known(timings.UncoreText))
            AppendQuantity("Uncore／記憶體控制器附加欄位", timings.UncoreText,
                Source(null), FactTrustLevel.Unknown, FactAvailability.Unknown,
                "未提供此欄位的獨立來源與目前值確認；保留原文供檢視。");
        return sb.ToString();

        static bool Known(string? value) => !string.IsNullOrWhiteSpace(value)
            && value.Trim() is not ("—" or "-" or "N/A");
        string Source(string? specific) => Known(specific) ? specific!
            : Known(timings.SourceText) ? timings.SourceText : "來源未標示";
        void AppendTimingExtra(string name, string value) => AppendQuantity(name, value,
            timing?.Source ?? Source(timings.TimingFactSource),
            timing?.Trust ?? timings.TimingFactTrust ?? FactTrustLevel.Unknown,
            timing?.Availability ?? FactStateLattice.Weakest(timings.TimingFactAvailability, FactAvailability.Unknown),
            timing?.UnavailableReason ?? (timing is null ? "沒有完整目前時序的確認資訊；附加欄位僅保留原文。" : null));
        void AppendQuantity(string name, string value, string source, FactTrustLevel trust,
            FactAvailability availability, string? reason)
        {
            string content = availability == FactAvailability.Present ? value
                : Known(value) ? "保留內容（不得當成已確認目前值）：" + value : "未取得值";
            sb.AppendLine($"{name}：{content}；來源：{source}；可用性：{availability}（{FactStateLattice.Describe(availability)}）；"
                + $"信任：{trust}（{HardwareSnapshotService.TrustText(trust)}）"
                + (string.IsNullOrWhiteSpace(reason) ? "" : "；原因：" + reason));
        }
    }

    // ── 網路 ────────────────────────────────────────────────────────────────

    private static void AddNetwork(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_network_state",
            "取得網路現況：每張使用中網卡的類型、連線速率、IPv4／IPv6、子網路遮罩、預設閘道、DNS 伺服器、"
            + "DHCP 狀態、MTU，以及此刻的上下行速率；另含已安裝網路介面清單。"
            + "要診斷連不上網、速度異常或找出實際使用哪張網卡時用這個。",
            _ =>
            {
                var sb = new StringBuilder();
                var net = vm.Net;
                if (net is null || net.Adapters.Count == 0)
                    sb.AppendLine("使用中網卡：未偵測到（可能全部斷線）");
                else
                {
                    sb.AppendLine($"合計流量：下載 {net.TotalDownText}・上傳 {net.TotalUpText}");
                    foreach (var a in net.Adapters)
                    {
                        sb.AppendLine($"網卡「{a.Name}」（{a.TypeText}）：連線速率 {a.LinkSpeedText}"
                                      + $"・此刻 下載 {a.DownText}／上傳 {a.UpText}");
                        sb.AppendLine($"　IPv4 {a.Ipv4}・遮罩 {a.SubnetMask}・閘道 {a.Gateway}"
                                      + $"・DNS {a.Dns}・DHCP {a.DhcpText}・MTU {a.MtuText}"
                                      + $"・IPv6 {a.Ipv6}・實體位址 {a.Mac}");
                    }
                }

                foreach (var n in vm.InstalledNics)
                    sb.AppendLine($"已安裝介面「{n.Name}」：{n.Manufacturer}・{n.TypeText}・{n.SpeedText}・{n.Mac}");

                return Done(sb, "（未讀到網路資訊）");
            });
    }

    // ── 螢幕 ────────────────────────────────────────────────────────────────

    private static void AddDisplay(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_display_gamut",
            "取得各螢幕由 EDID 解析出的色域能力：製造商、白點與三原色座標、對 sRGB／Adobe RGB／DCI-P3 的"
            + "覆蓋率與判定。要回答「這螢幕適合修圖／剪片嗎」時用這個。EDID 為螢幕自述值，"
            + "非實測校色結果，回答時須說明這點。",
            _ =>
            {
                var sb = new StringBuilder();
                if (vm.Monitors.Count == 0)
                    return "（未讀到任何螢幕 EDID；筆電內顯或部分轉接線可能不提供）";
                foreach (var m in vm.Monitors)
                {
                    if (!m.Valid) { sb.AppendLine($"螢幕「{m.Name}」：EDID 色度資料不完整，無法計算色域"); continue; }
                    sb.AppendLine($"螢幕「{m.Name}」（{m.Manufacturer}）：sRGB {m.SrgbText}"
                                  + $"・Adobe RGB {m.AdobeText}・DCI-P3 {m.DciText}");
                    sb.AppendLine($"　白點 {m.WhitePointText}・三原色 {m.PrimariesText}"
                                  + $"・色域面積 {m.AreaText}・判定 {m.Assessment}");
                }
                sb.AppendLine("（以上為 EDID 自述的色度座標推算值，非校色儀實測。）");
                return Done(sb, "（無資料）");
            });
    }

    // ── 升級建議 ────────────────────────────────────────────────────────────

    private static void AddUpgrade(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_upgrade_advice",
            "跑一次內建的升級建議規則引擎，取得瓶頸判定、結論可信度，以及依優先度排序的建議清單"
            + "（每條含判斷依據＝本機真實讀值、具體行動、花費層級、預期效益）。"
            + "使用者問「這台該升級什麼」「先換哪個最有感」「值不值得換」時用這個。"
            + "引擎遇到讀不到的項目會自行略過該規則，因此清單為空就是真的沒有可據以建議的證據。"
            + "預期效益是同類升級的經驗範圍，不是本機實測，轉述時必須說明。",
            _ =>
            {
                var facts = UpgradeFactsCollector.Collect(vm);
                var r = UpgradeAdvisor.Analyze(facts);
                var sb = new StringBuilder();
                sb.AppendLine($"瓶頸判定：{r.Bottleneck}（{SevText(r.BottleneckSeverity)}）");
                sb.AppendLine($"　說明：{r.BottleneckDetail}");
                sb.AppendLine($"可信度：{r.Confidence}");

                if (!r.HasItems)
                {
                    sb.AppendLine("建議清單：空（現有讀值不足以支撐任何一條建議，請勿自行補充建議）");
                    return sb.ToString().TrimEnd();
                }

                sb.AppendLine($"建議清單（共 {r.Count} 條，已依優先度排序）：");
                int i = 1;
                foreach (var s in r.Items)
                {
                    sb.AppendLine($"{i++}. [{s.PartText}] {s.Title}"
                                  + $"（優先度 {s.PriorityText}／{s.Score} 分・{SevText(s.Severity)}・花費 {s.Cost}）");
                    sb.AppendLine($"　依據：{s.Evidence}");
                    sb.AppendLine($"　行動：{s.Action}");
                    sb.AppendLine($"　預期效益：{s.Gain}");
                }
                return sb.ToString().TrimEnd();
            });
    }

    // ── 電池 ────────────────────────────────────────────────────────────────

    private static void AddPortable(AiToolbox box)
    {
        box.Add("get_battery_state",
            "取得電池健康狀況：是否有電池、化學類型、目前電量與充放電狀態、設計容量、目前滿充容量、"
            + "耗損率與循環次數。要回答「電池還健康嗎」「該換電池了嗎」或判斷本機是筆電還是桌機時用這個。"
            + "桌機／伺服器會明確回報「未偵測到電池」；循環次數多數機型不回報，屆時不可推測。",
            _ =>
            {
                BatteryInfo b;
                try { b = new BatteryService().Read(); }
                catch (Exception ex) { return "讀取電池資訊失敗：" + ex.Message; }

                if (!b.Present) return b.Message.Length > 0 ? b.Message : "未偵測到電池（桌機或伺服器環境）。";

                var sb = new StringBuilder();
                Line(sb, "電池", b.Name);
                Line(sb, "化學類型", b.Chemistry);
                sb.AppendLine($"目前電量：{b.ChargePercent} %（{b.StatusText}）");
                Line(sb, "設計容量", b.DesignText);
                Line(sb, "目前滿充容量", b.FullText);
                Line(sb, "耗損率", b.WearText);
                sb.AppendLine($"循環次數：{b.CycleText}");
                if (b.DesignCapacity <= 0 || b.FullCapacity <= 0)
                    sb.AppendLine("（此機型未透過 WMI 提供設計／滿充容量，故無法計算耗損率——不可據此猜測電池衰退程度。）");
                return Done(sb, "（未讀到電池細節）");
            });
    }

    // ── 開機啟動項 ──────────────────────────────────────────────────────────

    private static void AddStartup(AiToolbox box)
    {
        box.Add("get_startup_items",
            "掃描開機自啟項目（登錄 Run／Run32 與啟動資料夾，HKLM 與 HKCU）並列出名稱、來源、"
            + "啟用狀態與指令列。要回答「開機很慢是不是啟動項太多」時用這個。"
            + "此工具只讀取，不會停用任何項目；要停用需使用者自己到工具箱操作。",
            args =>
            {
                int limit = AiToolbox.IntArg(args, "limit", 40, 1, 200);
                bool onlyEnabled = (AiToolbox.StringArg(args, "only_enabled") ?? "").Trim().ToLowerInvariant()
                                   is "1" or "true" or "yes" or "是";

                var svc = new StartupService();
                svc.Scan();
                var sb = new StringBuilder();
                sb.AppendLine(svc.Status.Length > 0 ? svc.Status : "（掃描狀態未知）");
                if (svc.Entries.Count == 0) return sb.ToString().TrimEnd();

                var rows = svc.Entries.Where(e => !onlyEnabled || e.Enabled).ToList();
                foreach (var e in rows.Take(limit))
                    sb.AppendLine($"「{e.Name}」：{e.StateText}・{e.Location}・指令 {e.Command}");
                if (rows.Count > limit) sb.AppendLine($"（另有 {rows.Count - limit} 項未列出）");
                return sb.ToString().TrimEnd();
            },
            """{"type":"object","properties":{"limit":{"type":"integer","description":"最多列出幾項，1–200，預設 40"},"only_enabled":{"type":"string","description":"填 true 只列出啟用中的項目"}}}""");
    }

    // ── 藍屏記錄 ────────────────────────────────────────────────────────────

    private static void AddBsod(AiToolbox box)
    {
        box.Add("get_bsod_history",
            "掃描 %SystemRoot%\\Minidump 內的核心小型傾印檔，列出每次藍屏的時間、停止代碼（含名稱）、"
            + "四個參數與常見原因提示。要回答「最近為什麼藍屏」「這代碼是什麼意思」時用這個。"
            + "註：真正的肇事驅動程式需 WinDbg 符號解析，本工具做不到——不可憑代碼指名某個驅動程式。",
            args =>
            {
                int limit = AiToolbox.IntArg(args, "limit", 10, 1, 50);
                var svc = new BsodService();
                svc.Scan();
                var sb = new StringBuilder();
                sb.AppendLine(svc.Status.Length > 0 ? svc.Status : "（掃描狀態未知）");
                if (!svc.HasDumps) return sb.ToString().TrimEnd();

                foreach (var r in svc.Rows.Take(limit))
                {
                    sb.AppendLine($"{r.TimeText}　{r.CodeHex} {r.Name}（{r.FileName}・{r.SizeText}）");
                    sb.AppendLine($"　參數：{r.Params}");
                    if (r.Hint.Length > 0) sb.AppendLine($"　常見原因：{r.Hint}");
                }
                if (svc.Rows.Count > limit) sb.AppendLine($"（另有 {svc.Rows.Count - limit} 筆較舊的傾印未列出）");
                sb.AppendLine("（停止代碼只指出失敗的類型，並未指出肇事的驅動程式；要指名驅動程式須以 WinDbg 解析符號。）");
                return sb.ToString().TrimEnd();
            },
            """{"type":"object","properties":{"limit":{"type":"integer","description":"最多列出幾筆傾印，1–50，預設 10"}}}""");
    }
}
