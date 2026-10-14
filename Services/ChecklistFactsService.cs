using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 自訂檢查清單（Vol2 第 NG-002 項）：<c>ng.checklist</c>——讀取使用者提供的清單檔，
/// 驗證它、並陳述它<b>能不能用</b>以及<b>為什麼不能用</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一項最容易做錯的地方是「靜默部分成功」</b>：清單裡十條有兩條寫錯，
/// 如果只回「載入 8 條」，使用者不會知道少了哪兩條、也不會知道少了；
/// 下次打開看到 8 條，會以為自己當初就是寫 8 條。
/// 所以本項的輸出是<b>「有效幾條、無效幾條、無效的每一條錯在哪一行」</b>。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>檔案不存在與解析失敗是不同的兩件事</b>——前者是「還沒建立」，
/// 後者是「建立了一份但有錯」；前者是 <see cref="FactAvailability.NotApplicable"/>，
/// 後者是 <see cref="FactAvailability.ReadError"/> 並帶行號；
/// ② <b>行號從 1 起算</b>，而且計的是<b>檔案裡的行</b>——使用者要拿它回去改檔案，
/// 用陣列索引會讓他改錯行；
/// ③ <b>不執行清單裡任何東西</b>：本項只驗證與陳述，把清單當資料讀；
/// ④ 無法辨識的鍵名<b>不算錯</b>而是列為「未知項」——清單可能要餵給別的版本或別的頁面，
/// 本工具認不得不等於寫錯（否則使用者會被逼著照本工具的鍵表寫，那違背「自訂」的意義）。
/// </para>
/// <para>
/// <b>檔案位置與寫入：</b>清單放 <c>%APPDATA%\XinSpect\checklist.txt</c>。
/// 本項<b>只讀不寫</b>——檔案由使用者自己建立（或由匯出功能產生）；
/// 若將來由本程式產生，一律走 <see cref="WriteGate"/> 留痕（同 settings.json 的政策）。
/// </para>
/// </remarks>
public static class ChecklistFactsService
{
    private const string Cat = "診斷治理";
    private const string Source = @"%APPDATA%\XinSpect\checklist.txt（唯讀）";

    /// <summary>NG-002 自訂檢查清單。</summary>
    public const string ChecklistKey = "ng.checklist";

    /// <summary>清單檔名（相對於設定目錄）。</summary>
    public const string FileName = "checklist.txt";

    /// <summary>單一行的最大長度——超過視為格式錯誤，避免有人貼進一整份報告。</summary>
    public const int MaxLineLength = 200;

    /// <summary>清單項數上限——超過代表這不是清單而是別的東西。</summary>
    public const int MaxItems = 500;

    /// <summary>一個清單項的驗證結果。</summary>
    /// <param name="Line">檔案裡的行號（從 1 起算）。</param>
    /// <param name="Text">項目文字（已去頭尾空白）。</param>
    /// <param name="Key">項目若帶鍵名則填入，否則為 null。</param>
    /// <param name="Kind">這是什麼樣的項目。</param>
    /// <param name="Error">這一項有問題時的原因（null＝沒問題）。</param>
    public sealed record ChecklistItem(int Line, string Text, string? Key, ItemKind Kind, string? Error);

    /// <summary>清單項的種類。</summary>
    public enum ItemKind
    {
        /// <summary>註解（<c>#</c> 開頭）——不計入有效／無效項。</summary>
        Comment,
        /// <summary>空白行。</summary>
        Blank,
        /// <summary>純文字項目（人類看的檢查步驟）。</summary>
        Note,
        /// <summary>帶鍵名的項目（指向某一把事實鍵）。</summary>
        KeyRef,
    }

