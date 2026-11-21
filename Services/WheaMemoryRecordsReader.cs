using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace XinSpect;

public enum WheaMemoryReadStatus { Complete, Truncated, TimedOut, InsufficientPrivilege, ReadError }

/// <summary>Enumeration completeness is independent of CPER section availability.</summary>
public sealed record WheaMemoryReadResult(
    IReadOnlyList<WheaMemoryRecord> Records, WheaMemoryReadStatus Status,
    DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc, string? Reason) : IReadOnlyList<WheaMemoryRecord>
{
    public int Count => Records.Count;
    public WheaMemoryRecord this[int index] => Records[index];
    public IEnumerator<WheaMemoryRecord> GetEnumerator() => Records.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Minimal event projection; a missing timestamp must never become the current time.</summary>
public sealed record WheaMemoryEvent(int EventId, DateTimeOffset? RecordedAt)
{
    public WheaEventEnvelope? Envelope { get; init; }
}

/// <summary>
/// 從事件記錄取 WHEA 記憶體錯誤記錄（U10-WH-0003／WH-0004 的生產端）。
/// </summary>
/// <remarks>Named RawData is accepted only for installed manifest ID/version contracts; CPER headers and every descriptor are bounded and validated before memory slices are decoded.</remarks>
public static class WheaMemoryRecordsReader
{
    /// <summary>時間窗：與 <see cref="EventIdEvidenceFactsService.WindowDays"/> 同一個窗。</summary>
    public const int WindowDays = EventIdEvidenceFactsService.WindowDays;

    /// <summary>讀取上限——不讓吵雜的供應者拖垮收集。</summary>
    public const int ReadCap = 64;
    public const int ScanCap = 4096;
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan EventReadTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 本機已核 manifest 的 RawData 候選：1／17／18／19／23／46／47。
    /// 查詢不以版本濾掉未知事件；版本取用契約由外層解析器明確拒解。
    /// <b>本版不靠事件 ID 判斷錯的是不是記憶體</b>——那要有節區才說得準，
    /// 而 ID 只是「這一筆值得看」的入口。
    /// </summary>
    public static readonly int[] CandidateEventIds = [1, 17, 18, 19, 23, 46, 47];

    /// <summary>節區讀不到時的理由（每一筆都一樣，所以寫成常數，避免十六份不同的說法）。</summary>
    public const string BlobNotParsed = "未提供可驗證的事件 RawData 外層";

    public static WheaMemoryReadResult ReadForThisMachine() => ReadResult();

    /// <summary>
    /// 列舉時間窗內的候選事件。<paramref name="probe"/> 可注入（測試用），
    /// Legacy injected records remain supported; production returns a completeness-bearing list.
    /// </summary>
    public static IReadOnlyList<WheaMemoryRecord> Read(
        Func<IReadOnlyList<WheaMemoryRecord>>? probe)
    {
        if (probe is not null) return probe();
        return ReadResult();
    }

    /// <summary>Creates a query without opening the log. Time and ID filtering precede the result cap.</summary>
    internal const string LogPath = "System";

    internal static string BuildXPath(DateTimeOffset at)
    {
        string start = at.AddDays(-WindowDays).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        string end = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        string ids = string.Join(" or ", CandidateEventIds.Select(id => "EventID=" + id.ToString(CultureInfo.InvariantCulture)));
        return $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and ({ids}) and TimeCreated[@SystemTime >= '{start}' and @SystemTime <= '{end}']]]";
    }

    public static EventLogQuery CreateQuery(DateTimeOffset at)
        => new(LogPath, PathType.LogName, BuildXPath(at))
        {
            // .NET 10 WindowsDesktop reference XML: EventLogQuery.ReverseDirection=true is newest first.
            ReverseDirection = true,
            TolerateQueryErrors = false,
        };

    public static WheaMemoryReadResult ReadResult(Func<IEnumerable<WheaMemoryEvent>>? probe = null,
        DateTimeOffset? at = null, Func<TimeSpan>? elapsed = null)
    {
        var end = (at ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = end.AddDays(-WindowDays);
        var timer = Stopwatch.StartNew();
        elapsed ??= () => timer.Elapsed;
        var list = new List<WheaMemoryRecord>();
        WheaMemoryReadResult Finish(WheaMemoryReadStatus status, string? reason = null) =>
            new(list.ToArray(), status, start, end, reason);
        try
        {
            using var events = (probe is null ? ReadEvents(end) : probe()).GetEnumerator();
            int scanned = 0;
            while (true)
            {
                if (elapsed() >= ReadBudget)
                    return Finish(WheaMemoryReadStatus.TimedOut, "WHEA event enumeration exceeded its time budget; retained records are partial.");
                bool hasNext = events.MoveNext();
                if (elapsed() >= ReadBudget)
                    return Finish(WheaMemoryReadStatus.TimedOut, "WHEA event enumeration completed after its deadline; retained records are partial.");
                if (!hasNext) return Finish(WheaMemoryReadStatus.Complete);
                if (++scanned > ScanCap)
                    return Finish(WheaMemoryReadStatus.Truncated, "WHEA event scan limit reached; retained records are a lower bound.");
                var record = events.Current;
                if (Array.IndexOf(CandidateEventIds, record.EventId) < 0) continue;
                if (record.RecordedAt is not { } when)
                    return Finish(WheaMemoryReadStatus.ReadError, "A candidate WHEA event has no timestamp; window membership cannot be established.");
                // Do not infer chronological ordering from timestamps: clocks can move backwards.
                if (when < start || when > end) continue;
                if (list.Count == ReadCap)
                    return Finish(WheaMemoryReadStatus.Truncated, "More candidate WHEA events exist in the window; retained records are a lower bound.");
                list.Add(new WheaMemoryRecord(record.EventId, when, null,
                    record.Envelope?.Reason ?? (record.Envelope?.Valid == true
                        ? "已驗證 CPER；記憶體節區依描述符逐筆處理" : BlobNotParsed))
                { Envelope = record.Envelope });
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Finish(WheaMemoryReadStatus.InsufficientPrivilege, "Access to the WHEA event log was denied; retained records are partial.");
        }
        catch (EventLogException ex) when (unchecked((uint)ex.HResult) == 0x800705B4)
        {
            return Finish(WheaMemoryReadStatus.TimedOut, "The native WHEA event read timed out; retained records are partial.");
        }
        catch (TimeoutException)
        {
            return Finish(WheaMemoryReadStatus.TimedOut, "Reading a WHEA event timed out; retained records are partial.");
        }
        catch (System.Security.SecurityException)
        {
            return Finish(WheaMemoryReadStatus.InsufficientPrivilege, "Access to the WHEA event log was denied; retained records are partial.");
        }
        catch (Exception ex)
        {
            return Finish(WheaMemoryReadStatus.ReadError, "WHEA event enumeration failed (" + ex.GetType().Name + "); retained records are partial.");
        }
    }

    private static IEnumerable<WheaMemoryEvent> ReadEvents(DateTimeOffset at)
    {
        using var reader = new EventLogReader(CreateQuery(at));
        while (true)
        {
            // Reference XML documents this timeout as canceling the read operation, not a caller-only wait.
            using var record = reader.ReadEvent(EventReadTimeout);
            if (record is null) yield break;
            yield return new WheaMemoryEvent(record.Id,
                record.TimeCreated is { } time ? new DateTimeOffset(time) : null)
            { Envelope = WheaEventEnvelopeParser.Parse(record.ToXml(), record.Id) };
        }
    }
}
