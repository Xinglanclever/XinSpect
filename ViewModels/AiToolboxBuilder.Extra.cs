using System.Text;

namespace XinSpect;

/// <summary>
/// AI 診斷代理的第二批唯讀工具：記憶體時序與 SPD、網路組態與流量、螢幕色域、效能天梯定位、
/// 升級建議、電池健康、開機啟動項、藍屏記錄。與第一批同樣只讀不寫，
/// 讀不到就如實回報「無資料」，絕不讓模型有機會編造數值。
/// </summary>
internal static partial class AiToolboxBuilder
{
    // ── 記憶體 ──────────────────────────────────────────────────────────────

    private static void AddMemory(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_memory_detail",
            "取得記憶體的完整細節：型別與通道組態、實際執行頻率與等效資料率、主要時序（CL-tRCD-tRP-tRAS）、"
            + "tRFC 與命令率、每支實體模組的插槽／製造商／料號／容量／標定與實跑頻率／電壓，"
            + "以及 SPD 內的 JEDEC 與 XMP／EXPO 設定檔。要判斷記憶體是否插對通道、有沒有吃到標定頻率時用這個。",
            _ =>
            {
                var sb = new StringBuilder();
                var t = vm.Timings;
                if (!t.Loaded) sb.AppendLine($"（時序讀取狀態：{t.Status}）");
                Line(sb, "記憶體型別", t.MemoryTypeText);
                Line(sb, "通道組態", t.ChannelsText);
                Line(sb, "實際頻率", t.FrequencyText);
                Line(sb, "等效資料率", t.DataRateText);
                Line(sb, "主要時序", t.PrimaryTimingsText);
                Line(sb, "tRFC", t.TRFC);
                Line(sb, "命令率", t.CommandRate);
                Line(sb, "Uncore／記憶體控制器", t.UncoreText);
                Line(sb, "總容量", t.MemorySizeText);
                Line(sb, "主橋", t.HostBridge);

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

                return Done(sb, "（未讀到任何記憶體資訊）");
            });
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

    // ── 效能天梯 ────────────────────────────────────────────────────────────

    private static void AddRanking(AiToolbox box, MainViewModel vm)
    {
        box.Add("get_ranking_position",
            "取得本機處理器／顯示卡在內建效能天梯榜上的名次與同級對手：名次、總筆數、分級、分數，"
            + "以及名次相鄰的數個型號可作對照。要回答「這顆 CPU 算強嗎」「該換哪張卡」時用這個。"
            + "榜單為離線快照且以名稱近似比對，未命中時會如實說明。",
            args =>
            {
                int near = AiToolbox.IntArg(args, "neighbors", 5, 0, 20);
                var r = vm.Ranking;
                var sb = new StringBuilder();
                sb.AppendLine(r.CpuSource);
                Section(sb, "處理器", r.LocalCpu, r.CpuList, near, r.CpuTotal);
                sb.AppendLine(r.GpuSource);
                Section(sb, "顯示卡", r.LocalGpu, r.GpuList, near, r.GpuTotal);
                return Done(sb, "（天梯資料未載入）");
            },
            """{"type":"object","properties":{"neighbors":{"type":"integer","description":"要一併列出的相鄰名次數量，0–20，預設 5"}}}""");
    }

    // 列出某一榜的本機定位與相鄰對手；未命中時只說明未命中，不猜名次
    private static void Section(StringBuilder sb, string label, RankRow? local,
                               System.ComponentModel.ICollectionView list, int near, Func<bool, int> total)
    {
        if (local is null)
        {
            sb.AppendLine($"{label}：未在榜單中命中（名稱近似比對不足，故不談名次）");
            return;
        }

        int all = total(local.IsLaptop);
        string scope = local.IsLaptop ? "筆電" : "桌機";
        double pct = all > 0 ? 100.0 * local.Rank / all : 0;
        sb.AppendLine($"{label}「{local.Name}」：{scope}榜第 {local.Rank} 名"
                      + (all > 0 ? $" / 共 {all} 筆（贏過約 {100 - pct:0} %）" : "")
                      + $"・分級 {local.Grade}・分數 {local.Score}"
                      + (local.Detail.Length > 0 ? $"・{local.Detail}" : ""));

        if (near <= 0) return;
        var rows = list.Cast<RankRow>().Where(x => x.IsLaptop == local.IsLaptop)
                       .OrderBy(x => x.Rank).ToList();
        int at = rows.FindIndex(x => ReferenceEquals(x, local));
        if (at < 0) return;
        int from = Math.Max(0, at - near), to = Math.Min(rows.Count - 1, at + near);
        for (int i = from; i <= to; i++)
        {
            if (i == at) continue;
            var x = rows[i];
            sb.AppendLine($"　對照 第 {x.Rank} 名 {x.Name}（{x.Grade}・分數 {x.Score}"
                          + (x.Detail.Length > 0 ? $"・{x.Detail}" : "") + "）");
        }
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
