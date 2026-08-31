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
    }

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
        TipTitle.Text = e.Title;
        TipWhat.Text = e.What;
        TipDoes.Text = e.Does;
        TipRisk.Text = e.RiskLine;

        var brush = Brush(e.Risk switch
        {
            HelpRisk.Danger => "CriticalBrush",
            HelpRisk.Caution => "WarningBrush",
            _ => "GoodBrush",
        });
        TipRisk.Foreground = brush;
        // 唯讀項目的圓圈保持低調（灰）：整頁大半是唯讀，全部染色就等於沒有標示；
        // 只有真的會寫進硬體或系統的項目才把圓圈本身變成黃／紅。
        Dot.BorderBrush = e.Risk == HelpRisk.ReadOnly ? Brush("MutedInkBrush") : brush;
        Glyph.Foreground = e.Risk == HelpRisk.ReadOnly ? Brush("SecondaryInkBrush") : brush;
    }

    private Brush Brush(string key)
        => TryFindResource(key) as Brush ?? Brushes.Gray;
}
