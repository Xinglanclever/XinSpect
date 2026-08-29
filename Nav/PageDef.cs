using System.Windows.Controls;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 單一導覽頁的宣告式定義。取代 1.x 時代「MainWindow.xaml 手寫 ListBoxItem ↔ _views[] 陣列」
/// 的索引平行對應：新增一頁只需在 <see cref="PageRegistry.Pages"/> 加一筆，側邊欄、命令面板、
/// 延遲載入與感測閘門全部自動跟上。
/// </summary>
public sealed class PageDef
{
    /// <summary>穩定識別碼（設定持久化、命令面板、程式化跳頁皆以此為鍵，不隨標題翻譯改動）。</summary>
    public required string Key { get; init; }

    /// <summary>側邊欄與命令面板顯示的名稱。</summary>
    public required string Title { get; init; }

    /// <summary>側邊欄分組標題（同組相鄰者會被歸在同一個標頭下）。</summary>
    public required string Group { get; init; }

    /// <summary>圖示幾何（Path Mini-Language；支援 F0/F1 填滿規則前綴）。</summary>
    public required string IconData { get; init; }

    /// <summary>首次進入該頁時才呼叫，用以建立檢視（延遲實體化）。</summary>
    public required Func<UserControl> Factory { get; init; }

    /// <summary>命令面板的額外搜尋關鍵字（別名、英文名、頁內功能字眼）。</summary>
    public string[] Keywords { get; init; } = [];

    /// <summary>命令面板顯示的副標說明，亦作為側邊欄的滑鼠提示。</summary>
    public string? Hint { get; init; }

    /// <summary>進入前需通過風險兩階段確認（超頻類頁面）。</summary>
    public bool RequiresRiskConsent { get; init; }

    /// <summary>
    /// 感測閘門：該頁是否顯示，決定感測引擎要不要每秒做某段昂貴工作。
    /// 由外殼在切頁時對所有頁面統一重放（第二參數為「此頁是否為當前頁」），
    /// 故感測引擎晚到時只要再跑一次即可，不需要任何型別判斷。
    /// </summary>
    public Action<SensorService, bool>? LiveGate { get; init; }

    private Geometry? _icon;
    /// <summary>解析後的圖示幾何（僅解析一次並凍結，供 XAML 直接繫結 Path.Data）。</summary>
    public Geometry Icon
    {
        get
        {
            if (_icon is null)
            {
                _icon = Geometry.Parse(IconData);
                _icon.Freeze();
            }
            return _icon;
        }
    }
}
