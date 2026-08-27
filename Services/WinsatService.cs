using System.IO;
using System.Diagnostics;
using System.Management;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace XinSpect;

/// <summary>
/// Windows 體驗指數（WinSAT）：即時讀取已快取的 Win32_WinSAT 子分數（CPU／記憶體／磁碟／圖形／D3D 與基礎分 WinSPR），
/// 並解析最新一次正式評分 XML 取得評分日期與原始量測值。可手動觸發 winsat formal 重新評分（隱藏視窗、非同步、容忍 RDP/D3D 失敗）。
/// </summary>
public sealed class WinsatService : ObservableObject
{
    private sealed class Snapshot
    {
        public bool HasData;
        public string State = "尚未評分";
        public double Base, Cpu, Mem, Disk, Gfx, D3d;
        public string AssessedText = "—";
        public string MemBandwidth = "—";
        public string DiskThroughput = "—";
    }

    private bool _hasData;
    public bool HasData { get => _hasData; private set => SetProperty(ref _hasData, value); }

    private string _state = "讀取中…";
    public string StateText { get => _state; private set => SetProperty(ref _state, value); }

    private string _baseScore = "—";
    public string BaseScoreText { get => _baseScore; private set => SetProperty(ref _baseScore, value); }

    private string _cpu = "—";
    public string CpuText { get => _cpu; private set => SetProperty(ref _cpu, value); }

    private string _mem = "—";
    public string MemoryText { get => _mem; private set => SetProperty(ref _mem, value); }

    private string _disk = "—";
    public string DiskText { get => _disk; private set => SetProperty(ref _disk, value); }

    private string _gfx = "—";
    public string GraphicsText { get => _gfx; private set => SetProperty(ref _gfx, value); }

    private string _d3d = "—";
    public string D3DText { get => _d3d; private set => SetProperty(ref _d3d, value); }

    private string _assessed = "—";
    public string AssessedText { get => _assessed; private set => SetProperty(ref _assessed, value); }

    private string _memBw = "—";
    public string MemBandwidthText { get => _memBw; private set => SetProperty(ref _memBw, value); }

