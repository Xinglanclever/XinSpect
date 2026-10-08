using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace XinSpect;

/// <summary>
/// 睡眠與喚醒診斷：為什麼自己醒來、為什麼睡不下去。
/// </summary>
/// <remarks>
/// 全部走 Windows 內建的 <c>powercfg</c> 唯讀查詢，四件事：可用的睡眠狀態、上次是誰叫醒的、
/// 排定的喚醒計時器、目前有誰按著不讓系統睡，以及被允許喚醒電腦的裝置清單。
/// <para>
/// <b>刻意不解析輸出</b>：powercfg 的文字是隨語言翻譯的，照關鍵字去比對會在不同語言的 Windows 上
/// 靜靜失效，而「解析失敗」與「沒有東西阻止睡眠」長得一模一樣——那是最糟的一種錯。
/// 所以這裡原樣呈現，只加上每一段的意義說明；要判斷的是使用者，不是一段猜出來的字串比對。
/// </para>
/// </remarks>
public sealed class SleepDiagnosticsService : ObservableObject, INativeToolSource
{
    /// <summary>單一查詢的逾時（毫秒）。powercfg 偶爾會等 WMI，不能讓畫面無限轉。</summary>
    private const int TimeoutMs = 8000;

    public ObservableCollection<NativeToolSection> Sections { get; } = [];

    /// <summary>接縫的可用性：這一頁的每一段查詢都靠 powercfg，找不到它就沒有資料可談。</summary>
    /// <remarks>
    /// 刻意<b>不是</b>「查了之後有沒有輸出」——那會把「指令不存在」與「指令跑了、
    /// 而確實沒有東西阻止睡眠」混成同一種長相，而後者是一個結論、前者不是。
    /// </remarks>
    public bool Available => PowercfgPath is not null;

    /// <summary>不能用的原因（能用時必須是 null）。</summary>
    public string? UnavailableReason => Available
        ? null
        : "找不到 powercfg.exe——本頁每一段都靠它。此時畫面上的「（沒有輸出）」不代表沒有東西阻止睡眠。";

    /// <summary>powercfg.exe 的完整路徑；找不到回 null（不用猜，就是查檔案在不在）。</summary>
    internal static string? PowercfgPath
    {
        get
        {
            string p = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
            return File.Exists(p) ? p : null;
        }
    }

    /// <summary>
    /// 接縫入口。與 <see cref="Refresh"/> 同一個查詢集合，差別是這裡<b>同步</b>回傳——
    /// 畫面用的是非同步的 <see cref="Refresh"/>，不必走這條。
    /// </summary>
    public IReadOnlyList<NativeToolSection> Run() => Collect();

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }

    public bool CanRefresh => !_busy;

    private string _status = "按「重新查詢」執行 powercfg 的唯讀查詢。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _loaded;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_busy) return;
        IsBusy = true;
        Status = "正在查詢…";
        _ = Task.Run(Collect).ContinueWith(t =>
        {
            if (t.IsFaulted) { Diag.Swallow("SleepDiagnostics.Refresh", t.Exception!, "背景查詢失敗，維持空白並解除忙碌"); IsBusy = false; return; }
            Sections.Clear();
            foreach (var s in t.Result) Sections.Add(s);
            Status = "查詢完成。以上都是 powercfg 的原樣輸出——本頁不解析它的文字，"
                   + "因為那些字會隨系統語言變，比對關鍵字會在別的語言上靜靜失效。";
            IsBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static readonly (string Title, string[] Args, string What)[] Queries =
    [
        ("可用的睡眠狀態", ["/a"],
         "這台機器支援哪些睡眠狀態，以及不支援的原因。現代待機（S0 低耗電待機）與傳統 S3 是兩套機制："
         + "S0 待機下系統其實還在跑，網路與部分工作照舊，所以耗電與「睡不著」的表現都跟 S3 不同。"),

        ("上次是誰叫醒的", ["/lastwake"],
         "最近一次喚醒的來源。看到裝置實例路徑就是硬體叫的（常見是網路卡的網路喚醒、USB 滑鼠鍵盤、"
         + "或計時器）；看到「喚醒來源計數 0」通常代表是使用者自己按的。"),

        ("排定的喚醒計時器", ["/waketimers"],
         "有誰預約了把電腦叫起來。Windows Update、備份、排程工作都會下這種預約。"
         + "半夜自己亮起來多半就是在這裡。要停的話是去改那個工作，不是關睡眠。"),

        ("誰按著不讓系統睡", ["/requests"],
         "目前的電源請求。DISPLAY 有東西＝螢幕不會關；SYSTEM 有東西＝系統不會睡。"
         + "常見的是播放器、瀏覽器的影片分頁、遊戲、以及某些驅動。段落名稱（DISPLAY／SYSTEM／AWAYMODE 等）"
         + "是固定的英文關鍵字，內容則隨系統語言翻譯。"),

        ("被允許喚醒電腦的裝置", ["/devicequery", "wake_armed"],
         "這些裝置有權把電腦叫起來。滑鼠輕碰一下就醒、或網路卡收到封包就醒，來源就在這份清單裡。"),
    ];

    /// <summary>把一則查詢宣告成一個區段（不含輸出）。</summary>
    internal static NativeToolSection Describe(string title, string[] args, string what) => new()
    {
        Title = title,
        Command = "powercfg " + string.Join(" ", args),
        What = what,
        Output = "",
    };

    /// <summary>
    /// 這一頁要查哪幾件事（<b>宣告</b>）。刻意與執行分開：測試才能驗
    /// 「查了哪幾件事、命令怎麼寫、說明有沒有寫」，而不必在測試裡真的開五個 powercfg 行程。
    /// </summary>
    internal static IReadOnlyList<NativeToolSection> Describe()
    {
        var list = new List<NativeToolSection>();
        foreach (var (title, args, what) in Queries) list.Add(Describe(title, args, what));
        return list;
    }

    private static List<NativeToolSection> Collect()
    {
        var list = new List<NativeToolSection>();
        foreach (var (title, args, what) in Queries)
        {
            string output = Run(args);
            list.Add(Describe(title, args, what) with
            {
                Output = string.IsNullOrWhiteSpace(output) ? "（沒有輸出）" : output.Trim(),
            });
        }
        return list;
    }

    /// <summary>執行一條 powercfg 查詢並取回文字；失敗時如實回報，不留空白讓人以為「沒事」。</summary>
    private static string Run(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 不指定 Encoding——讓 .NET 使用系統預設字碼頁（Big5／GBK），
                // 與 powercfg.exe 的輸出編碼一致。強制 UTF-8 會在中文 Windows 上全變亂碼。
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return "無法啟動 powercfg.exe。";

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(true); } catch { }
                return $"查詢逾時（超過 {TimeoutMs / 1000} 秒），已中止。";
            }

            if (!string.IsNullOrWhiteSpace(stdout)) return stdout;
            if (!string.IsNullOrWhiteSpace(stderr)) return "powercfg 回報：" + stderr.Trim();
            return "（沒有輸出）";
        }
        catch (Exception ex)
        {
            Diag.Swallow("SleepDiagnosticsService.Run", ex, "睡眠診斷的一段查詢失敗，該段顯示錯誤原因。");
            return "查詢失敗：" + ex.Message;
        }
    }
}
