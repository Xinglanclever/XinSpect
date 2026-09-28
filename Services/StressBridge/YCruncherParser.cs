namespace XinSpect;

/// <summary>外部壓測工具跑完的判定。</summary>
public enum StressOutcome
{
    /// <summary>跑滿要求時長、乾淨退出、未回報運算錯誤。</summary>
    Passed,
    /// <summary>偵測到運算錯誤（記憶體／CPU 在壓力下算錯），或工具異常中止。</summary>
    ErrorDetected,
    /// <summary>乾淨退出但沒跑滿要求時長——未完成的壓測不作數。</summary>
    Incomplete,
    /// <summary>尚未執行。</summary>
    NotRun,
}

/// <summary>一次外部壓測的結果（帶原始日誌尾巴供人核對，不只給一個結論）。</summary>
public sealed record StressResult(
    StressOutcome Outcome, double RanSeconds, double RequestedSeconds, string Summary, string LogTail);

/// <summary>
/// 解析 y-cruncher 壓測(component stress)的輸出。純函式、零外部相依，故完整可測。
/// </summary>
/// <remarks>
/// <b>主判據是「乾淨退出(碼 0)且跑滿要求時長」＝通過</b>——y-cruncher 偵測到運算錯誤會當場停下，
/// 所以非零退出、或遠早於時限就結束，都不算通過。錯誤字樣掃描只是輔助(本機無 y-cruncher 可對真實輸出，
/// 保守列幾個常見字樣)；最終一律附上原始日誌尾巴，判斷交給人，不憑脆弱的字串比對下死結論。
/// </remarks>
public static class YCruncherParser
{
    // 常見的運算錯誤／異常字樣（保守；輔助用，主判據仍是退出碼＋時長）。
    private static readonly string[] ErrorMarkers =
    [
        "error", "mismatch", "incorrect", "coefficient is too", "validation fail",
        "computation error", "hardware error", "an error occurred",
    ];

    public static StressResult Parse(int exitCode, double ranSeconds, double requestedSeconds, string stdout)
    {
        stdout ??= "";
        string tail = Tail(stdout, 40);
        bool errorMarker = ErrorMarkers.Any(m => stdout.Contains(m, StringComparison.OrdinalIgnoreCase));

        // y-cruncher 偵測到運算錯誤會當場停下：錯誤字樣、或非零退出碼＝有問題。
        if (errorMarker || exitCode != 0)
        {
            string why = errorMarker
                ? "y-cruncher 輸出含錯誤字樣——偵測到運算錯誤（記憶體／CPU 在壓力下算錯）。"
                : $"y-cruncher 以非零碼（{exitCode}）結束，多半是中途出錯或被中止。";
            return new(StressOutcome.ErrorDetected, ranSeconds, requestedSeconds, why + " 詳見下方原始日誌。", tail);
        }

        // 乾淨退出但沒跑滿要求時長（容差取 5 秒或 5%）→ 未完成，不作通過。
        double tolerance = Math.Max(5, requestedSeconds * 0.05);
        if (requestedSeconds > 0 && ranSeconds < requestedSeconds - tolerance)
            return new(StressOutcome.Incomplete, ranSeconds, requestedSeconds,
                $"只跑了 {ranSeconds:0} 秒（目標 {requestedSeconds:0} 秒）就乾淨結束——未完成的壓測不作數。", tail);

        return new(StressOutcome.Passed, ranSeconds, requestedSeconds,
            $"壓測 {ranSeconds:0} 秒完成，y-cruncher 未回報任何運算錯誤。", tail);
    }

    private static string Tail(string s, int lines)
    {
        var all = s.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }
}
