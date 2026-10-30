using System.Diagnostics.Eventing.Reader;

namespace XinSpect;

/// <summary>
/// 從事件記錄取 WHEA 記憶體錯誤記錄（U10-WH-0003／WH-0004 的生產端）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一支刻意只做「列出事件」，不做「拆 blob」。</b>
/// WHEA-Logger 的 CPER 記錄放在事件的 data 段，取出來是二進位 blob。要逐欄解，
/// 除了 CPER 節區本身的佈局（那份有 cper.h 可核，已寫在
/// <see cref="WheaMemorySectionDecoder"/>），還要先知道 <b>事件 data 段怎麼把
/// WHEA_ERROR_RECORD 包起來</b>——那一層本版沒有可核的出處（SDK 標頭只宣告結構，
/// 不宣告事件裝載格式）。硬拆的結果是「看起來很專業的錯誤」。
/// </para>
/// <para>
/// 所以：<b>事件數如實列出、節區一律標讀不到並說明缺什麼</b>。
/// 讀者得到的是「這台機器回報過 N 筆 WHEA 記憶體事件，而本版解不開內容」——
/// 這句話比一個猜出來的「第 0 條 DIMM 單一位元錯誤」有用得多。
/// </para>
/// </remarks>
public static class WheaMemoryRecordsReader
{
    /// <summary>時間窗：與 <see cref="EventIdEvidenceFactsService.WindowDays"/> 同一個窗。</summary>
    public const int WindowDays = EventIdEvidenceFactsService.WindowDays;

    /// <summary>讀取上限——不讓吵雜的供應者拖垮收集。</summary>
    public const int ReadCap = 64;

    /// <summary>
    /// 這些事件 ID 都可能帶記憶體節區。17／19／23／46／47 全部收：
    /// <b>本版不靠事件 ID 判斷錯的是不是記憶體</b>——那要有節區才說得準，
    /// 而 ID 只是「這一筆值得看」的入口。
    /// </summary>
    public static readonly int[] CandidateEventIds = [17, 19, 23, 46, 47];

    /// <summary>節區讀不到時的理由（每一筆都一樣，所以寫成常數，避免十六份不同的說法）。</summary>
    public const string BlobNotParsed =
        "事件的 data 段裝載格式（WHEA_ERROR_RECORD 外層）本版沒有可核的出處，"
        + "因此**不去拆 blob**——記憶體節區的欄位佈局有 cper.h 可核，但外層包裝沒有";

    public static IReadOnlyList<WheaMemoryRecord> ReadForThisMachine() => Read(null);

    /// <summary>
    /// 列舉時間窗內的候選事件。<paramref name="probe"/> 可注入（測試用），
    /// 回傳 <c>(EventId, RecordedAt, Section)</c> 清單即代表整條路徑可用。
    /// </summary>
    public static IReadOnlyList<WheaMemoryRecord> Read(
        Func<IReadOnlyList<WheaMemoryRecord>>? probe)
    {
        if (probe is not null) return probe();
        return ReadFromEventLog();
    }

    private static IReadOnlyList<WheaMemoryRecord> ReadFromEventLog()
    {
        List<WheaMemoryRecord> list = [];
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]");
            using var reader = new EventLogReader(query);
            var since = DateTimeOffset.UtcNow.AddDays(-WindowDays);
            for (int i = 0; i < ReadCap; i++)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                DateTimeOffset when;
                try { when = new DateTimeOffset(record.TimeCreated ?? DateTime.Now); }
                catch { when = DateTimeOffset.UtcNow; }
                if (when < since) break; // 事件記錄是時間遞減，超過窗就停
                int id = record.Id;
                if (Array.IndexOf(CandidateEventIds, id) < 0) continue;
                // 節區一律 null：理由見 BlobNotParsed。**不猜 blob 佈局。**
                list.Add(new WheaMemoryRecord(id, when, null, BlobNotParsed));
            }
        }
        catch
        {
            // 事件記錄不可用（權限、記錄檔損毀）——回空清單。
            // 呼叫端看到的是「0 筆」，而 WheaMemoryFactsService 的 summary 會寫明
            // 「沒有記錄」與「沒有記憶體錯誤」是兩件事。
        }
        return list;
    }
}