    private string _diskTp = "—";
    public string DiskThroughputText { get => _diskTp; private set => SetProperty(ref _diskTp, value); }

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanRun)); } }
    public bool CanRun => !_running;

    private string _status = "讀取系統已快取的體驗指數…";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>讀取快取分數（開機時呼叫，瞬間完成）。</summary>
    public async Task LoadCachedAsync()
    {
        var snap = await Task.Run(ReadCached);
        Apply(snap);
        StatusLine = snap.HasData
            ? "已讀取系統快取的體驗指數。可按「重新評分」以最新硬體重跑。"
            : "系統尚無有效的體驗指數。可按「重新評分」執行 WinSAT（需數分鐘）。";
    }

    /// <summary>以 winsat formal 重新評分（隱藏、非同步），完成後重新讀取快取。</summary>
    public async Task RunFormalAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        StatusLine = "正在執行 WinSAT 正式評分，過程約需數分鐘，請稍候…（遠端桌面下 D3D 項可能略過，屬正常）";
        try
        {
            int code = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "WinSAT.exe"),
                    Arguments = "formal -restart",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return -1;
                // 兩條輸出管線必須並行汲取：若先同步 ReadToEnd 標準輸出、再讀標準錯誤，
                // 當子行程先寫滿 stderr 緩衝時雙方會互等而死結。改以非同步同時讀取後再等待結束。
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                Task.WaitAll(outTask, errTask);
                p.WaitForExit();
                return p.ExitCode;
            });

            var snap = await Task.Run(ReadCached);
            Apply(snap);
            StatusLine = snap.HasData
                ? (code == 0 ? "評分完成，已更新分數。" : $"評分結束（部分項目可能未完成，代碼 {code}），已更新可得分數。")
                : $"評分未產生有效結果（代碼 {code}）。遠端桌面或無圖形環境常見。";
        }
        catch (Exception ex)
        {
            StatusLine = "執行 WinSAT 失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Apply(Snapshot s)
    {
        HasData = s.HasData;
        StateText = s.State;
        BaseScoreText = s.HasData ? Fmt(s.Base) : "—";
        CpuText = s.HasData ? Fmt(s.Cpu) : "—";
        MemoryText = s.HasData ? Fmt(s.Mem) : "—";
        DiskText = s.HasData ? Fmt(s.Disk) : "—";
        GraphicsText = s.HasData ? Fmt(s.Gfx) : "—";
        D3DText = s.HasData ? Fmt(s.D3d) : "—";
        AssessedText = s.AssessedText;
        MemBandwidthText = s.MemBandwidth;
        DiskThroughputText = s.DiskThroughput;
    }

    private static string Fmt(double v) => v > 0 ? v.ToString("0.0") : "—";

    private static Snapshot ReadCached()
    {
        var s = new Snapshot();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_WinSAT");
            using var col = searcher.Get();
            foreach (ManagementObject mo in col)
            {
                using (mo)
                {
                    s.Base = D(mo["WinSPRLevel"]);
                    s.Cpu = D(mo["CPUScore"]);
                    s.Mem = D(mo["MemoryScore"]);
                    s.Disk = D(mo["DiskScore"]);
                    s.Gfx = D(mo["GraphicsScore"]);
                    s.D3d = D(mo["D3DScore"]);
                    s.State = MapState(mo["WinSATAssessmentState"]);
                    s.HasData = s.Base > 0 || s.Cpu > 0;
                }
                break;
            }
        }
        catch (Exception ex)
        {
            s.State = "無法讀取 Win32_WinSAT：" + ex.Message;
        }

        TryEnrichFromXml(s);
        return s;
    }

    private static double D(object? o)
    {
        try { return o is null ? 0 : Convert.ToDouble(o); } catch { return 0; }
    }

    private static string MapState(object? o)
    {
        int v; try { v = o is null ? 0 : Convert.ToInt32(o); } catch { return "未知"; }
        return v switch
        {
            1 => "有效",
            2 => "與現行硬體不符（建議重新評分）",
            3 => "尚無評分",
            4 => "無效",
            _ => "未知",
        };
    }

    /// <summary>解析最新一次正式評分 XML，補入評分日期與原始頻寬/吞吐量（best-effort）。</summary>
    private static void TryEnrichFromXml(Snapshot s)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                      "Performance", "WinSAT", "DataStore");
            if (!Directory.Exists(dir)) return;

            var files = new DirectoryInfo(dir).GetFiles("*Formal.Assessment*.WinSAT.xml");
            if (files.Length == 0) return;

            FileInfo newest = files[0];
            foreach (var f in files) if (f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;

            s.AssessedText = newest.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

            var doc = XDocument.Load(newest.FullName);

            // 記憶體頻寬（bytes/sec）→ GB/s
            var memBw = Descendant(doc, "Bandwidth");
            if (memBw is not null && double.TryParse(memBw, out var bw) && bw > 0)
                s.MemBandwidth = $"{bw / 1_000_000_000.0:0.0} GB/s";

            // 磁碟吞吐（MB/s）——名稱依版本而異，取第一個看似吞吐的節點
            var diskTp = Descendant(doc, "AvgThroughput") ?? Descendant(doc, "Throughput");
            if (diskTp is not null && double.TryParse(diskTp, out var tp) && tp > 0)
                s.DiskThroughput = tp > 1000 ? $"{tp / 1000.0:0.0} MB/s（原始 {tp:0}）" : $"{tp:0.0} MB/s";

            // 若 WMI 未取得基礎分，改用 XML 的 SystemScore
            if (!s.HasData)
            {
                var sys = Descendant(doc, "SystemScore");
                if (sys is not null && double.TryParse(sys, out var ss) && ss > 0)
                {
                    s.Base = ss;
                    s.Cpu = ParseD(Descendant(doc, "CpuScore"));
                    s.Mem = ParseD(Descendant(doc, "MemoryScore"));
                    s.Disk = ParseD(Descendant(doc, "DiskScore"));
                    s.Gfx = ParseD(Descendant(doc, "GraphicsScore"));
                    s.D3d = ParseD(Descendant(doc, "GamingScore"));
                    s.HasData = true;
                }
            }
        }
        catch { /* XML 結構隨版本而異，取不到即略過 */ }
    }

    private static string? Descendant(XDocument doc, string localName)
    {
        foreach (var e in doc.Descendants())
            if (e.Name.LocalName == localName && !string.IsNullOrWhiteSpace(e.Value))
                return e.Value.Trim();
        return null;
    }

    private static double ParseD(string? v) => double.TryParse(v, out var d) ? d : 0;
}
