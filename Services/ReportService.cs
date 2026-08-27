using System.IO;
using System.Text;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>將目前所有硬體資訊彙整成一份深色主題 HTML 報告，另存並開啟。</summary>
public static class ReportService
{
    /// <summary>顯示另存對話框並寫出報告；成功回傳路徑，取消回傳 null。</summary>
    public static string? Export(MainViewModel vm)
    {
        var dlg = new SaveFileDialog
        {
            Title = "匯出硬體報告",
            Filter = "HTML 報告 (*.html)|*.html|純文字 (*.txt)|*.txt",
            FileName = $"XinSpect_報告_{DateTime.Now:yyyyMMdd_HHmmss}.html",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return null;

        string path = dlg.FileName;
        bool asText = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        string content = asText ? BuildText(vm) : BuildHtml(vm);
        File.WriteAllText(path, content, new UTF8Encoding(true));

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

    // ---- HTML ----------------------------------------------------------------

    private static string BuildHtml(MainViewModel vm)
    {
        var sb = new StringBuilder(16384);
        sb.Append("""
<!DOCTYPE html><html lang="zh-Hant"><head><meta charset="utf-8">
<title>曦覽 XinSpect 硬體報告</title>
<style>
:root{color-scheme:dark}
body{margin:0;background:#0d0d0d;color:#c3c2b7;font-family:"Microsoft JhengHei UI","Microsoft JhengHei","Segoe UI",sans-serif;font-size:14px;line-height:1.6}
.wrap{max-width:960px;margin:0 auto;padding:32px 28px 64px}
h1{color:#fff;font-size:26px;margin:0}
.sub{color:#898781;font-size:13px;margin-top:4px}
.accent{width:56px;height:6px;border-radius:3px;background:linear-gradient(90deg,#3987e5,#1c5cab);margin:14px 0 24px}
h2{color:#fff;font-size:17px;margin:34px 0 12px;padding-bottom:8px;border-bottom:1px solid #2c2c2a}
table{width:100%;border-collapse:collapse;margin:6px 0 4px}
td,th{text-align:left;padding:7px 10px;border-bottom:1px solid #1f1f1e;vertical-align:top}
th{color:#898781;font-weight:600;font-size:12.5px}
td:first-child{color:#898781;width:200px}
.grid td:first-child{color:#898781}
.tag{display:inline-block;padding:2px 9px;border-radius:6px;background:#232322;color:#c3c2b7;font-size:12px;margin-right:6px}
.foot{margin-top:44px;padding-top:16px;border-top:1px solid #2c2c2a;color:#898781;font-size:12.5px}
.val{color:#fff}
</style></head><body><div class="wrap">
""");
        sb.Append("<h1>曦覽 XinSpect 硬體報告</h1>");
        sb.Append($"<div class=\"sub\">產生時間：{Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))} ・ 主機：{Esc(vm.System.HostName)}</div>");
        sb.Append("<div class=\"accent\"></div>");

        var s = vm.System;
        Section(sb, "系統", new (string, string)[]
        {
            ("作業系統", s.OsName), ("版本", s.OsVersion), ("系統架構", s.OsArch),
            ("主機名稱", s.HostName), ("使用者", s.UserName),
            ("開機時間", s.BootTime), ("已運行", s.Uptime), ("安裝日期", s.InstallDate),
            ("製造商", s.SystemManufacturer), ("機型", s.SystemModel),
            ("主機板", $"{s.BoardVendor} {s.BoardModel}"),
            ("BIOS", $"{s.BiosVendor} {s.BiosVersion}（{s.BiosDate}）"),
        });

        var c = vm.Cpu;
        Section(sb, "處理器", new (string, string)[]
        {
            ("型號", c.Name), ("製造商", c.Manufacturer), ("插槽", c.Socket),
            ("實體核心", $"{c.Cores} 核"), ("邏輯處理器", $"{c.Threads} 執行緒"),
            ("額定頻率", $"{c.MaxClockMHz:0} MHz"), ("L2 / L3 快取", $"{c.L2Cache} / {c.L3Cache}"),
        });
        if (vm.Live is { } live)
        {
            Section(sb, "處理器 · 即時", new (string, string)[]
            {
                ("目前最高時脈", live.CpuClockText), ("總使用率", live.CpuLoadText),
                ("封裝溫度", live.CpuTempText), ("封裝功耗", live.CpuPowerText),
                ("核心電壓", live.CpuVoltText),
            });
            if (live.CpuCores.Count > 0)
            {
                sb.Append("<table><tr><th>核心</th><th>時脈</th><th>使用率</th><th>溫度</th></tr>");
                foreach (var core in live.CpuCores)
                    sb.Append($"<tr><td class=\"val\">{Esc(core.Name)}</td><td>{Esc(core.ClockText)}</td><td>{Esc(core.LoadText)}</td><td>{Esc(core.TempText)}</td></tr>");
                sb.Append("</table>");
            }
        }

        var t = vm.Timings;
        Section(sb, "記憶體 · 時序", new (string, string)[]
        {
            ("狀態", t.Status), ("記憶體類型", t.MemoryTypeText), ("通道", t.ChannelsText),
            ("DRAM 頻率", t.FrequencyText), ("資料速率", t.DataRateText),
            ("主要時序", t.PrimaryTimingsText),
            ("CL / tRCD / tRP / tRAS", $"{t.CL} / {t.TRCD} / {t.TRP} / {t.TRAS}"),
            ("tRFC", t.TRFC), ("命令率", t.CommandRate),
        });
        if (vm.Modules.Count > 0)
        {
            sb.Append("<table><tr><th>插槽</th><th>製造商</th><th>型號</th><th>容量</th><th>速率</th></tr>");
            foreach (var m in vm.Modules)
                sb.Append($"<tr><td class=\"val\">{Esc(m.Slot)}</td><td>{Esc(m.Manufacturer)}</td><td>{Esc(m.PartNumber)}</td><td>{Esc(m.CapacityText)}</td><td>{Esc(m.SpeedText)}</td></tr>");
            sb.Append("</table>");
        }

        if (vm.Live is { Gpus.Count: > 0 } gl)
        {
            sb.Append("<h2>顯示卡</h2>");
            foreach (var g in gl.Gpus)
            {
                sb.Append($"<div class=\"tag\">{Esc(g.VendorText)}</div><span class=\"val\">{Esc(g.Name)}</span>");
                sb.Append("<table class=\"grid\">");
                Row(sb, "核心時脈", g.CoreClockText); Row(sb, "記憶體時脈", g.MemClockText);
                Row(sb, "使用率", g.LoadText); Row(sb, "溫度", g.TempText);
                Row(sb, "顯示記憶體", g.VramText); Row(sb, "功耗", g.PowerText);
                sb.Append("</table>");
            }
        }

        if (vm.Live is { Drives.Count: > 0 } dl)
        {
            sb.Append("<h2>儲存裝置</h2><table><tr><th>裝置</th><th>類型</th><th>容量</th><th>溫度</th><th>剩餘壽命</th><th>已用</th></tr>");
            foreach (var d in dl.Drives)
                sb.Append($"<tr><td class=\"val\">{Esc(d.Name)}</td><td>{Esc(d.TypeText)}</td><td>{Esc(d.CapacityText)}</td><td>{Esc(d.TempText)}</td><td>{Esc(d.LifeText)}</td><td>{Esc(d.UsedText)}</td></tr>");
            sb.Append("</table>");
        }

        if (vm.Net is { Adapters.Count: > 0 } net)
        {
            sb.Append("<h2>網路</h2><table><tr><th>介面</th><th>類型</th><th>IPv4</th><th>MAC</th><th>連線速度</th></tr>");
            foreach (var a in net.Adapters)
                sb.Append($"<tr><td class=\"val\">{Esc(a.Name)}</td><td>{Esc(a.TypeText)}</td><td>{Esc(a.Ipv4)}</td><td>{Esc(a.Mac)}</td><td>{Esc(a.LinkSpeedText)}</td></tr>");
            sb.Append("</table>");
        }

        if (vm.Live is { AllSensors.Count: > 0 } sl)
        {
            sb.Append("<h2>感測器</h2><table><tr><th>群組</th><th>名稱</th><th>數值</th><th>最小</th><th>最大</th></tr>");
            foreach (var sensor in sl.AllSensors)
                sb.Append($"<tr><td>{Esc(sensor.Group)}</td><td class=\"val\">{Esc(sensor.Name)}</td><td>{Esc(sensor.ValueText)}</td><td>{Esc(sensor.MinText)}</td><td>{Esc(sensor.MaxText)}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<div class=\"foot\">由 曦覽 XinSpect 產生 ・ By：Xinglanclever，Claude Opus 4.8，2026年8月24日</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, (string k, string v)[] rows)
    {
        sb.Append($"<h2>{Esc(title)}</h2><table class=\"grid\">");
        foreach (var (k, v) in rows) Row(sb, k, v);
        sb.Append("</table>");
    }

    private static void Row(StringBuilder sb, string k, string v)
        => sb.Append($"<tr><td>{Esc(k)}</td><td class=\"val\">{Esc(v)}</td></tr>");

    private static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---- 純文字 --------------------------------------------------------------

    private static string BuildText(MainViewModel vm)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("曦覽 XinSpect 硬體報告");
        sb.AppendLine($"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss} ・ 主機：{vm.System.HostName}");
        sb.AppendLine(new string('=', 52));

        var s = vm.System;
        sb.AppendLine("\n【系統】");
        sb.AppendLine($"  作業系統：{s.OsName}（{s.OsVersion}）{s.OsArch}");
        sb.AppendLine($"  機型：{s.SystemManufacturer} {s.SystemModel}");
        sb.AppendLine($"  主機板：{s.BoardVendor} {s.BoardModel}");
        sb.AppendLine($"  BIOS：{s.BiosVendor} {s.BiosVersion}（{s.BiosDate}）");
        sb.AppendLine($"  已運行：{s.Uptime}");

        var c = vm.Cpu;
        sb.AppendLine("\n【處理器】");
        sb.AppendLine($"  {c.Name}");
        sb.AppendLine($"  {c.Cores} 核 {c.Threads} 執行緒 ・ {c.Socket} ・ 額定 {c.MaxClockMHz:0} MHz");
        if (vm.Live is { } live)
            sb.AppendLine($"  即時：{live.CpuClockText} ・ {live.CpuLoadText} ・ {live.CpuTempText} ・ {live.CpuPowerText}");

        var t = vm.Timings;
        sb.AppendLine("\n【記憶體時序】");
        sb.AppendLine($"  {t.MemoryTypeText} {t.DataRateText} {t.ChannelsText} ・ {t.PrimaryTimingsText}");
        foreach (var m in vm.Modules)
            sb.AppendLine($"  {m.Slot}｜{m.Manufacturer} {m.PartNumber}｜{m.CapacityText}｜{m.SpeedText}");

        if (vm.Live is { Gpus.Count: > 0 } gl)
        {
            sb.AppendLine("\n【顯示卡】");
            foreach (var g in gl.Gpus)
                sb.AppendLine($"  {g.VendorText} {g.Name} ・ {g.LoadText} ・ {g.TempText} ・ {g.VramText}");
        }

        if (vm.Live is { Drives.Count: > 0 } dl)
        {
            sb.AppendLine("\n【儲存裝置】");
            foreach (var d in dl.Drives)
                sb.AppendLine($"  {d.Name}｜{d.TypeText}｜{d.CapacityText}｜{d.TempText}｜壽命 {d.LifeText}");
        }

        if (vm.Net is { Adapters.Count: > 0 } net)
        {
            sb.AppendLine("\n【網路】");
            foreach (var a in net.Adapters)
                sb.AppendLine($"  {a.Name}｜{a.Ipv4}｜{a.Mac}｜{a.LinkSpeedText}");
        }

        sb.AppendLine("\n" + new string('=', 52));
        sb.AppendLine("由 曦覽 XinSpect 產生 ・ By：Xinglanclever，Claude Opus 4.8，2026年8月24日");
        return sb.ToString();
    }
}
