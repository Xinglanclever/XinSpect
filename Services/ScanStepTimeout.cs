using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// 掃描步驟的超時上界（ULTIMATE-1000 階段一 P1-002）。
///
/// <para>
/// <b>為什麼「宣告上界」比「實作逾時」重要：</b>逾時本身到處都有，但多數是
/// 「等太久就放棄」的權宜值，沒有人知道那個數字從哪來。這一項要求的是
/// <b>上界先被宣告</b>，逾時只是把宣告執行出來——所以紀錄裡必須留下
/// 「這一輪用的是哪個上界」，否則事後無法判斷某次逾時是設計如此還是環境異常。
/// </para>
///
/// <para>
/// <b>逾時是一筆狀態，不是例外。</b>逾時要落在 <see cref="ScanStepOutcome.Failed"/>
/// 並附上步驟名與上界，讓上層能說出「第 N 步超過 250 毫秒」，而不是丟一個
/// <see cref="TimeoutException"/> 讓人猜是哪一步。
/// </para>
/// </summary>
public sealed record ScanStepTimeout(TimeSpan Limit)
{
    /// <summary>未宣告上界時的預設。刻意給一個寬鬆值，讓「忘了宣告」不會變成隨機紅燈。</summary>
    public static readonly ScanStepTimeout Unspecified = new(TimeSpan.FromSeconds(5));

    public bool IsDeclared => Limit != Unspecified.Limit;

    /// <summary>逾時時要說的話。不含步驟以外的細節——訊息可能被貼到公開討論區。</summary>
    public string Describe(string stepName) =>
        IsDeclared
            ? $"步驟「{stepName}」超過宣告上界 {Limit.TotalMilliseconds:0} 毫秒"
            : $"步驟「{stepName}」超過預設上界 {Limit.TotalMilliseconds:0} 毫秒（未宣告）";

    /// <summary>
    /// 以宣告的上界執行一個步驟。<b>逾時不是取消硬體存取</b>——這裡只是不再等它，
    /// 步驟本身若已完成則結果仍算數（見 <see cref="TimedStep"/>）。
    /// </summary>
    public (T? Value, bool TimedOut, TimeSpan Elapsed) Run<T>(Func<T> body)
    {
        var sw = Stopwatch.StartNew();
        var task = Task.Run(body);
        bool done = task.Wait(Limit);
        sw.Stop();
        return done ? (task.Result, false, sw.Elapsed) : (default, true, sw.Elapsed);
    }
}

/// <summary>把「宣告上界」與「步驟本體」綁在一起，讓逾時判定與紀錄一致。</summary>
public static class TimedStep
{
    /// <summary>
    /// 執行一個步驟並回傳它的紀錄。逾時會變成
    /// <see cref="ScanStepOutcome.Failed"/> 且原因指名步驟與上界。
    ///
    /// <para>
    /// <b>逾時不假裝成功。</b>這是本專案反覆出現的那個病（「回一個看起來成功的值，
    /// 但那次讀取根本沒發生」）的逾時版本：不能因為「等不下去」就把沒讀到的東西
    /// 記成 <see cref="ScanStepOutcome.Completed"/>。
    /// </para>
    /// </summary>
    public static ScanStepResult Run(string name, ScanStepTimeout limit, Func<ScanStepResult> body)
    {
        var (result, timedOut, elapsed) = limit.Run(body);
        if (timedOut)
            return ScanStepResult.Failed(name, elapsed, limit.Describe(name));
        return result ?? ScanStepResult.Failed(name, elapsed, "步驟回傳 null");
    }
}
