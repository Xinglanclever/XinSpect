using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>一條可靠性事件列。</summary>
public sealed class ReliabilityRow
{
    public ReliabilityRow(string category, string time, string detail)
    { Category = category; Time = time; Detail = detail; }
    public string Category { get; }
    public string Time { get; }
    public string Detail { get; }
}

/// <summary>
/// 可靠性歷史：機器自己的病歷。非預期關機（Kernel-Power 41／EventLog 6008）、
/// 藍屏（BugCheck 1001，含代碼）、應用程式當機與停止回應（Application 1000／1002）、
/// 開機耗時（Diagnostics-Performance 100）。近 30 天的時間軸。
/// </summary>
/// <remarks>誠實界線：只呈現事件與時間，不下診斷結論；開機耗時是 Windows Diagnostics-Performance
/// 量到的數字（來源標明）。各頻道以 XPath 過濾查詢，不全量掃描。</remarks>
public sealed class ReliabilityHistoryService : ObservableObject
{
    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _summary = "尚未讀取。";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private string _bootTrend = "—";
    public string BootTrend { get => _bootTrend; private set => SetProperty(ref _bootTrend, value); }

    public ObservableCollection<ReliabilityRow> Rows { get; } = [];

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Summary = "讀取中…";
        Rows.Clear();
        try
        {
            const int days = 30;
            var since = DateTime.Now.AddDays(-days);
            var (unexpected, bugchecks, appCrashes, bootTimes, rows, bootChannelMissing) =
                await Task.Run(() => Collect(since));

            Summary = $"近 {days} 天："
                    + (unexpected > 0 ? $"非預期關機 {unexpected} 次" : "無非預期關機") + "・"
                    + (bugchecks > 0 ? $"藍屏 {bugchecks} 次" : "無藍屏") + "・"
                    + (appCrashes > 0 ? $"應用程式當機/停止回應 {appCrashes} 次" : "無應用程式當機") + "。";

            if (bootTimes.Count >= 2)
            {
                BootTrend = "最近開機耗時（新→舊）："
                          + string.Join("・", bootTimes.Take(5).Select(b => $"{b.Ms:N0} ms"))
                          + (bootTimes[0].Ms > bootTimes[^1].Ms * 1.3 ? "（近期有變慢跡象；來源：Diagnostics-Performance 100）" : "");
            }
            else if (bootTimes.Count == 1) BootTrend = $"僅一筆開機紀錄：{bootTimes[0].Ms:N0} ms";
            else if (bootChannelMissing)
                BootTrend = "開機耗時：這個 Windows 版本沒有 Diagnostics-Performance 頻道"
                          + "（Windows Server 不隨附它，用戶端的 Windows 10／11 才有），所以沒有資料來源。"
                          + "「開機耗時分解」那張卡也是同一個原因。";
            else BootTrend = "無開機耗時紀錄（頻道存在但還沒有事件）。";

            foreach (var r in rows.Take(80)) Rows.Add(r);
        }
        catch (Exception ex)
        {
            Summary = "無法讀取可靠性事件：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static (int Unexpected, int Bugchecks, int AppCrashes, List<(DateTime Time, long Ms)> BootTimes,
                    List<ReliabilityRow> Rows, bool BootChannelMissing)
        Collect(DateTime since)
    {
        int unexpected = 0, bugchecks = 0, appCrashes = 0;
        var rows = new List<ReliabilityRow>();
        var bootTimes = new List<(DateTime, long)>();
        string msFilter = $"*[System[TimeCreated[timediff(@SystemTime) <= {(long)(DateTime.Now - since).TotalMilliseconds}]]]";

        // System：非預期關機 41（Kernel-Power）／6008（EventLog）與藍屏 1001（BugCheck）
        using (var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName,
            msFilter.Replace("*[System[", "*[System[(EventID=41 or EventID=6008 or EventID=1001) and "))))
        {
            while (true)
            {
                EventRecord? rec;
                try { rec = reader.ReadEvent(); } catch (EventLogException) { break; }
                if (rec is null) break;
                using (rec)
                {
                    var time = (rec.TimeCreated ?? DateTime.Now).ToString("MM-dd HH:mm");
                    if (rec.Id is 41 or 6008)
                    {
                        unexpected++;
                        rows.Add(new ReliabilityRow("非預期關機", time, $"#{rec.Id}（{rec.ProviderName}）"));
                    }
                    else
                    {
                        bugchecks++;
                        rows.Add(new ReliabilityRow("藍屏（BugCheck）", time, FirstLine(SafeFormat(rec))));
                    }
                }
            }
        }

        // Application：當機 1000／停止回應 1002
        using (var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName,
            msFilter.Replace("*[System[", "*[System[(EventID=1000 or EventID=1002) and "))))
        {
            while (true)
            {
                EventRecord? rec;
                try { rec = reader.ReadEvent(); } catch (EventLogException) { break; }
                if (rec is null) break;
                using (rec)
                {
                    appCrashes++;
                    rows.Add(new ReliabilityRow(rec.Id == 1000 ? "應用程式當機" : "停止回應",
                        (rec.TimeCreated ?? DateTime.Now).ToString("MM-dd HH:mm"), FirstLine(SafeFormat(rec))));
                }
            }
        }

        // 開機耗時：Diagnostics-Performance 事件 100（第 3 個屬性為 BootTime ms）
        bool bootChannelMissing = false;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(
                "Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                msFilter.Replace("*[System[", "*[System[(EventID=100) and ")));
            while (bootTimes.Count < 20)
            {
                EventRecord? rec;
                try { rec = reader.ReadEvent(); } catch (EventLogException) { break; }
                if (rec is null) break;
                using (rec)
                {
                    long ms = 0;
                    try { ms = Convert.ToInt64(rec.Properties[2].Value); } catch { }
                    if (ms > 0) bootTimes.Add((rec.TimeCreated ?? DateTime.Now, ms));
                }
            }
        }
        catch (EventLogNotFoundException)
        {
            // 這台機器根本沒有這個頻道（Windows Server 不隨附）——與「頻道存在但沒紀錄」是兩件事，
            // 訊息必須分開，否則使用者只看到一片空白，不知道是沒事還是讀不到。
            bootChannelMissing = true;
        }
        catch { /* 其他讀取失敗則略過開機趨勢 */ }

        rows.Sort((a, b) => string.CompareOrdinal(b.Time, a.Time));
        bootTimes.Sort((a, b) => b.Item1.CompareTo(a.Item1));
        return (unexpected, bugchecks, appCrashes, bootTimes, rows, bootChannelMissing);
    }

    private static string SafeFormat(EventRecord rec)
    {
        try { return rec.FormatDescription() ?? ""; }
        catch { return "（內文無法格式化）"; }
    }

    private static string FirstLine(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines ? lines[0] : "(無內文)";
}
