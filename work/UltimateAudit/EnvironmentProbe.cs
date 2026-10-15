using System.Text.RegularExpressions;

namespace UltimateAudit;

/// <summary>
/// 環境探針：把「這台機器上可用的比對機制正不正常」記錄下來。
///
/// <para>
/// 它<b>不</b>參與稽核判定，也<b>不</b>能讓稽核失敗——它的用途只有一個：
/// 當有人（包括未來的我）拿另一套工具得出不同結論時，這份紀錄能立刻回答
/// 「是文件不同，還是工具不同」。
/// </para>
///
/// <para>
/// 為什麼要有它：2026-10-16 有一輪稽核用 Python 的 <c>re</c> 解析這批文件，
/// 四版四個答案。追查後發現該直譯器對「字元類別＋量詞」的比對結果不穩
/// （<c>[A-Z]+</c> 對 <c>"A1"</c> 回 false、對 <c>"ABC"</c> 回 true，可重現）。
/// 這份探針就是那個發現的可重跑版本，只是換到 .NET 上量。
/// </para>
/// </summary>
internal static class EnvironmentProbe
{
    public static void Run()
    {
        (string What, bool Got, bool Expected)[] cases =
        [
            ("Regex [A-Z]+ 對 ABC（應 true）", Regex.IsMatch("ABC", "^[A-Z]+$"), true),
            ("Regex [A-Z]+ 對 A1（應 false）", Regex.IsMatch("A1", "^[A-Z]+$"), false),
            ("Regex [A-Z]+\\d? 對 F10（應 true）", Regex.IsMatch("F10", "^[A-Z]+\\d?$"), true),
            ("Regex P\\d{1,2}-\\d{3} 對 P1-001（應 true）", Regex.IsMatch("P1-001", "^P\\d{1,2}-\\d{3}$"), true),
            ("Regex P\\d{1,2}-\\d{3} 對 P10-001（應 true）", Regex.IsMatch("P10-001", "^P\\d{1,2}-\\d{3}$"), true),
            ("字面比對 F10 前綴長度（應 true）", "F10".TakeWhile(char.IsAsciiLetter).Count() == 1, true),
        ];

        int bad = 0;
        foreach (var (what, got, expected) in cases)
        {
            bool ok = got == expected;
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {what}（實得 {got}）");
        }
        Console.WriteLine(bad == 0
            ? "  本機的 .NET 正則引擎行為正常；本程式的稽核結論不受環境影響。"
            : $"  !! 本機 .NET 正則引擎有 {bad} 項不符預期——本程式的稽核結論請以字面比對的檢查為準。");
    }
}
