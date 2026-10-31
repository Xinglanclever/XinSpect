using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XinSpect;

/// <summary>
/// 圓圈問號：滑鼠靠近就說明這一段在講什麼、能做什麼、以及動它有沒有風險。
/// <para>
/// 文字一律取自 <see cref="HelpCatalog"/>（以 <see cref="HelpKey"/> 為鍵），版面只負責掛鍵值。
/// 這樣同一個名詞在幾頁裡出現都只有一份說明，改一次就全站一致；
/// <c>Tests/HelpCatalogTests</c> 另外盯著「版面上的鍵一定查得到」與「說明表裡沒有孤兒」。
/// </para>
/// <para>
/// 查不到鍵值時整顆隱藏（<see cref="Visibility.Collapsed"/>）而不是顯示空提示——
/// 一個點下去甚麼都沒有的問號比沒有問號更糟。
/// </para>
/// </summary>
public partial class HelpDot : UserControl
{
    public HelpDot()
    {
        InitializeComponent();
        Apply();
        // 說明文字在程式碼裡產生（取自 HelpCatalog），視覺樹掃描涵蓋不到 ToolTip 內容，
        // 因此在產出點過 T，並在語言切換時重套一次。
        Loaded += (_, _) =>
        {
            LanguageService.Changed += OnLanguageChanged;
            ThemeService.Changed += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            LanguageService.Changed -= OnLanguageChanged;
            ThemeService.Changed -= OnThemeChanged;
        };
    }

    private void OnLanguageChanged() => Apply();

    /// <summary>主題換了要重新套一次顏色；問號本身是 SetResourceReference，會自己跟著換。</summary>
    private void OnThemeChanged() => Apply();

    public static readonly DependencyProperty HelpKeyProperty = DependencyProperty.Register(
        nameof(HelpKey), typeof(string), typeof(HelpDot),
        new PropertyMetadata("", (d, _) => ((HelpDot)d).Apply()));

    /// <summary>說明表鍵值，形如 <c>cpu/快取 Cache</c>。</summary>
    public string HelpKey
    {
        get => (string)GetValue(HelpKeyProperty);
        set => SetValue(HelpKeyProperty, value);
    }

    private void Apply()
    {
        var e = HelpCatalog.Find(HelpKey);
        if (e is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        TipTitle.Text = LanguageService.T(e.Title);
        TipWhat.Text = LanguageService.T(e.What);
        TipDoes.Text = LanguageService.T(e.Does);
        TipRisk.Text = LanguageService.T(e.RiskLine);

        string riskKey = e.Risk switch
        {
            HelpRisk.Danger => "CriticalBrush",
            HelpRisk.Caution => "WarningBrush",
            _ => "GoodBrush",
        };
        // 用 SetResourceReference 而不是直接指派 Brush 物件：指派會把「當下那一刻」的筆刷快取住，
        // 切換主題（尤其換到白紙底的瑞士版）之後，深色主題的淺灰字留在白底上就等於消失。
        // 唯讀項目的圓圈保持低調（灰）：整頁大半是唯讀，全部染色就等於沒有標示；
        // 只有真的會寫進硬體或系統的項目才把圓圈本身變成黃／紅。
        bool readOnly = e.Risk == HelpRisk.ReadOnly;
        Dot.SetResourceReference(BorderBrushProperty, readOnly ? "MutedInkBrush" : riskKey);
        Glyph.SetResourceReference(ForegroundProperty, readOnly ? "SecondaryInkBrush" : riskKey);
        TipRisk.SetResourceReference(ForegroundProperty, riskKey);
    }

    private Brush Brush(string key)
        => TryFindResource(key) as Brush ?? Brushes.Gray;
}
