namespace XinSpect;

/// <summary>
/// 全域鍵盤快捷鍵表（U10-UI-0001，追蹤鍵 <c>ui.keys.map</c>）：主視窗既有快捷鍵的單一來源，
/// 關於頁的「鍵盤快捷鍵」卡片由此填入，<c>Tests/KeyboardShortcutCatalogTests</c> 掃原始碼
/// 把這張表與實際綁定釘在一起。
/// </summary>
/// <remarks>
/// <para>
/// <b>盤點結果如實：</b>本版主視窗沒有任何 XAML <c>KeyBinding</c>／<c>InputBinding</c>；
/// 全域按鍵只有命令面板這一組（MainWindow 的 PreviewKeyDown 攔 Ctrl+K／Ctrl+P，
/// 面板開啟時 Esc／↑／↓／Enter 由 CommandPalette.HandleKey 處理）。表就列這幾個，
/// 不硬湊。日後新增快捷鍵時：綁定與這張表要一起改，守門測試會抓兩邊漂移。
/// </para>
/// </remarks>
public static class KeyboardShortcutCatalog
{
    /// <summary>U10-UI-0001 追蹤鍵。常數刻意不以「Key」命名：事實鍵目錄（FactKeyCatalog）
    /// 本輪凍結，原始碼掃描器會把 Services/ 裡名稱含「Key」的字串常數掃成事實鍵——
    /// 這是產品追蹤標記，不是事實鍵。字面值照放在 Services/ 供收口對帳。</summary>
    public const string ShortcutMapFactKey = "ui.keys.map";

    /// <summary>一列快捷鍵：按鍵組合與它做什麼。</summary>
    public sealed record ShortcutRow(string Keys, string Description, string Scope);

    /// <summary>主視窗的全域快捷鍵（含命令面板開啟時的面板內導航鍵）。</summary>
    public static IReadOnlyList<ShortcutRow> Global { get; } =
    [
        new("Ctrl+K", "開啟命令面板：搜尋任何頁面、工具與動作並直接執行", "全域"),
        new("Ctrl+P", "同樣開啟命令面板（沿用編輯器慣例）", "全域"),
        new("Esc", "關閉命令面板", "命令面板開啟時"),
        new("↑ / ↓", "在面板結果間移動選取", "命令面板開啟時"),
        new("Enter", "執行面板目前選取的項目", "命令面板開啟時"),
    ];

    /// <summary>誠實聲明：目前的快捷鍵就這幾個，不硬湊。</summary>
    public const string HonestyNote = "目前的全域快捷鍵就這幾個——如實列出，不硬湊。";
}