    /// <summary>整份清單的判讀結果。</summary>
    /// <param name="Valid">有效項（Note＋KeyRef）。</param>
    /// <param name="Invalid">無效項（帶 Error）。</param>
    /// <param name="UnknownKeys">帶了鍵名但本工具認不得的項（不算錯）。</param>
    /// <param name="Comments">註解與空白行數。</param>
    public sealed record ChecklistReport(
        IReadOnlyList<ChecklistItem> Valid,
        IReadOnlyList<ChecklistItem> Invalid,
        IReadOnlyList<ChecklistItem> UnknownKeys,
        int Comments);

    // ── 判讀（純函式，可單獨驗）─────────────────────────────────────────

    /// <summary>
    /// 逐行判讀清單。<b>行號一律從 1 起算並對應檔案實際行數</b>——
    /// 使用者要拿這個號碼回去改檔案。
    /// </summary>
    /// <param name="lines">檔案的每一行（未過濾空白行，順序即檔案順序）。</param>
    /// <param name="knownKeys">本工具認得的事實鍵（用來分辨未知項；null＝不做鍵名檢查）。</param>
    public static ChecklistReport Parse(IReadOnlyList<string> lines, IReadOnlySet<string>? knownKeys = null)
    {
        var valid = new List<ChecklistItem>();
        var invalid = new List<ChecklistItem>();
        var unknown = new List<ChecklistItem>();
        int comments = 0, items = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            int lineNo = i + 1;                     // 檔案行號從 1 起算
            string raw = lines[i] ?? "";
            string text = raw.Trim();

            if (text.Length == 0)
            {
                comments++;
                continue;
            }
            if (text.StartsWith('#'))
            {
                comments++;
                continue;
            }

            items++;
            if (items > MaxItems)
            {
                invalid.Add(new ChecklistItem(lineNo, Truncate(text), null, ItemKind.Note,
                    $"超過 {MaxItems} 項上限——這個檔案看起來不是檢查清單"));
                continue;
            }
            if (text.Length > MaxLineLength)
            {
                invalid.Add(new ChecklistItem(lineNo, Truncate(text), null, ItemKind.Note,
                    $"單行超過 {MaxLineLength} 字元——請拆成多行或改成註解"));
                continue;
            }

            // 帶鍵名的項目寫法：`鍵名` 或 [鍵名] 或 鍵名：說明（鍵名是全小寫兩段式）
            string? key = ExtractKey(text);
            if (key is null)
            {
                valid.Add(new ChecklistItem(lineNo, text, null, ItemKind.Note, null));
                continue;
            }

            var item = new ChecklistItem(lineNo, text, key, ItemKind.KeyRef, null);
            if (knownKeys is null || knownKeys.Contains(key))
                valid.Add(item);
            else
                unknown.Add(item);      // 認不得 ≠ 寫錯：清單可能要餵給別的版本
        }

