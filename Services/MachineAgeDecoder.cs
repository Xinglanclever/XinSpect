namespace XinSpect;

/// <summary>一顆磁碟的通電時數（S.M.A.R.T. 回報）。</summary>
/// <param name="Model">磁碟型號。</param>
/// <param name="Hours">累計通電小時；0 代表沒讀到。</param>
public sealed record DiskAge(string Model, long Hours);

/// <summary>推估機器年齡用得到的三個線索。</summary>
/// <remarks>
/// 三個線索各有各的偏誤，所以這裡只收原始值，不在收集階段就下結論。
/// </remarks>
public sealed record MachineAgeFacts
{
    /// <summary>Windows 安裝日期；讀不到為 null。</summary>
    public DateTime? WindowsInstall { get; init; }

    /// <summary>BIOS／UEFI 韌體的建置日期；讀不到為 null。</summary>
    public DateTime? BiosDate { get; init; }

    /// <summary>各磁碟的通電時數。</summary>
    public IReadOnlyList<DiskAge> Disks { get; init; } = [];

    /// <summary>「現在」——測試要能固定它，所以不在函式裡直接取系統時間。</summary>
    public DateTime Now { get; init; } = DateTime.Now;
}

/// <summary>機器年齡的推估結果。</summary>
public sealed class MachineAgeVerdict
{
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required Severity Severity { get; init; }
}

/// <summary>
/// 「這台機器多老了」的推估（純函式）。
/// </summary>
/// <remarks>
/// <b>這一頁給的是三個各有偏誤的線索，不是一個答案</b>——沒有任何暫存器記著出廠日期或購買日期，
/// 所以誠實的做法是把每個線索的意義與偏誤方向講清楚，讓使用者自己合起來看：
/// <list type="bullet">
/// <item><b>Windows 安裝日期</b>：只說明「這套系統裝了多久」。重裝或大版本升級會重設它，
/// 所以它是機器年齡的<b>下界</b>——機器不可能比它上面的系統還年輕。</item>
/// <item><b>BIOS／UEFI 日期</b>：韌體的<b>建置</b>日期，不是出廠日、更不是購買日。從沒刷過韌體時
/// 它會落在出廠前幾週到幾個月，因此推估值會<b>略高於</b>實際；刷過韌體的機器則會低估。</item>
/// <item><b>磁碟通電時數</b>：那顆碟實際通電多久，是使用強度最實在的證據。但換過碟就只代表新碟的年齡，
/// 不代表整機。</item>
/// </list>
/// </remarks>
public static class MachineAgeDecoder
{
    /// <summary>單顆磁碟累計通電超過這麼多小時就值得留意（約 3.4 年的 24 小時運轉）。</summary>
    public const long WornHours = 30_000;

    /// <summary>時間長度 → 「N 年 M 個月」。不足一個月就說天數，不要寫成「0 年 0 個月」。</summary>
    public static string Duration(TimeSpan span)
    {
        int days = (int)Math.Floor(span.TotalDays);
        if (days < 0) return "—";
        if (days < 31) return $"{days} 天";

        int months = (int)Math.Floor(days / 30.44);
        int years = months / 12, rest = months % 12;
        if (years == 0) return $"{rest} 個月";
        return rest == 0 ? $"{years} 年" : $"{years} 年 {rest} 個月";
    }

    /// <summary>通電小時 → 文字，並換算成「若全天候運轉相當於多久」讓人有感。</summary>
    public static string HoursText(long hours)
    {
        if (hours <= 0) return "讀不到";
        double years = hours / 24.0 / 365.25;
        return years >= 1
            ? $"{hours:N0} 小時（全天候運轉約 {years:0.#} 年）"
            : $"{hours:N0} 小時（約 {hours / 24.0:0} 天的通電時間）";
    }

    public static MachineAgeVerdict Judge(MachineAgeFacts f)
    {
        var clues = new List<string>();
        TimeSpan? floor = null;   // 機器至少這麼老

        if (f.WindowsInstall is { } install && install <= f.Now)
        {
            var age = f.Now - install;
            floor = age;
            clues.Add($"這套 Windows 是 {install:yyyy-MM-dd} 裝的，已經 {Duration(age)}"
                    + "——重裝或大版本升級會把這個日期重設，所以它只是機器年齡的下界。");
        }
        else clues.Add("讀不到 Windows 安裝日期。");

        if (f.BiosDate is { } bios && bios <= f.Now)
        {
            var age = f.Now - bios;
            if (floor is null || age > floor) floor = age;
            clues.Add($"韌體的建置日期是 {bios:yyyy-MM-dd}，距今 {Duration(age)}"
                    + "——那是韌體被建出來的時間，不是出廠日也不是購買日。從沒刷過韌體的機器"
                    + "這個值會略高於實際年齡；刷過的則會低估。");
        }
        else clues.Add("讀不到 BIOS／UEFI 的建置日期。");

        var withHours = f.Disks.Where(d => d.Hours > 0).OrderByDescending(d => d.Hours).ToList();
        if (withHours.Count > 0)
        {
            var oldest = withHours[0];
            clues.Add($"通電最久的磁碟是 {oldest.Model}：{HoursText(oldest.Hours)}"
                    + "——這是使用強度最實在的證據，但換過碟的話就只代表那顆碟的年齡。");
        }
        else clues.Add("磁碟通電時數還沒讀（需要按下按鈕，且需要系統管理員身分）。");

        if (floor is null)
            return new MachineAgeVerdict
            {
                Headline = "推不出來",
                Severity = Severity.Neutral,
                Detail = "三個線索一個都讀不到，因此不給任何數字。" + string.Join(" ", clues),
            };

        bool worn = withHours.Count > 0 && withHours[0].Hours >= WornHours;
        return new MachineAgeVerdict
        {
            Headline = $"這台機器至少 {Duration(floor.Value)}",
            Severity = worn ? Severity.Warning : Severity.Neutral,
            Detail = "沒有任何暫存器記著出廠日或購買日，所以這是由三個各有偏誤的線索合起來看的推估，"
                   + "不是查到的事實。" + string.Join(" ", clues)
                   + (worn ? $" 通電時數已超過 {WornHours:N0} 小時，備份習慣可以再確認一次。" : ""),
        };
    }
}
