namespace XinSpect;

/// <summary>一次重試決定。<paramref name="Reason"/> 會直接顯示給使用者（重試時一定要說出原因與次數）。</summary>
public readonly record struct RetryDecision(bool Retry, TimeSpan Delay, string Reason);

/// <summary>
/// 暫時性失敗的重試政策：只重試「等一下可能就會成功」的狀態碼（429 額度、502/503/504 閘道與過載、408 逾時），
/// 401/403/404 這類重試一百次也不會好的錯誤一律立刻回報——那才是有用的錯誤訊息。
///
/// <para>
/// 重試只在<b>還沒開始串流任何內容</b>時發生：那時畫面還是占位字，重試不會產生重複文字。
/// 已經吐了一半的回答再重試，使用者會看到同一段話出現兩次，而且不知道哪一次才是完整的。
/// </para>
/// </summary>
public static class AiRetryPolicy
{
    /// <summary>總嘗試次數（首次＋重試）。三次之後就照實回報錯誤，不要讓使用者枯等。</summary>
    public const int MaxAttempts = 3;

    /// <summary>兩次重試之間最多等這麼久；端點要求更久就直接失敗，不要把人卡在「正在重試」。</summary>
    public const int MaxDelaySeconds = 20;

    /// <summary>沒給 Retry-After 時的基礎等待秒數（第 n 次重試等 n 倍）。</summary>
    public const int BaseDelaySeconds = 2;

    public static bool IsTransient(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>
    /// 決定是否重試。<paramref name="attempt"/> 從 1 起算（1＝首次嘗試剛剛失敗）。
    /// <paramref name="retryAfter"/> 取自端點的 <c>Retry-After</c>，超過 <see cref="MaxDelaySeconds"/> 時放棄重試。
    /// </summary>
    public static RetryDecision Decide(int statusCode, int attempt, TimeSpan? retryAfter = null)
    {
        if (!IsTransient(statusCode))
            return new RetryDecision(false, TimeSpan.Zero, $"HTTP {statusCode} 不是暫時性錯誤，重試也不會改變結果");
        if (attempt >= MaxAttempts)
            return new RetryDecision(false, TimeSpan.Zero, $"HTTP {statusCode} 已連續失敗 {attempt} 次");

        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            if (ra > TimeSpan.FromSeconds(MaxDelaySeconds))
                return new RetryDecision(false, TimeSpan.Zero,
                    $"端點要求 {Math.Ceiling(ra.TotalSeconds):F0} 秒後再試（超過上限 {MaxDelaySeconds} 秒）");
            return new RetryDecision(true, ra,
                $"HTTP {statusCode}：端點要求 {Math.Ceiling(ra.TotalSeconds):F0} 秒後再試");
        }

        int seconds = Math.Min(MaxDelaySeconds, BaseDelaySeconds * Math.Max(1, attempt));
        return new RetryDecision(true, TimeSpan.FromSeconds(seconds), $"HTTP {statusCode}（暫時性錯誤）");
    }

    /// <summary>解析 <c>Retry-After</c>：支援「秒數」與 HTTP 日期兩種格式；看不懂就回 null（改用預設退避）。</summary>
    public static TimeSpan? ParseRetryAfter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (int.TryParse(value, out int secs) && secs >= 0)
            return TimeSpan.FromSeconds(secs);
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
        {
            var delta = when - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }
}