        return new ChecklistReport(valid, invalid, unknown, comments);
    }

    /// <summary>
    /// 從一行抽出鍵名。認得的寫法有三種（都要求鍵名長成 <c>aa.bbb</c> 的全小寫兩段式）：
    /// <c>`key`</c>（反引號）、<c>[key]</c>（方括號）、<c>key：說明</c>（全形或半形冒號開頭）。
    /// 抽不到回 <c>null</c>——<b>純文字項目是合法的清單形式</b>，不是每一行都得帶鍵名。
    /// </summary>
    public static string? ExtractKey(string text)
    {
        foreach (var (open, close) in new[] { ('`', '`'), ('[', ']') })
        {
            int a = text.IndexOf(open);
            if (a < 0) continue;
            int b = text.IndexOf(close, a + 1);
            if (b <= a + 1) continue;
            string inside = text[(a + 1)..b].Trim();
            if (IsKeyShape(inside)) return inside;
        }

        // 「key：說明」形式：冒號前的整段必須就是鍵名（避免把句子裡的冒號誤判）。
        int colon = text.IndexOfAny(['：', ':']);
        if (colon > 0)
        {
            string head = text[..colon].Trim();
            if (IsKeyShape(head)) return head;
        }
        return null;
    }

    /// <summary>鍵名形狀：兩段以上、全小寫、以點分隔、允許數字與底線。</summary>
    public static bool IsKeyShape(string s)
    {
        if (s.Length < 3 || s.Length > 60) return false;
        if (!s.Contains('.')) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_'))
                return false;
        return !s.StartsWith('.') && !s.EndsWith('.') && !s.Contains("..");
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    // ── 事實 ──────────────────────────────────────────────────────────────

    /// <summary>清單目錄（與 settings.json 同一個；測試可覆寫根目錄）。</summary>
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XinSpect");

    /// <summary>清單檔完整路徑。</summary>
    public static string FilePath => Path.Combine(Directory, FileName);

    /// <summary>收集清單事實。<paramref name="rootOverride"/> 供測試指向暫存目錄。</summary>
    public static HardwareFact Collect(DateTimeOffset at,
        IReadOnlySet<string>? knownKeys = null, string? rootOverride = null)
    {
        string path = rootOverride is null
            ? FilePath
            : Path.Combine(rootOverride, FileName);

        IReadOnlyList<string> lines;
        try
        {
            if (!File.Exists(path))
                return Unavailable(at, FactAvailability.NotApplicable,
                    $"尚未建立自訂檢查清單（{path}）——不適用，不是錯誤。" +
                    "清單是可選的使用者資產：沒有它就等於沒有自訂檢查步驟，" +
                    "內建檢查不受影響");
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            return Unavailable(at, FactAvailability.ReadError,
                "清單檔讀取失敗：" + ex.GetType().Name + "——讀不到就不假裝沒有這份清單");
        }

        var r = Parse(lines, knownKeys);

        // 空檔案與「只有註解」都是有效狀態，但要在值裡說清楚。
        if (r.Valid.Count == 0 && r.Invalid.Count == 0)
            return new HardwareFact(ChecklistKey, Cat, "自訂檢查清單",
                $"清單檔存在（{lines.Count} 行）但沒有可用的項目——只有註解與空白行。" +
                "這是有效狀態不是錯誤：使用者可能剛建立檔案還沒填內容。",
                "項", Source, FactTrustLevel.Reported, false, at, 0);

        var invalidText = r.Invalid.Count == 0
            ? "沒有無效項"
            : $"<b>{r.Invalid.Count} 項無效</b>：" + string.Join("；", r.Invalid.Take(5)
                .Select(x => $"第 {x.Line} 行 {x.Error}"))
              + (r.Invalid.Count > 5 ? $"；等 {r.Invalid.Count - 5} 項" : "");

        var unknownText = r.UnknownKeys.Count == 0
            ? ""
            : $"・其中 {r.UnknownKeys.Count} 項帶了本工具認不得的鍵名（"
              + string.Join("、", r.UnknownKeys.Take(5).Select(x => x.Key))
              + (r.UnknownKeys.Count > 5 ? " 等" : "")
              + "）——<b>認不得不等於寫錯</b>：清單可能要餵給別的版本或用來記錄待辦，"
              + "本項不因為鍵名不認得就把那一項判成無效";

        return new HardwareFact(ChecklistKey, Cat, "自訂檢查清單",
            $"清單 {lines.Count} 行：有效 {r.Valid.Count} 項、{invalidText}、" +
            $"註解與空白 {r.Comments} 行{unknownText}。" +
            "<b>無效項逐項帶行號</b>（從 1 起算，對應檔案實際行數）——" +
            "使用者要拿那個號碼回去改檔案，給陣列索引會讓他改錯行。" +
            "<b>不做靜默部分成功</b>：十條有兩條寫錯就明說少了哪兩條，" +
            "只回「載入 8 條」會讓使用者下次打開時以為自己當初就寫 8 條。" +
            "<b>本項只驗證與陳述，不執行清單裡任何東西</b>；" +
            "清單檔由使用者自己建立，本項唯讀不寫（若本程式產生，一律走 WriteGate 留痕）。",
            "項", Source, FactTrustLevel.Reported, false, at, r.Valid.Count);
    }

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(ChecklistKey, Cat, "自訂檢查清單", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
