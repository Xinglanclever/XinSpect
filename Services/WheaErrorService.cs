using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>一筆 WHEA 事件的呈現列。</summary>
public sealed class WheaRow
{
    public WheaRow(string time, string level, string id, string message)
    { Time = time; Level = level; Id = id; Message = message; }
    public string Time { get; }
    public string Level { get; }
    public string Id { get; }
    public string Message { get; }
}

/// <summary>
/// WHEA 硬體錯誤紀錄：讀取事件檢視器的 <c>Microsoft-Windows-WHEA-Logger</c> 頻道。
/// CPU 內部錯誤、PCIe 可修正／不可修正錯誤、記憶體與快取階層的韌體級錯誤——
/// Windows 已經替我們收好在那裡，這條路零特權且完全可靠。0 筆是好事，就照實說。
/// </summary>
public sealed class WheaErrorService : ObservableObject
{
    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _summary = "尚未讀取。";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public ObservableCollection<WheaRow> Rows { get; } = [];

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
            var entries = await Task.Run(() => Collect(since));
            var (crit, err, warn) = Summarize(entries);
            if (entries.Count == 0)
            {
                Summary = $"近 {days} 天：無 WHEA 事件紀錄（這是好事，照實說）。";
            }
            else
            {
                var parts = new List<string>();
                if (crit > 0) parts.Add($"重大 {crit}");
                if (err > 0) parts.Add($"錯誤 {err}");
                if (warn > 0) parts.Add($"警告 {warn}");
                Summary = $"近 {days} 天：{entries.Count} 筆（{string.Join("・", parts)}）。重大／錯誤事件建議對照事件內文與最近硬體變更。";
            }
            foreach (var e in entries.Take(60))
                Rows.Add(new WheaRow(
                    e.Time.ToString("MM-dd HH:mm:ss"),
                    e.Level switch { 1 => "重大", 2 => "錯誤", 3 => "警告", _ => "資訊" },
                    $"#{e.Id}",
                    e.Message));
        }
        catch (Exception ex)
        {
            Summary = "無法讀取 WHEA 事件紀錄：" + ex.Message + "（此頻道可能未啟用或被政策關閉）";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static List<(DateTime Time, byte Level, int Id, string Message)> Collect(DateTime since)
    {
        var list = new List<(DateTime, byte, int, string)>();
        var query = new EventLogQuery("Microsoft-Windows-WHEA-Logger/Operational", PathType.LogName,
            $"*[System[TimeCreated[timediff(@SystemTime) <= {(long)(DateTime.Now - since).TotalMilliseconds}]]]");
        using var reader = new EventLogReader(query);
        while (list.Count < 500)
        {
            EventRecord? rec;
            try { rec = reader.ReadEvent(); }
            catch (EventLogNotFoundException) { throw new InvalidOperationException("找不到 WHEA-Logger 頻道"); }
            if (rec is null) break;
            using (rec)
            {
                string message;
                try { message = rec.FormatDescription() ?? ""; }
                catch { message = "（事件內文無法格式化：提供者資訊缺失）"; }
                var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines ? lines[0] : "(無內文)";
                list.Add((rec.TimeCreated ?? DateTime.MinValue, rec.Level ?? 0, rec.Id, firstLine));
            }
        }
        return list;
    }

    /// <summary>純函式：依層級計數（重大／錯誤／警告）。</summary>
    public static (int Critical, int Error, int Warning) Summarize(
        IReadOnlyList<(DateTime Time, byte Level, int Id, string Message)> entries)
        => (entries.Count(e => e.Level == 1), entries.Count(e => e.Level == 2), entries.Count(e => e.Level == 3));
}
