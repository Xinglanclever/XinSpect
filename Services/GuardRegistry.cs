using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 守門清冊（ULTIMATE-1000 階段一 P1-055 – P1-063 的落地）。
///
/// <para>
/// <b>為什麼要有這份清冊而不是讓每條守門各自宣告：</b>
/// 宣告散在 80 多個測試檔裡，就沒有人能回答「我們的守門整體而言守得住什麼」。
/// 清冊讓那個問題有一個地方可以回答，也讓「新守門忘了宣告」變成<b>可以檢查</b>的事
/// ——後者才是關鍵：規格說「新守門必宣告」，而沒有清冊就無從檢查誰沒宣告。
/// </para>
///
/// <para>
/// <b>本檔只登記「方法論性質」的守門</b>（掃描類、政策類、對帳類），
/// 不登記一般的行為測試——那些的規格是「測的行為對不對」，不是守門方法論。
/// </para>
/// </summary>
public static class GuardRegistry
{
    /// <summary>已登記的守門。<b>新增掃描類／政策類守門時必須登記，否則紅燈。</b></summary>
    public static IReadOnlyList<GuardPolicy.Guard> Guards { get; } =
    [
        new("接線守門", GuardPolicy.GuardMode.SourceScan,
            "服務寫好了、單元測試也綠，但沒有任何生產碼呼叫它——事實鍵登記在目錄裡卻沒人生產，"
            + "覆蓋申報於是把從未存在的事實算成已考慮",
            new GuardPolicy.GuardLevel(
                "生產碼完全沒有引用某支事實服務",
                "『引用了但引用路徑本身也到不了使用者面前』——那需要目錄／註冊／申報三方對帳"),
            HasPositiveControl: true),

        new("目錄與原始碼逐鍵相等", GuardPolicy.GuardMode.Reconciliation,
            "新增事實服務後忘了更新 FactKeyCatalog，於是目錄宣稱的鍵集與實際生產的鍵集漂移",
            new GuardPolicy.GuardLevel(
                "原始碼掃得到的鍵與目錄不一致",
                "掃不到的情形——發佈版沒有 Services/ 目錄，那要由目錄本身的設計解決，不是這條守門"),
            HasPositiveControl: false),

        new("ULTIMATE 矩陣宣告的鍵都有人生產", GuardPolicy.GuardMode.Reconciliation,
            "規格宣告了輸出鍵、實作也做了，但沒有任何地方把它變成事實輸出——"
            + "規格有、實作有、沒接線。既有的目錄守門對此完全隱形（掃不到就不會報）",
            new GuardPolicy.GuardLevel(
                "已開工範圍內、矩陣有宣告、而原始碼掃不到生產者的鍵",
                "『生產了但使用者看不到』——那需要呈現層的另一組對帳"),
            HasPositiveControl: false),

        new("掃描骨架不得出現寫入 API", GuardPolicy.GuardMode.SourceScan,
            "一個『掃描器』在被讀取的路徑上順手改了硬體狀態（MSR 受控寫入、SuperIO 設定視窗），"
            + "而這種寫入出現在讀取程式碼裡的理由通常很正當，所以不會被 review 擋下來",
            new GuardPolicy.GuardLevel(
                "掃描器類別直接呼叫寫入 API",
                "『掃描器呼叫另一個會寫入的方法』——那是呼叫圖分析，不是原始碼掃描"),
            HasPositiveControl: true),

        new("供檔路徑不得出現出網 API", GuardPolicy.GuardMode.SourceScan,
            "使用者提供的檔案在被解析的那條路上連出去——這與本專案的零出網立場直接衝突",
            new GuardPolicy.GuardLevel(
                "供檔服務原始碼裡出現已知的出網 API",
                "反射或動態載入取得的連線能力——原始碼掃描看不到執行期才決定的型別"),
            HasPositiveControl: true),

        new("單一可用性型別", GuardPolicy.GuardMode.SourceScan,
            "全專案長出第二套事實可用性定義——同一個情形在不同服務裡得到不同答案，"
            + "使用者看到『有些卡片說讀不到、有些說一致』",
            new GuardPolicy.GuardLevel(
                "生產碼裡出現第二個可用性列舉或以布林表達可用性",
                "『用字串表示狀態』——字串沒有型別可以掃，那要由介面契約擋"),
            HasPositiveControl: false,
            PositiveControlExemption:
                "這條的樣式若失效會回空集合，而另一條『六態必須是唯一的列舉定義處』"
                + "會獨立抓到定義重複——兩條互為對方的正對照，任何一條失效另一條仍會亮。"),

        new("六態必須是唯一的列舉定義處", GuardPolicy.GuardMode.PolicyPin,
            "FactAvailability 被定義兩次，於是兩份定義開始漂移",
            new GuardPolicy.GuardLevel(
                // 這裡刻意不寫出完整的定義語法：Registry 是生產碼，而掃描守門掃的就是生產碼。
                // 第一版寫了完整的 "enum " + 型別名，於是這份「宣告」自己被掃成第二個定義處——
                // 守門抓到了描述它自己的那段文字。改成拆開寫。
                "生產碼裡那個可用性列舉的定義處多於一個",
                "『另一種表達同一組狀態的型別』——那由單一可用性型別那條守門管"),
            HasPositiveControl: false,
            PositiveControlExemption:
                "這是計數斷言（定義處必須恰好一個），沒有樣式失效的空間："
                + "把要抓的字串複製進任何檔案都會讓計數變成 2，也就是它必然會紅。"),

        new("檢視入口守門", GuardPolicy.GuardMode.SourceScan,
            "整頁畫面寫完（XAML、後置程式碼、服務、單元測試都在），卻沒有任何人指得到它——"
            + "它會編譯，煙霧測試也不會紅（沒人建構它就不會壞），而使用者的世界裡它不存在；"
            + "2026-10-18 的 TroubleshootView 就是這個形狀：故障排查嚮導做完，卻不在註冊表裡",
            new GuardPolicy.GuardLevel(
                "Views/ 裡的檢視沒有任何其他檔案引用它",
                "『有引用、但引用路徑本身也到不了使用者面前』——那要與命令面板／註冊表三方對帳"),
            HasPositiveControl: true),

        new("L3 禁項不得出現在輸出中", GuardPolicy.GuardMode.PolicyPin,
            "機密類欄位從顯示或匯出的某一條路徑漏出去——這一類缺陷不會產生錯誤訊息，"
            + "只會在下游被發現",
            new GuardPolicy.GuardLevel(
                "以已知禁項前綴命名的鍵出現在事實清單裡",
                "『值本身含機密但鍵名看不出來』——那需要內容分析，不是鍵名比對"),
            HasPositiveControl: true),
    ];

    /// <summary>檢查整份清冊。</summary>
    public static IReadOnlyList<string> ValidateAll()
    {
        var problems = new List<string>();
        foreach (var g in Guards)
            problems.AddRange(GuardPolicy.Validate(g));

        var dupes = Guards.GroupBy(g => g.Name).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (string d in dupes)
            problems.Add($"守門清冊出現重複名稱：{d}");

        return problems;
    }

    /// <summary>依模式統計——用來回答「我們的守門整體而言偏重哪一類防線」。</summary>
    public static IReadOnlyDictionary<GuardPolicy.GuardMode, int> CountByMode() =>
        Guards.GroupBy(g => g.Mode).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>沒有正對照的掃描類／政策類守門——這些最可能是「永遠是綠的」。</summary>
    public static IReadOnlyList<string> GuardsWithoutPositiveControl() =>
        Guards.Where(g => g.Mode is GuardPolicy.GuardMode.SourceScan or GuardPolicy.GuardMode.PolicyPin
                       && !g.HasPositiveControl)
              .Select(g => g.Name).ToList();
}
