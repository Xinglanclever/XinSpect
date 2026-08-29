using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 硬體報告匯出：HTML（含列印／另存 PDF 版面）、Markdown（貼上論壇或議題用）與純文字。
/// </summary>
/// <remarks>
/// <para>
/// 三種格式共用同一份中介表示（<see cref="Section"/> 與 <see cref="Block"/>）：先把要寫的東西
/// 收集齊全，再交給各自的渲染器。這樣就不會出現「HTML 有感測器一節、純文字卻漏掉」這類走樣，
/// 也不必為了新增一節而改三個地方。
/// </para>
/// <para>
/// 報告只在本機產生、只寫到本機檔案。曦覽不提供「產生分享網址」——那必然得把這台機器的
/// 硬體資訊送到別人的伺服器上存放，一旦送出就收不回來。要分享就分享這份檔案本身：
/// HTML 為單一自帶樣式的檔案（不外連字型、不外連指令碼），寄出、附在議題、或以瀏覽器
/// 列印成 PDF 都不會少東西；要貼論壇則用 Markdown。
/// </para>
/// <para>
/// 若打算把報告貼到公開場合，可在設定頁開啟「遮蔽可識別資訊」：主機名稱、使用者名稱、
/// MAC 位址與磁碟序號會以「（已遮蔽）」取代，其餘規格與讀值照實輸出。
/// </para>
/// </remarks>
public static class ReportService
{
    /// <summary>顯示另存對話框並寫出報告；成功回傳路徑，取消回傳 <c>null</c>。</summary>
    public static string? Export(MainViewModel vm)
    {
        var dlg = new SaveFileDialog
        {
            Title = "匯出硬體報告",
            Filter = "HTML 報告（可列印／另存 PDF）(*.html)|*.html"
                   + "|Markdown（貼上論壇或議題）(*.md)|*.md"
                   + "|純文字 (*.txt)|*.txt",
            FileName = $"XinSpect_報告_{DateTime.Now:yyyyMMdd_HHmmss}.html",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return null;

        string path = dlg.FileName;
        File.WriteAllText(path, Build(vm, FormatOf(path)), new UTF8Encoding(true));

        try
        {
            using var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = path;
            p.StartInfo.UseShellExecute = true;
            p.Start();
        }
        catch { /* 開啟失敗不影響已存檔 */ }

        return path;
    }

    /// <summary>把 Markdown 版報告放進剪貼簿（貼上論壇、議題或聊天室用）；成功回傳 true。</summary>
    public static bool CopyMarkdown(MainViewModel vm)
    {
        try
        {
            Clipboard.SetText(Build(vm, ReportFormat.Markdown));
            return true;
        }
        catch { return false; }      // 剪貼簿被別的程式鎖住時不視為錯誤，交由呼叫端提示
    }

    private static ReportFormat FormatOf(string path)
        => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? ReportFormat.Text
         : path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? ReportFormat.Markdown
         : ReportFormat.Html;

    private static string Build(MainViewModel vm, ReportFormat fmt)
    {
        bool mask = vm.Settings.ReportMaskIdentity;
        var secs = Collect(vm, mask);
        return fmt switch
        {
            ReportFormat.Text => RenderText(secs, vm, mask),
            ReportFormat.Markdown => RenderMarkdown(secs, vm, mask),
            _ => RenderHtml(secs, vm, mask),
        };
    }

    private enum ReportFormat { Html, Markdown, Text }

    // ── 中介表示 ──────────────────────────────────────────────────────────────

    /// <summary>報告的一節；沒有任何內容的節不會被加入，故報告不會出現空章節。</summary>
    private sealed class Section
    {
        public Section(string id, string title) { Id = id; Title = title; }
        public string Id { get; }
        public string Title { get; }
        public List<Block> Blocks { get; } = new();
        public bool HasContent => Blocks.Count > 0;
    }

    /// <summary>一節裡的一塊內容：說明文字、名稱／數值列表，或多欄表格（三者可並存）。</summary>
    private sealed class Block
    {
        public string? Caption;                          // 表格上方的小標（例：顯示卡名稱）
        public string? Note;                             // 說明文字（誠實註記多寫在這裡）
        public string? Pre;                              // 原樣保留換行的長文（AI 回覆）
        public List<(string K, string V)>? Rows;         // 名稱／數值
        public string[]? Heads;                          // 多欄表格的欄位標題
        public List<string[]>? Cells;                    // 多欄表格的內容
    }

    // ── 收集：內容只寫一次，三種格式共用 ──────────────────────────────────────

    private static List<Section> Collect(MainViewModel vm, bool mask)
    {
        var secs = new List<Section>();
        Summary(secs, vm);
        SystemInfo(secs, vm, mask);
        Processor(secs, vm);
        Memory(secs, vm);
        Graphics(secs, vm);
        Storage(secs, vm, mask);
        Monitors(secs, vm);
        Network(secs, vm, mask);
        Sensors(secs, vm);
        Benchmarks(secs, vm);
        Upgrade(secs, vm);
        AiVerdict(secs, vm);
        return secs;
    }

    /// <summary>建立一節並交給 <paramref name="body"/> 填內容；填完仍是空的就不收錄。</summary>
    private static void Add(List<Section> secs, string id, string title, Action<Section> body)
    {
        var sec = new Section(id, title);
        try { body(sec); } catch { /* 某一節取值失敗不該讓整份報告產不出來 */ }
        if (sec.HasContent) secs.Add(sec);
    }

    private static void Note(this Section s, string text)
        => s.Blocks.Add(new Block { Note = text });

    private static void Kv(this Section s, params (string K, string V)[] rows)
        => s.Blocks.Add(new Block { Rows = new List<(string, string)>(rows) });

    private static void Tbl(this Section s, string? caption, string[] heads, List<string[]> cells)
    {
        if (cells.Count == 0) return;
        s.Blocks.Add(new Block { Caption = caption, Heads = heads, Cells = cells });
    }

    private static string SevText(Severity s) => s switch
    {
        Severity.Good => "良好",
        Severity.Warning => "注意",
        Severity.Serious => "警告",
        Severity.Critical => "危險",
        _ => "一般",
    };

    /// <summary>遮蔽可識別資訊；關閉時原值輸出。空值一律回「—」以免出現空白格。</summary>
    private static string Id(string? value, bool mask)
        => mask ? "（已遮蔽）" : string.IsNullOrWhiteSpace(value) ? "—" : value;

    // ── 各節內容 ──────────────────────────────────────────────────────────────

    private static void Summary(List<Section> secs, MainViewModel vm) => Add(secs, "summary", "整機摘要", s =>
    {
        var h = vm.Health;
        s.Kv(
            ("健康評分", $"{h.ScoreText} / 100（{SevText(h.ScoreSeverity)}）"),
            ("評語", h.Summary),
            ("建議", h.Advice));

        var rows = new List<string[]>();
        foreach (var i in h.Items)
            rows.Add(new[] { i.Label, i.StatusText, i.ValueText, i.Detail });
        s.Tbl(null, new[] { "項目", "狀態", "數值", "說明" }, rows);
        s.Note("評分由本機當下的讀值換算，沒讀到的項目不計分也不猜測；換一個時間點重測，分數本來就會不同。");
    });

    private static void SystemInfo(List<Section> secs, MainViewModel vm, bool mask) => Add(secs, "system", "系統", s =>
    {
        var y = vm.System;
        s.Kv(
            ("作業系統", y.OsName), ("版本", y.OsVersion), ("系統架構", y.OsArch),
            ("主機名稱", Id(y.HostName, mask)), ("使用者", Id(y.UserName, mask)),
            ("開機時間", y.BootTime), ("已運行", y.Uptime), ("安裝日期", y.InstallDate),
            ("製造商", y.SystemManufacturer), ("機型", y.SystemModel),
            ("主機板", $"{y.BoardVendor} {y.BoardModel}".Trim()),
            ("BIOS", $"{y.BiosVendor} {y.BiosVersion}（{y.BiosDate}）"));

        var b = vm.Mainboard;
        if (b.Loaded)
            s.Kv(
                ("晶片組", b.ChipsetName), ("北橋", b.Northbridge), ("南橋", b.Southbridge),
                ("匯流排規格", b.BusSpec), ("顯示介面", b.GraphicInterface),
                ("PCIe 連結", $"{b.PcieLinkWidth} ・ {b.PcieLinkSpeed}"),
                ("UEFI", b.Uefi), ("LPCIO", $"{b.LpcioVendor} {b.LpcioModel}".Trim()));
    });

    private static void Processor(List<Section> secs, MainViewModel vm) => Add(secs, "cpu", "處理器", s =>
    {
        var c = vm.Cpu;
        s.Kv(
            ("型號", c.Name), ("製造商", c.Manufacturer), ("插槽", c.Socket),
            ("實體核心", $"{c.Cores} 核"), ("邏輯處理器", $"{c.Threads} 執行緒"),
            ("額定頻率", $"{c.MaxClockMHz:0} MHz"), ("L2 / L3 快取", $"{c.L2Cache} / {c.L3Cache}"));

        var d = vm.CpuDetail;
        if (d.Loaded)
            s.Kv(
                ("代號", d.Codename), ("製程", d.Technology), ("封裝", d.Package),
                ("步進 / 微碼", $"{d.Stepping} / {d.Microcode}"),
                ("倍頻", d.Multiplier), ("標定頻率", d.StockFreq),
                ("非睿頻上限 / 睿頻上限", $"{d.MaxNonTurbo} / {d.MaxTurbo}"),
                ("功耗上限 PL1 / PL2", $"{d.PowerMaxPl1} / {d.PowerMaxPl2}"),
                ("Tjmax", d.Tjmax),
                ("快取 L1D / L1I / L2 / L3", $"{d.L1D} / {d.L1I} / {d.L2} / {d.L3}"),
                ("指令集", d.Instructions));

        if (vm.Live is { } live)
        {
            s.Kv(
                ("目前最高時脈", live.CpuClockText), ("總使用率", live.CpuLoadText),
                ("封裝溫度", live.CpuTempText), ("封裝功耗", live.CpuPowerText),
                ("核心電壓", live.CpuVoltText));

            var rows = new List<string[]>();
            foreach (var core in live.CpuCores)
                rows.Add(new[] { core.Name, core.ClockText, core.LoadText, core.TempText });
            s.Tbl("各核心即時讀值", new[] { "核心", "時脈", "使用率", "溫度" }, rows);
        }
    });

    private static void Memory(List<Section> secs, MainViewModel vm) => Add(secs, "memory", "記憶體", s =>
    {
        var t = vm.Timings;
        s.Kv(
            ("狀態", t.Status), ("記憶體類型", t.MemoryTypeText), ("通道", t.ChannelsText),
            ("DRAM 頻率", t.FrequencyText), ("資料速率", t.DataRateText),
            ("主要時序", t.PrimaryTimingsText),
            ("CL / tRCD / tRP / tRAS", $"{t.CL} / {t.TRCD} / {t.TRP} / {t.TRAS}"),
            ("tRFC", t.TRFC), ("命令率", t.CommandRate));
        if (vm.Live is { } live) s.Kv(("目前用量", live.MemUsageText));

        var rows = new List<string[]>();
        foreach (var m in vm.Modules)
            rows.Add(new[] { m.Slot, m.Manufacturer, m.PartNumber, m.CapacityText, m.SpeedText });
        s.Tbl("已安裝模組", new[] { "插槽", "製造商", "型號", "容量", "速率" }, rows);

        var spd = new List<string[]>();
        foreach (var m in vm.SpdModules)
            spd.Add(new[] { m.Slot, m.MemoryType, m.Size, m.MaxBandwidth, m.NominalVoltage, m.XmpSummary });
        s.Tbl("SPD 深度資料", new[] { "插槽", "類型", "容量", "最大頻寬", "標準電壓", "XMP／EXPO" }, spd);
    });

    private static void Graphics(List<Section> secs, MainViewModel vm) => Add(secs, "gpu", "顯示卡", s =>
    {
        if (vm.Live is { Gpus.Count: > 0 } gl)
            foreach (var g in gl.Gpus)
            {
                s.Blocks.Add(new Block
                {
                    Caption = $"{g.VendorText}　{g.Name}",
                    Rows = new List<(string, string)>
                    {
                        ("核心時脈", g.CoreClockText), ("記憶體時脈", g.MemClockText),
                        ("使用率", g.LoadText), ("溫度", g.TempText),
                        ("顯示記憶體", g.VramText), ("功耗", g.PowerText),
                    },
                });
            }

        foreach (var d in vm.GpuDetails)
            s.Blocks.Add(new Block
            {
                Caption = $"{d.Title}（深度規格）",
                Rows = new List<(string, string)>
                {
                    ("板卡製造商", d.BoardManufacturer), ("板卡型號", d.BoardPartNumber),
                    ("代號 / 架構", $"{d.Codename} / {d.CoreFamily}"), ("製程", d.Technology),
                    ("運算單元 / ROP / TMU", $"{d.Cores} / {d.RopUnits} / {d.TmUnits}"),
                    ("顯示記憶體", $"{d.MemoryType} {d.MemorySize}（{d.MemoryBusWidth}）"),
                    ("基礎時脈（核心 / 記憶體）", $"{d.BaseCoreClock} / {d.BaseMemClock}"),
                    ("加速時脈（核心 / 記憶體）", $"{d.BoostCoreClock} / {d.BoostMemClock}"),
                    ("功耗上限 / 溫度上限", $"{d.PowerLimit} / {d.ThermalLimit}"),
                    ("驅動程式 / WDDM", $"{d.DriverVersion} / {d.Wddm}"),
                },
            });

        if (vm.CudaVersion is { Length: > 0 } cuda && cuda != "—") s.Kv(("CUDA", cuda));
    });

    private static void Storage(List<Section> secs, MainViewModel vm, bool mask) => Add(secs, "disk", "儲存裝置", s =>
    {
        var phys = new List<string[]>();
        foreach (var d in vm.PhysicalDisks)
            phys.Add(new[]
            {
                d.CountText, d.Model, d.TypeText, d.CapacityText, d.InterfaceType,
                Id(d.SerialNumber, mask), d.Firmware, $"{SevText(d.HealthSeverity)}　{d.HealthDetail}".Trim(),
            });
        s.Tbl("實體磁碟", new[] { "編號", "型號", "類型", "容量", "介面", "序號", "固件", "健康" }, phys);

        if (vm.Live is { Drives.Count: > 0 } dl)
        {
            var rows = new List<string[]>();
            foreach (var d in dl.Drives)
                rows.Add(new[] { d.Name, d.TypeText, d.CapacityText, d.TempText, d.LifeText, d.UsedText });
            s.Tbl("即時讀值", new[] { "裝置", "類型", "容量", "溫度", "剩餘壽命", "已用" }, rows);
        }

        var vols = new List<string[]>();
        foreach (var v in vm.Volumes.Volumes)
            vols.Add(new[] { v.Name, v.Label, v.TypeText, v.SizeText, v.FreeText, SevText(v.Severity) });
        s.Tbl("磁碟區", new[] { "磁碟區", "標籤", "檔案系統", "已用 / 總量", "可用", "空間狀態" }, vols);
        if (vm.Volumes.Volumes.Count > 0) s.Kv(("整機儲存", vm.Volumes.SummaryText));
    });

    private static void Monitors(List<Section> secs, MainViewModel vm) => Add(secs, "monitor", "螢幕與色域", s =>
    {
        var rows = new List<string[]>();
        foreach (var m in vm.Monitors)
            rows.Add(new[] { m.Name, m.Manufacturer, m.SrgbText, m.AdobeText, m.DciText, m.Assessment });
        s.Tbl(null, new[] { "螢幕", "製造商", "sRGB", "Adobe RGB", "DCI-P3", "評估" }, rows);
        if (rows.Count > 0)
            s.Note("色域由 EDID 內的原色座標換算，反映的是螢幕韌體聲稱的能力，不等於校色儀實測值。");
    });

    private static void Network(List<Section> secs, MainViewModel vm, bool mask) => Add(secs, "net", "網路", s =>
    {
        if (vm.Net is { Adapters.Count: > 0 } net)
        {
            var rows = new List<string[]>();
            foreach (var a in net.Adapters)
                rows.Add(new[] { a.Name, a.TypeText, a.Ipv4, Id(a.Mac, mask), a.LinkSpeedText });
            s.Tbl("使用中的介面", new[] { "介面", "類型", "IPv4", "MAC", "連線速度" }, rows);
        }

        var nics = new List<string[]>();
        foreach (var n in vm.InstalledNics)
            nics.Add(new[] { n.Name, n.Manufacturer, n.TypeText, n.SpeedText, Id(n.Mac, mask) });
        s.Tbl("已安裝的網路卡", new[] { "名稱", "製造商", "類型", "額定速度", "MAC" }, nics);
    });

    private static void Sensors(List<Section> secs, MainViewModel vm) => Add(secs, "sensor", "感測器", s =>
    {
        if (vm.Live is not { AllSensors.Count: > 0 } sl) return;
        var rows = new List<string[]>();
        foreach (var x in sl.AllSensors)
            rows.Add(new[] { x.Group, x.Name, x.ValueText, x.MinText, x.MaxText });
        s.Tbl(null, new[] { "群組", "名稱", "數值", "最小", "最大" }, rows);
        s.Note("最小／最大為本次執行期間的極值，關閉曦覽後重新開啟即重新累計。");
    });

    private static void Benchmarks(List<Section> secs, MainViewModel vm) => Add(secs, "bench", "跑分紀錄", s =>
    {
        var rows = new List<string[]>();
        foreach (var r in vm.Benchmarks.Recent)
            rows.Add(new[] { r.TimeText, r.Title, r.Config, r.ScoreText, r.Conditions });
        s.Tbl(null, new[] { "時間", "項目", "設定", "成績", "量測條件" }, rows);
        if (rows.Count > 0)
            s.Note("這些數字全部來自這台機器。曦覽不內建其他機器的參考分數：同型號的機器換一支散熱膏、"
                 + "換一種電源計劃就差一成，內建對照表只是把猜測寫得像量測。同項目、同設定之間才可相比；"
                 + "量測條件是當時的實際溫度與頻率讀值。");
    });

    private static void Upgrade(List<Section> secs, MainViewModel vm) => Add(secs, "upgrade", "升級建議", s =>
    {
        var r = UpgradeAdvisor.Analyze(UpgradeFactsCollector.Collect(vm));
        s.Kv(
            ("瓶頸判定", $"{r.Bottleneck}（{SevText(r.BottleneckSeverity)}）"),
            ("說明", r.BottleneckDetail),
            ("可信度", r.Confidence));

        var rows = new List<string[]>();
        foreach (var i in r.Items)
            rows.Add(new[] { i.PriorityText, i.PartText, i.Title, i.Evidence, i.Action, i.Gain, i.Cost });
        s.Tbl(null, new[] { "優先度", "部位", "建議", "依據", "行動", "預期效益", "花費" }, rows);

        s.Note(r.HasItems
            ? "每一條建議都附了「依據」——那是本機實際讀到的數字。讀值不足以支撐的規則會整條跳過，不以典型值代入。"
            : "現有讀值不足以支撐任何一條建議。累積一段時間的歷史記錄（設定頁可開啟）之後再看會更準。");
    });

    private static void AiVerdict(List<Section> secs, MainViewModel vm) => Add(secs, "ai", "AI 評價", s =>
    {
        string text = "";
        foreach (var m in vm.Ai.Messages)
            if (m.IsAssistant && m.Text.Trim().Length > 0) text = m.Text.Trim();
        if (text.Length == 0) return;

        s.Note($"以下是 AI 分頁最後一次的回覆，由你自己設定的模型（{vm.Settings.AiModel}）產生，"
             + "不是量測結果，也不是曦覽的判斷；模型可能誤讀或過度推論，請以上面各節的讀值為準。");
        s.Blocks.Add(new Block { Pre = text });
    });

    // ── HTML ──────────────────────────────────────────────────────────────────
    // 單一自帶樣式的檔案：不外連字型、不外連指令碼，離線開啟與轉存 PDF 都不會少東西。
    // 螢幕上沿用曦覽的深色配色；列印時整份切成淺色（@media print），
    // 否則按 Ctrl+P 存出來的會是一整片吃墨的黑底。

    private const string HtmlHead = """
<!DOCTYPE html><html lang="zh-Hant"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>曦覽 XinSpect 硬體報告</title>
<style>
:root{
  color-scheme:dark;
  --plane:#0d0d0d; --surface:#1a1a19; --surface2:#232322;
  --ink:#ffffff; --ink2:#c3c2b7; --muted:#898781;
  --line:#2c2c2a; --line2:#1f1f1e; --accent:#3987e5; --accent-dim:#1c5cab;
}
*{box-sizing:border-box}
body{margin:0;background:var(--plane);color:var(--ink2);
  font-family:"Microsoft JhengHei UI","Microsoft JhengHei","Segoe UI",sans-serif;
  font-size:14px;line-height:1.65;-webkit-font-smoothing:antialiased}
.wrap{max-width:1000px;margin:0 auto;padding:36px 28px 72px}
.brand{display:flex;align-items:center;gap:9px;color:var(--muted);font-size:13px;letter-spacing:.05em}
.brand i{width:6px;height:16px;border-radius:3px;background:linear-gradient(180deg,#4c96f0,var(--accent-dim));display:inline-block}
h1{color:var(--ink);font-size:27px;margin:10px 0 0;font-weight:600}
.sub{color:var(--muted);font-size:13px;margin-top:5px}
.hero{display:flex;flex-wrap:wrap;gap:12px;margin:22px 0 4px}
.tile{flex:1 1 210px;background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
.tile .cap{color:var(--muted);font-size:12.5px}
.tile .big{color:var(--ink);font-size:26px;font-weight:600;line-height:1.25;margin-top:2px}
.tile .small{color:var(--ink2);font-size:13px;margin-top:3px}
.toc{display:flex;flex-wrap:wrap;gap:7px;margin:22px 0 6px}
.toc a{color:var(--ink2);background:var(--surface2);border:1px solid var(--line);
  border-radius:6px;padding:4px 11px;font-size:12.5px;text-decoration:none}
.toc a:hover{color:var(--ink);border-color:var(--accent)}
.bar{display:flex;gap:9px;align-items:center;margin:18px 0 0}
button{font-family:inherit;font-size:13px;color:var(--ink);background:var(--surface2);
  border:1px solid var(--line);border-radius:7px;padding:7px 15px;cursor:pointer}
button:hover{border-color:var(--accent)}
h2{color:var(--ink);font-size:17.5px;margin:40px 0 6px;padding-bottom:8px;
  border-bottom:1px solid var(--line);font-weight:600;scroll-margin-top:14px}
.cap2{color:var(--ink);font-size:14px;font-weight:600;margin:18px 0 2px}
.note{color:var(--muted);font-size:12.5px;margin:8px 0 2px;padding-left:11px;border-left:2px solid var(--line)}
pre{white-space:pre-wrap;word-wrap:break-word;background:var(--surface);border:1px solid var(--line);
  border-radius:9px;padding:14px 16px;margin:12px 0 2px;color:var(--ink2);
  font-family:inherit;font-size:13.5px;line-height:1.7}
table{width:100%;border-collapse:collapse;margin:8px 0 2px;font-size:13.5px}
td,th{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line2);vertical-align:top}
th{color:var(--muted);font-weight:600;font-size:12.5px;background:var(--surface);
  border-bottom:1px solid var(--line);white-space:nowrap}
.kv td:first-child{color:var(--muted);width:210px}
.val{color:var(--ink)}
.foot{margin-top:52px;padding-top:16px;border-top:1px solid var(--line);color:var(--muted);font-size:12.5px}
@media print{
  :root{color-scheme:light;
    --plane:#ffffff; --surface:#f6f5f2; --surface2:#efeee9;
    --ink:#111114; --ink2:#26262a; --muted:#5c5a55; --line:#c9c7c0; --line2:#e2e0da}
  @page{size:A4;margin:14mm 12mm}
  body{font-size:11pt;line-height:1.5}
  .wrap{max-width:none;padding:0}
  .noprint{display:none!important}
  h1{font-size:20pt}
  h2{font-size:13pt;margin:16pt 0 4pt;break-after:avoid}
  .cap2{break-after:avoid}
  tr,.tile,.note,pre{break-inside:avoid}
  thead{display:table-header-group}
  a{color:inherit;text-decoration:none}
}
</style></head><body><div class="wrap">
""";

    private static string RenderHtml(List<Section> secs, MainViewModel vm, bool mask)
    {
        var sb = new StringBuilder(65536);
        sb.Append(HtmlHead);
        sb.Append("<div class=\"brand\"><i></i>曦覽 XinSpect</div>");
        sb.Append("<h1>硬體報告</h1>");
        sb.Append($"<div class=\"sub\">產生時間：{Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))}"
                + $" ・ 主機：{Esc(Id(vm.System.HostName, mask))}"
                + $" ・ {Esc(vm.Cpu.Name)}</div>");

        // 摘要磁貼：先給結論，細節在下面各節
        var h = vm.Health;
        sb.Append("<div class=\"hero\">");
        Tile(sb, "健康評分", $"{Esc(h.ScoreText)} <span style=\"font-size:15px;color:var(--muted)\">/ 100</span>",
             $"{Esc(SevText(h.ScoreSeverity))} ・ {Esc(h.Summary)}");
        Tile(sb, "系統", Esc(vm.System.OsName), Esc($"{vm.System.SystemManufacturer} {vm.System.SystemModel}".Trim()));
        Tile(sb, "處理器與記憶體", Esc($"{vm.Cpu.Cores} 核 {vm.Cpu.Threads} 執行緒"),
             Esc(vm.Timings.DataRateText + " ・ " + vm.Timings.ChannelsText));
        sb.Append("</div>");

        sb.Append("<div class=\"toc noprint\">");
        foreach (var s in secs) sb.Append($"<a href=\"#{Esc(s.Id)}\">{Esc(s.Title)}</a>");
        sb.Append("</div>");
        sb.Append("<div class=\"bar noprint\"><button onclick=\"window.print()\">列印／另存 PDF</button>"
                + "<span class=\"sub\">在列印對話框選「Microsoft Print to PDF」或「另存為 PDF」即可，版面已為紙張排好。</span></div>");

        foreach (var s in secs)
        {
            sb.Append($"<h2 id=\"{Esc(s.Id)}\">{Esc(s.Title)}</h2>");
            foreach (var b in s.Blocks)
            {
                if (b.Caption is { Length: > 0 }) sb.Append($"<div class=\"cap2\">{Esc(b.Caption)}</div>");
                if (b.Rows is { Count: > 0 })
                {
                    sb.Append("<table class=\"kv\">");
                    foreach (var (k, v) in b.Rows)
                        sb.Append($"<tr><td>{Esc(k)}</td><td class=\"val\">{Esc(v)}</td></tr>");
                    sb.Append("</table>");
                }
                if (b.Heads is { } heads && b.Cells is { Count: > 0 } cells)
                {
                    sb.Append("<table><thead><tr>");
                    foreach (var head in heads) sb.Append($"<th>{Esc(head)}</th>");
                    sb.Append("</tr></thead><tbody>");
                    foreach (var row in cells)
                    {
                        sb.Append("<tr>");
                        for (int i = 0; i < row.Length; i++)
                            sb.Append(i == 0 ? $"<td class=\"val\">{Esc(row[i])}</td>" : $"<td>{Esc(row[i])}</td>");
                        sb.Append("</tr>");
                    }
                    sb.Append("</tbody></table>");
                }
                if (b.Pre is { Length: > 0 }) sb.Append($"<pre>{Esc(b.Pre)}</pre>");
                if (b.Note is { Length: > 0 }) sb.Append($"<div class=\"note\">{Esc(b.Note)}</div>");
            }
        }

        sb.Append($"<div class=\"foot\">{Esc(FootLine(mask))}</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void Tile(StringBuilder sb, string cap, string bigHtml, string small)
        => sb.Append($"<div class=\"tile\"><div class=\"cap\">{Esc(cap)}</div>"
                   + $"<div class=\"big\">{bigHtml}</div><div class=\"small\">{small}</div></div>");

    private static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");

    // ── Markdown ──────────────────────────────────────────────────────────────
    // 給論壇、GitHub 議題與聊天室用：表格是標準 GFM 表格，說明文字用引言，AI 回覆用圍籬
    // 包起來（避免它自己的 Markdown 語法把版面弄亂）。

    private static string RenderMarkdown(List<Section> secs, MainViewModel vm, bool mask)
    {
        var sb = new StringBuilder(32768);
        sb.Append("# 曦覽 XinSpect 硬體報告\n\n");
        sb.Append($"產生時間 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ・ 主機 {Id(vm.System.HostName, mask)}"
                + $" ・ {Md(vm.Cpu.Name)}\n");

        foreach (var s in secs)
        {
            sb.Append($"\n## {s.Title}\n");
            foreach (var b in s.Blocks)
            {
                if (b.Caption is { Length: > 0 }) sb.Append($"\n**{Md(b.Caption)}**\n");
                if (b.Rows is { Count: > 0 })
                {
                    sb.Append("\n| 項目 | 內容 |\n| --- | --- |\n");
                    foreach (var (k, v) in b.Rows) sb.Append($"| {Md(k)} | {Md(v)} |\n");
                }
                if (b.Heads is { } heads && b.Cells is { Count: > 0 } cells)
                {
                    sb.Append('\n');
                    Row(sb, heads);
                    sb.Append('|');
                    for (int i = 0; i < heads.Length; i++) sb.Append(" --- |");
                    sb.Append('\n');
                    foreach (var row in cells) Row(sb, row);
                }
                if (b.Pre is { Length: > 0 }) sb.Append($"\n```\n{b.Pre}\n```\n");
                if (b.Note is { Length: > 0 }) sb.Append($"\n> {Md(b.Note)}\n");
            }
        }

        sb.Append($"\n---\n\n{FootLine(mask)}\n");
        return sb.ToString();

        static void Row(StringBuilder sb, string[] cells)
        {
            sb.Append('|');
            foreach (var c in cells) sb.Append(' ').Append(Md(c)).Append(" |");
            sb.Append('\n');
        }
    }

    /// <summary>表格格內不能有換行，直豎線也得跳脫，否則欄位會被切錯。</summary>
    private static string Md(string? s)
        => (s ?? "").Replace("|", "\\|").Replace("\r", "").Replace('\n', ' ');

    // ── 純文字 ────────────────────────────────────────────────────────────────
    // 貼進純文字信件或工單用。欄寬依實際內容對齊（以顯示寬度計，中日韓字元算兩格），
    // 而不是硬塞固定寬度——後者遇到長型號就整排歪掉。

    private static string RenderText(List<Section> secs, MainViewModel vm, bool mask)
    {
        var sb = new StringBuilder(32768);
        sb.Append("曦覽 XinSpect 硬體報告\n");
        sb.Append(new string('=', 60)).Append('\n');
        sb.Append($"產生時間　{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append($"主機　　　{Id(vm.System.HostName, mask)}\n");
        sb.Append($"處理器　　{vm.Cpu.Name}\n");

        foreach (var s in secs)
        {
            sb.Append("\n\n【").Append(s.Title).Append("】\n");
            sb.Append(new string('-', 60)).Append('\n');

            foreach (var b in s.Blocks)
            {
                if (b.Caption is { Length: > 0 }) sb.Append('\n').Append(b.Caption).Append('\n');
                if (b.Rows is { Count: > 0 })
                {
                    int w = 0;
                    foreach (var (k, _) in b.Rows) w = Math.Max(w, Wide(k));
                    foreach (var (k, v) in b.Rows)
                        sb.Append("  ").Append(Pad(k, w)).Append("  ").Append(One(v)).Append('\n');
                }
                if (b.Heads is { } heads && b.Cells is { Count: > 0 } cells) Table(sb, heads, cells);
                if (b.Pre is { Length: > 0 }) sb.Append('\n').Append(b.Pre.Replace("\r", "")).Append('\n');
                if (b.Note is { Length: > 0 }) sb.Append("\n  ※ ").Append(One(b.Note)).Append('\n');
            }
        }

        sb.Append("\n\n").Append(new string('-', 60)).Append('\n').Append(FootLine(mask)).Append('\n');
        return sb.ToString();
    }

    private static void Table(StringBuilder sb, string[] heads, List<string[]> cells)
    {
        int n = heads.Length;
        var w = new int[n];
        for (int i = 0; i < n; i++) w[i] = Wide(heads[i]);
        foreach (var row in cells)
            for (int i = 0; i < n && i < row.Length; i++) w[i] = Math.Max(w[i], Wide(One(row[i])));

        sb.Append('\n');
        Line(sb, heads, w);
        sb.Append("  ");
        for (int i = 0; i < n; i++) sb.Append(new string('-', w[i])).Append(i == n - 1 ? "" : "  ");
        sb.Append('\n');
        foreach (var row in cells) Line(sb, row, w);

        static void Line(StringBuilder sb, string[] row, int[] w)
        {
            sb.Append("  ");
            for (int i = 0; i < w.Length; i++)
            {
                string cell = One(i < row.Length ? row[i] : "");
                sb.Append(i == w.Length - 1 ? cell : Pad(cell, w[i]) + "  ");
            }
            sb.Append('\n');
        }
    }

    private static string One(string? s)
        => (s ?? "").Replace("\r", "").Replace('\n', ' ').TrimEnd();

    /// <summary>以等寬字型下的顯示寬度計算：中日韓與全形符號佔兩格。</summary>
    private static int Wide(string? s)
    {
        int n = 0;
        foreach (char c in s ?? "") n += IsFullWidth(c) ? 2 : 1;
        return n;
    }

    private static string Pad(string? s, int width)
    {
        int pad = width - Wide(s);
        return pad > 0 ? s + new string(' ', pad) : s ?? "";
    }

    private static bool IsFullWidth(char c)
        => c is >= '\u1100' and <= '\u115F'          // 韓文字母
            or >= '\u2E80' and <= '\u303E'           // 部首、標點
            or >= '\u3041' and <= '\u33FF'           // 假名、注音、相容字元
            or >= '\u3400' and <= '\u4DBF'           // 擴充 A
            or >= '\u4E00' and <= '\u9FFF'           // 基本區
            or >= '\uA000' and <= '\uA4CF'
            or >= '\uAC00' and <= '\uD7A3'           // 韓文音節
            or >= '\uF900' and <= '\uFAFF'           // 相容表意
            or >= '\uFE30' and <= '\uFE6F'
            or >= '\uFF00' and <= '\uFF60'           // 全形英數與標點
            or >= '\uFFE0' and <= '\uFFE6';

    private static string FootLine(bool mask)
        => "本報告由曦覽 XinSpect 在本機產生，未上傳任何資料。所有數值皆為產生當時的實際讀值，"
         + "讀不到的項目以「—」表示，不以典型值或估算值填補。"
         + (mask ? "已依設定遮蔽主機名稱、使用者、MAC 位址與磁碟序號。" : "");
}
