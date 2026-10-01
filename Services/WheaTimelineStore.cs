using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

public sealed record WheaTimelineEvent(DateTime Time, byte Level, int Id, string Message);

public interface IWheaEventStore
{
    IReadOnlyList<WheaTimelineEvent> ReadSince(DateTime sinceLocal);
}

/// <summary>
/// 直接讀 WHEA-Logger 頻道；只提供原始事件時間軸，不把讀不到的資料補成 0。
/// </summary>
public sealed class WheaTimelineStore : IWheaEventStore
{
    public IReadOnlyList<WheaTimelineEvent> ReadSince(DateTime sinceLocal)
    {
        long elapsedMs = Math.Max(0, (long)(DateTime.Now - sinceLocal).TotalMilliseconds);
        var query = new EventLogQuery(
            "Microsoft-Windows-WHEA-Logger/Operational",
            PathType.LogName,
            $"*[System[TimeCreated[timediff(@SystemTime) <= {elapsedMs}]]]");
        var events = new List<WheaTimelineEvent>();
        using var reader = new EventLogReader(query);
        while (events.Count < 500)
        {
            EventRecord? record;
            try { record = reader.ReadEvent(); }
            catch (EventLogNotFoundException)
            {
                throw new InvalidOperationException("找不到 WHEA-Logger 頻道。");
            }
            if (record is null) break;

            using (record)
            {
                string message;
                try { message = record.FormatDescription() ?? string.Empty; }
                catch { message = "（事件內文無法格式化：提供者資訊缺失）"; }
                string firstLine = message.Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines
                    ? lines[0]
                    : "（無內文）";
                events.Add(new WheaTimelineEvent(
                    record.TimeCreated ?? DateTime.MinValue,
                    record.Level ?? 0,
                    record.Id,
                    firstLine));
            }
        }

        return events;
    }
}
