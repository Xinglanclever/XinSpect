using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>SRUM 資料庫的結構統計（不含任何一筆實際紀錄）。</summary>
/// <param name="DatabasePresent">這個機器上有沒有 SRUDB.dat。</param>
/// <param name="Tables">看得出來的資料表數。</param>
/// <param name="Rows">看得出來的資料列總數（粗估，見 <see cref="SrumReadbackService"/> 的說明）。</param>
/// <param name="OldestDay">最舊一筆的日期（只有日期，不含時間）；讀不到時是 <c>null</c>。</param>
/// <param name="NewestDay">最新一筆的日期；讀不到時是 <c>null</c>。</param>
public sealed record SrumDatabase(
    bool DatabasePresent, int Tables, long Rows, DateOnly? OldestDay, DateOnly? NewestDay);

/// <summary>
/// SRUM 讀回（Vol 2 FS-004）：系統資源使用量監視資料庫的<b>結構與覆蓋範圍</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一項刻意只交付「骨架」，不交付「內容」。</b>SRUDB.dat 是一份 ESE（Extensible Storage
/// Engine）資料庫，裡面逐筆記著每個行程用了多少 CPU、多少網路位元組、多少磁碟 I/O，
/// 以每小時為單位保留約 30–60 天。它是鑑識上很有價值的一件——而正因為如此，
/// <b>把它逐筆列出來等於把使用者的完整活動輪廓攤開</b>：什麼時候用哪個程式、用了多久、
/// 傳了多少資料。本項因此只回答四個問題：<b>這台機器有沒有這個資料庫、有幾張表、
/// 大約幾列、涵蓋哪一段日期</b>。日期只到「日」不到「時」。
/// </para>
/// <para>
/// <b>誠實界線一：讀不到 ESE 就不假裝讀到了。</b>解析 ESE 需要遵循它的頁面與 B 樹格式，
/// 而且版本之間有差異；<b>沒對準出處就絕不出值</b>——本專案在 NVMe 健康紀錄上吃過
/// 「每一項都讀得到、看起來也都像合理的數字，只是全都不是它標的那一項」的虧
/// （見 v2.84 的紀錄頁位移修復）。所以 <see cref="SrumDatabase"/> 由探針提供，
/// 未注入探針＝不適用，不是「0 張表」。
/// </para>
/// <para>
/// <b>誠實界線二：列數是粗估。</b>列數若由資料表大小除以平均列長推算，就是估的；
/// 值裡一定寫明估算方式，不讓讀者以為那是精確計數。
/// </para>
/// <para>
/// <b>誠實界線三：不列行程、不列帳號、不列資料量。</b>那些正是這個資料庫最敏感的欄位。
/// 本項的輸出裡沒有任何一個逐筆欄位，所以不需要遮蔽——<b>它從一開始就沒有讀</b>。
/// </para>
/// </remarks>
public static class SrumReadbackService
{
    public const string Category = "檔案鑑識";

    /// <summary>有沒有這個資料庫、多大。</summary>
    public const string CountKey = "fs.srum.count";
    /// <summary>涵蓋範圍與結構。</summary>
    public const string RangeKey = "fs.srum.range";

    /// <summary>
    /// 由資料庫統計算出事實。<paramref name="db"/> 為 <c>null</c>＝未注入探針（沒讀）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, SrumDatabase? db = null)
    {
        if (db is null)
            return
            [
                Unavailable(at, CountKey, "SRUM 資料庫", "未注入 SRUM 讀取——未讀不等於沒有這個資料庫；"
                    + "ESE 解析未對準出處前不出值（同 NVMe 健康紀錄位移那份教訓）"),
                Unavailable(at, RangeKey, "SRUM 涵蓋範圍", "未注入 SRUM 讀取——未讀不等於沒有這個資料庫"),
            ];

        if (!db.DatabasePresent)
            return
            [
                new HardwareFact(CountKey, Category, "SRUM 資料庫",
                    "這台機器上沒有 SRUM 資料庫（SRUDB.dat 不存在）——系統資源使用量監視未啟用，"
                    + "不是讀取失敗，也不是「0 筆紀錄」。<b>沒有這份資料＝沒有這份資料</b>，"
                    + "不從缺檔案推論使用情況。",
                    "個", @"%WINDIR%\System32\sru\SRUDB.dat（唯讀，不列完整路徑）",
                    FactTrustLevel.Reported, false, at, 0),
                new HardwareFact(RangeKey, Category, "SRUM 涵蓋範圍",
                    "沒有資料庫，因此沒有涵蓋範圍可言——不以「0 天」冒充。",
                    "天", "無（資料庫不存在）", FactTrustLevel.Reported, false, at, null),
            ];

        string span = db.OldestDay is { } o && db.NewestDay is { } n
            ? $"涵蓋 {o:yyyy-MM-dd} 到 {n:yyyy-MM-dd}（共 {n.DayNumber - o.DayNumber + 1} 天）"
            : "涵蓋範圍讀不到——如實留白，不以 0 天冒充";

        var facts = new List<HardwareFact>
        {
            new(CountKey, Category, "SRUM 資料庫",
                $"SRUM 資料庫存在：{db.Tables} 張表、約 {db.Rows:N0} 列。"
                + "<b>列數是粗估</b>：由資料表大小與平均列長推算，不是精確計數——"
                + "ESE 逐列走訪的成本遠高於本項的用途。"
                + "<b>本項只讀骨架不讀內容</b>：這個資料庫逐筆記著每個行程用了多少 CPU、"
                + "多少網路位元組、多少磁碟 I/O，把它逐筆列出來等於把使用者的完整活動輪廓攤開。"
                + "因此本項<b>不列行程、不列帳號、不列資料量</b>——這不是遮蔽：本項的輸出裡"
                + "沒有任何一個逐筆欄位，<b>它從一開始就沒有讀</b>。",
                "列", @"%WINDIR%\System32\sru\SRUDB.dat（唯讀，ESE 結構統計）",
                FactTrustLevel.Reported, false, at, db.Rows),
            new(RangeKey, Category, "SRUM 涵蓋範圍", span
                + "。<b>日期只到「日」不到「時」</b>：時級的時間戳會把「什麼時候在用電腦」"
                + "講得比「哪幾天在用」清楚得多，那不是本項要交付的東西。"
                + "資料庫保留期由系統決定（通常 30–60 天），覆蓋範圍短不代表機器沒在用，"
                + "只代表保留期到了。",
                "天", @"%WINDIR%\System32\sru\SRUDB.dat（日期範圍）",
                FactTrustLevel.Reported, false, at,
                db.OldestDay is { } od && db.NewestDay is { } nd ? nd.DayNumber - od.DayNumber + 1 : null),
        };
        return facts;
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name, string reason)
        => new(key, Category, name, "", "", "SRUM（未讀取）",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable, reason);
}
