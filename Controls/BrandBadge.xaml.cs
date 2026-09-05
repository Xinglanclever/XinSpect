using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XinSpect;

/// <summary>
/// 自繪品牌徽章：以品牌漸層色磚 + 白色類別字形 + 文字標記呈現，
/// 並支援特殊型號（CPU / GPU）的專屬配色與疊加層（圓環 / 邊框 / 直書 / 四角小標）。
/// 全為原創向量圖形（色彩 + 幾何字形），非官方商標圖樣之重製。
/// </summary>
public partial class BrandBadge : UserControl
{
    public BrandBadge()
    {
        InitializeComponent();
        Rebuild();
    }

    public static readonly DependencyProperty BrandProperty =
        DependencyProperty.Register(nameof(Brand), typeof(Brand), typeof(BrandBadge),
            new PropertyMetadata(Brand.Unknown, OnAnyChanged));

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(BadgeKind), typeof(BrandBadge),
            new PropertyMetadata(BadgeKind.Generic, OnAnyChanged));

    public static readonly DependencyProperty ModelProperty =
        DependencyProperty.Register(nameof(Model), typeof(string), typeof(BrandBadge),
            new PropertyMetadata("", OnAnyChanged));

    public static readonly DependencyProperty CpuModelProperty =
        DependencyProperty.Register(nameof(CpuModel), typeof(string), typeof(BrandBadge),
            new PropertyMetadata("", OnAnyChanged));

    public static readonly DependencyProperty ShowModelProperty =
        DependencyProperty.Register(nameof(ShowModel), typeof(bool), typeof(BrandBadge),
            new PropertyMetadata(true, OnAnyChanged));

    public static readonly DependencyProperty EmblemSizeProperty =
        DependencyProperty.Register(nameof(EmblemSize), typeof(double), typeof(BrandBadge),
            new PropertyMetadata(48.0, OnAnyChanged));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(BrandBadge),
            new PropertyMetadata(false, OnAnyChanged));

    public Brand Brand { get => (Brand)GetValue(BrandProperty); set => SetValue(BrandProperty, value); }
    public BadgeKind Kind { get => (BadgeKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Model { get => (string)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    /// <summary>CPU 型號字串（WMI Name）。用於辨識特殊型號並套用專屬圖示，僅 Kind=Cpu 時有效。</summary>
    public string CpuModel { get => (string)GetValue(CpuModelProperty); set => SetValue(CpuModelProperty, value); }
    public bool ShowModel { get => (bool)GetValue(ShowModelProperty); set => SetValue(ShowModelProperty, value); }
    public double EmblemSize { get => (double)GetValue(EmblemSizeProperty); set => SetValue(EmblemSizeProperty, value); }
    /// <summary>僅顯示色磚徽章、隱藏文字（用於表格列前綴）。</summary>
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BrandBadge)d).Rebuild();

    private void Rebuild()
    {
        if (Glyph is null) return;

        var info = Brands.Info(Brand);
        double E = EmblemSize;

        Emblem.Width = Emblem.Height = E;
        double radius = Math.Round(E * 0.26);
        Emblem.CornerRadius = new CornerRadius(radius);

        // 特殊型號的專屬圖示：CPU 取 CpuModel，GPU 取 Model；其餘類別為 null。
        string probe = Kind == BadgeKind.Cpu ? CpuModel : Model;
        var edition = DeviceIcons.Resolve(Kind, probe);

        // 每次重繪都先重置所有前景層與疊加層，避免沿用上一次綁定的狀態。
        GlyphBox.Visibility = Visibility.Collapsed;
        GlyphBox.Margin = new Thickness(10);      // 預設字形內距（XAML 原值）
        GlyphBox.RenderTransform = null;
        TextBox.Visibility = Visibility.Collapsed;
        TextBox.RenderTransform = null;
        TextBox.Margin = new Thickness(15);       // 預設文字內距（XAML 原值）
        LogoImage.Visibility = Visibility.Collapsed;
        LogoImage.Source = null;
        RingOverlay.Visibility = Visibility.Collapsed;
        FrameOverlay.Visibility = Visibility.Collapsed;
        SideText.Visibility = Visibility.Collapsed;
        LowerLeftText.Visibility = Visibility.Collapsed;
        CornerText.Visibility = Visibility.Collapsed;
        TopMarkText.Visibility = Visibility.Collapsed;
        TopColorBevel.Visibility = Visibility.Collapsed;

        if (edition is not null)
        {
            // 徽章底色（可為純色 / 對角雙色 / 多段彩虹）＋ 版本專屬前景色。
            Emblem.Background = BuildBrush(edition.Emblem, new Point(0, 0), new Point(1, 1));
            EmblemShadow.Color = Parse(edition.Emblem[^1]);
            var ink = new SolidColorBrush(Parse(edition.Ink));

            var logo = string.IsNullOrEmpty(edition.Text)
                ? TryLoadLogo(DeviceIcons.AssetFolder(Kind), edition.Id)
                : null;
            if (logo is not null)
            {
                // ① 官方 logo 圖檔：中性底 + 圖檔。
                LogoImage.Source = logo;
                LogoImage.Visibility = Visibility.Visible;
                Emblem.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));
            }
            else if (!string.IsNullOrEmpty(edition.Text))
            {
                // ② 中心文字（工程樣品 "ES" / 至尊 "XE" / "Everest"）。Viewbox 會將文字連同行框
                //    （含上緣行距與降部空間）一併置中，使全大寫字的視覺重心略高於幾何中心。
                EmblemText.Text = edition.Text;
                EmblemText.Foreground = ink;
                if (edition.Id == "es-sample")
                    // 工程樣品「ES」（不分廠牌 AMD/Intel/其他）：整體明顯向左下角偏移。
                    TextBox.RenderTransform = new TranslateTransform(-E * 0.14, E * 0.17);
                else if (edition.Id == "threadripper" || edition.Id == "ryzen")
                {
                    // 撕裂者／一般 Ryzen「Ryzen」：置於紅圈中央並放大至大過圓圈（縮小內距＝放大）。
                    TextBox.Margin = new Thickness(E * 0.06);
                    TextBox.RenderTransform = new TranslateTransform(0, E * 0.02);
                }
                else if (edition.Text.Length <= 2)
                    // 短標「XE」（≤2 字）：向右下角偏移（沿用早期版位）。
                    TextBox.RenderTransform = new TranslateTransform(E * 0.09, E * 0.21);
                else
                {
                    // 長字（Everest / BlackOps）：縮小內距＝放大字，並略向下補償。
                    TextBox.Margin = new Thickness(E * 0.13);
                    TextBox.RenderTransform = new TranslateTransform(0, E * 0.03);
                }
                TextBox.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(edition.Glyph))
            {
                // ③ 原創專屬向量字形（★紀念 / ✕至尊 / 處理器 / 顯示卡）。
                Glyph.Data = Geometry.Parse(CenterGlyph(edition.Glyph));
                Glyph.Fill = ink;
                if (edition.Glyph == "star")
                {
                    // ★紀念徽記（8086K）：縮小一些，仍向左上偏移。
                    GlyphBox.Margin = new Thickness(E * 0.18);
                    GlyphBox.RenderTransform = new TranslateTransform(-E * 0.05, -E * 0.05);
                }
                else if (edition.Glyph == "gpu")
                {
                    // 顯示卡字形：略放大並上移、微左移（字形本身偏右下，於此補償），
                    // 讓出底部橫帶避開右下角標（如 "Titan V"）。
                    GlyphBox.Margin = new Thickness(E * 0.14);
                    GlyphBox.RenderTransform = new TranslateTransform(-E * 0.05, -E * 0.11);
                }
                GlyphBox.Visibility = Visibility.Visible;
            }
            // ④ 皆空（如撕裂者）→ 中心留白，僅靠疊加層（圓環）表現。

            ApplyOverlays(edition, E, radius);
        }
        else
        {
            // 品牌通用徽章（既有行為）；記憶體類別統一採亮紫色，不分品牌。
            string[] palette = Kind == BadgeKind.Ram
                ? new[] { "#B24BF5", "#6E17C4" }
                : new[] { info.Color1, info.Color2 };
            Emblem.Background = BuildBrush(palette, new Point(0, 0), new Point(1, 1));
            EmblemShadow.Color = Parse(palette[^1]);

            // 主機板：若能辨識廠商（華碩／技嘉／微星／EVGA／ASRock／映泰／美超微…），
            // 以該廠商的原創字母標記取代通用主機板字形；無對應者才用通用字形。
            string? monogram = Kind == BadgeKind.Board ? Brands.BoardMonogram(Brand) : null;
            if (monogram is not null)
            {
                EmblemText.Text = monogram;
                EmblemText.Foreground = Brushes.White;
                // 字數越多內距越大（避免過寬）；Viewbox 會等比放大置中。
                TextBox.Margin = new Thickness(E * (monogram.Length >= 3 ? 0.26 : monogram.Length == 2 ? 0.20 : 0.16));
                TextBox.Visibility = Visibility.Visible;
            }
            else
            {
                Glyph.Data = Geometry.Parse(GlyphPath(Kind));
                Glyph.Fill = Brushes.White;
                GlyphBox.Visibility = Visibility.Visible;
            }
        }

        if (Compact)
        {
            TextPanel.Visibility = Visibility.Collapsed;
            // 精簡模式（表格列）隱藏文字，改以工具提示補充版本資訊。
            ToolTip = edition is not null ? $"{info.Name} · {edition.Tier}" : null;
            return;
        }
        TextPanel.Visibility = Visibility.Visible;
        ToolTip = null;

        string model = Model ?? "";
        if (!string.IsNullOrEmpty(info.Name))
        {
            NameText.Text = info.Name;
            ModelText.Text = model;
            ModelText.Visibility = ShowModel && model.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            // 未知品牌：以型號本身作主標
            NameText.Text = model.Length > 0 ? model : "未知裝置";
            ModelText.Visibility = Visibility.Collapsed;
        }

        // 版本徽記（至尊版 / 紀念版 / 工程樣品…）：以膠囊專屬漸層作底、白字，深淺主題皆清晰。
        if (edition is not null)
        {
            TierText.Text = edition.Tier;
            TierText.Foreground = Brushes.White;
            TierChip.Background = BuildBrush(edition.Chip, new Point(0, 0), new Point(0, 1));
            TierChip.Visibility = Visibility.Visible;

            // 第二枚膠囊：雙重身分才畫（如 X5698 掛「Everest 珠穆朗瑪峰系列」＋藍色「Xeon」）。
            if (!string.IsNullOrEmpty(edition.Tier2) && edition.Chip2 is not null)
            {
                TierText2.Text = edition.Tier2;
                TierText2.Foreground = Brushes.White;
                TierChip2.Background = BuildBrush(edition.Chip2, new Point(0, 0), new Point(0, 1));
                TierChip2.Visibility = Visibility.Visible;
            }
            else
            {
                TierChip2.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            TierChip.Visibility = Visibility.Collapsed;
            TierChip2.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>套用選配疊加層：圓環 / 矩形邊框 / 左上橫書 / 左下・右下・右上小標 / 右上級別斜面。</summary>
    private void ApplyOverlays(BadgeIcon e, double E, double radius)
    {
        var markInk = new SolidColorBrush(Parse(e.MarkInk));

        // 圓形環（撕裂者紅圈）
        if (e.Ring is not null)
        {
            RingOverlay.Stroke = BuildBrush(e.Ring, new Point(0, 0), new Point(1, 1));
            RingOverlay.StrokeThickness = Math.Max(2, E * 0.075);
            RingOverlay.Margin = new Thickness(E * 0.14);
            RingOverlay.Visibility = Visibility.Visible;
        }

        // 矩形邊框（8086K 炫彩 / 9990XE 炫彩 / Everest 炫彩 / Titan 銀 / CEO・Titan V 金），沿徽章圓角
        if (e.Frame is not null)
        {
            FrameOverlay.BorderBrush = BuildBrush(e.Frame, new Point(0, 0), new Point(1, 1));
            FrameOverlay.BorderThickness = new Thickness(Math.Max(2, E * 0.055));
            FrameOverlay.CornerRadius = new CornerRadius(radius);
            FrameOverlay.Visibility = Visibility.Visible;
        }

        // 左上角橫書縮小字（Xeon）
        if (!string.IsNullOrEmpty(e.Side))
        {
            SideText.Text = e.Side;
            SideText.Foreground = markInk;
            SideText.FontSize = Math.Max(6, E * 0.155);
            SideText.Margin = new Thickness(E * 0.11, E * 0.075, 0, 0);
            SideText.Visibility = Visibility.Visible;
        }

        // 左下角小標（8086）
        if (!string.IsNullOrEmpty(e.LowerLeft))
        {
            LowerLeftText.Text = e.LowerLeft;
            LowerLeftText.Foreground = markInk;
            LowerLeftText.FontSize = Math.Max(6, E * 0.20);
            LowerLeftText.Margin = new Thickness(E * 0.08, 0, 0, E * 0.055);
            LowerLeftText.Visibility = Visibility.Visible;
        }

        // 右下角小標（Ryzen / P / Titan / CEO / Tesla / Titan V）
        if (!string.IsNullOrEmpty(e.Corner))
        {
            bool gpu = Kind == BadgeKind.Gpu;
            CornerText.Text = e.Corner;
            CornerText.Foreground = markInk;
            // 顯示卡：字略放大並向左移（加大右內距）；Titan／Titan V 再向左移一格。
            CornerText.FontSize = Math.Max(6, E * (gpu ? 0.195 : 0.18));
            double cornerRight = e.Id is "titan" or "titan-v" ? E * 0.17 : E * (gpu ? 0.10 : 0.08);
            CornerText.Margin = new Thickness(0, 0, cornerRight, E * 0.055);
            CornerText.Visibility = Visibility.Visible;
        }

        // 右上角小標（W / E3 / 40ᵀᴴ）與右上級別斜面（Scalable）互斥
        if (!string.IsNullOrEmpty(e.TopMark))
        {
            BuildTopMark(e.TopMark, markInk, E);
            // 8086K 的「40ᵀᴴ」向左移；Xeon W/E 系字母維持靠右上角。
            double topRight = e.Id == "i7-8086k" ? E * 0.15 : E * 0.07;
            TopMarkText.Margin = new Thickness(0, E * 0.05, topRight, 0);
            TopMarkText.Visibility = Visibility.Visible;
        }
        else if (e.TopColor is not null)
        {
            // 右上角級別斜面：切去右上角的三角，填代表色（銅/銀/金/白金），裁切至徽章圓角。
            double s = E * 0.44;
            TopColorBevel.Width = TopColorBevel.Height = E;
            TopColorBevel.Points = new PointCollection
            {
                new Point(E - s, 0), new Point(E, 0), new Point(E, s)
            };
            TopColorBevel.Fill = BuildBrush(e.TopColor, new Point(0, 0), new Point(1, 1));
            TopColorBevel.Clip = new RectangleGeometry(new Rect(0, 0, E, E), radius, radius);
            TopColorBevel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>右上角小標：支援 "40^TH" 形式（^ 之後為上標並縮小為一半）。</summary>
    private void BuildTopMark(string mark, Brush ink, double E)
    {
        TopMarkText.Inlines.Clear();
        TopMarkText.Foreground = ink;
        double baseSize = Math.Max(6, E * 0.20);
        TopMarkText.FontSize = baseSize;
        int caret = mark.IndexOf('^');
        if (caret >= 0)
        {
            TopMarkText.Inlines.Add(new Run(mark.Substring(0, caret)));
            TopMarkText.Inlines.Add(new Run(mark.Substring(caret + 1))
            {
                BaselineAlignment = BaselineAlignment.Superscript,
                FontSize = baseSize * 0.5,
            });
        }
        else
        {
            TopMarkText.Inlines.Add(new Run(mark));
        }
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>依色段數建立筆刷：1 段=純色、2 段=對角雙色、3+ 段=均分多段漸層（彩虹）。</summary>
    private static Brush BuildBrush(string[] colors, Point start, Point end)
    {
        if (colors is null || colors.Length == 0) return Brushes.Transparent;
        if (colors.Length == 1) return new SolidColorBrush(Parse(colors[0]));
        var gb = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (int i = 0; i < colors.Length; i++)
            gb.GradientStops.Add(new GradientStop(Parse(colors[i]), (double)i / (colors.Length - 1)));
        return gb;
    }

    /// <summary>載入特殊型號的官方 logo 圖檔（Assets/{folder}/{id}.png，需標記為 Resource）；不存在則回 null。</summary>
    private static ImageSource? TryLoadLogo(string? folder, string id)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/{folder}/{id}.png", UriKind.Absolute);
            var res = System.Windows.Application.GetResourceStream(uri);
            if (res is null) return null;
            using var stream = res.Stream;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }  // 圖檔不存在或格式錯誤 → 靜默回退向量徽章
    }

    /// <summary>中心字形：處理器 / 顯示卡取類別字形，其餘取專屬特殊字形（★ / ✕）。</summary>
    private static string CenterGlyph(string key) => key switch
    {
        "cpu" => GlyphPath(BadgeKind.Cpu),
        "gpu" => GlyphPath(BadgeKind.Gpu),
        "disk" => GlyphPath(BadgeKind.Disk),
        "board" => GlyphPath(BadgeKind.Board),
        _ => SpecialGlyph(key),
    };

    /// <summary>特殊版本的專屬白色字形（100×100 座標，非零填充）。</summary>
    private static string SpecialGlyph(string key) => key switch
    {
        // 五角星：限量 / 週年紀念版
        "star" =>
            "F1 M50,24 L56.2,41.5 L74.7,42 L60,53.2 L65.3,71 L50,60.5 " +
            "L34.7,71 L40,53.2 L25.3,42 L43.8,41.5 Z",
        // 交叉 X：至尊版 Extreme Edition
        "x" =>
            "F1 M34,40 L40,34 L66,60 L60,66 Z M60,34 L66,40 L40,66 L34,60 Z",
        // 蒸汽閥（Steam 標記）：左下大環 + 連桿 + 右上小環。CC150 專用。
        //    採 EvenOdd（F0）挖出兩環內孔，故三塊圖形刻意不重疊——連桿兩端只切齊環的外緣
        //    （端點距圓心恰等於外半徑），一旦壓進環身就會被 XOR 挖掉一塊。
        "steam" =>
            "F0 M10,64 A24,24 0 1 1 58,64 A24,24 0 1 1 10,64 Z " +      // 大環外圈（圓心 34,64 半徑 24）
            "M24,64 A10,10 0 1 1 44,64 A10,10 0 1 1 24,64 Z " +         // 大環內孔（半徑 10）
            "M59,28 A14,14 0 1 1 87,28 A14,14 0 1 1 59,28 Z " +         // 小環外圈（圓心 73,28 半徑 14）
            "M67.5,28 A5.5,5.5 0 1 1 78.5,28 A5.5,5.5 0 1 1 67.5,28 Z " + // 小環內孔（半徑 5.5）
            "M55.3,51.7 L66.4,41.5 L59.0,33.5 L47.9,43.7 Z",            // 連桿（寬 11，貼齊兩環外緣）
        _ => "F1 M30,30 H70 V70 H30 Z",
    };

    private static string GlyphPath(BadgeKind kind) => kind switch
    {
        // 處理器：外殼 + 核心方孔 + 四邊接腳
        BadgeKind.Cpu =>
            "F0 M33,31 H67 A2,2 0 0 1 69,33 V67 A2,2 0 0 1 67,69 H33 A2,2 0 0 1 31,67 V33 A2,2 0 0 1 33,31 Z " +
            "M41,41 H59 V59 H41 Z " +
            "M38,23 H42 V31 H38 Z M48,23 H52 V31 H48 Z M58,23 H62 V31 H58 Z " +
            "M38,69 H42 V77 H38 Z M48,69 H52 V77 H48 Z M58,69 H62 V77 H58 Z " +
            "M23,38 H31 V42 H23 Z M23,48 H31 V52 H23 Z M23,58 H31 V62 H23 Z " +
            "M69,38 H77 V42 H69 Z M69,48 H77 V52 H69 Z M69,58 H77 V62 H69 Z",
        // 顯示卡：長板 + 風扇孔 + 兩條散熱縫 + 右側支架
        BadgeKind.Gpu =>
            "F0 M20,34 H80 A4,4 0 0 1 84,38 V62 A4,4 0 0 1 80,66 H34 L20,66 Z " +
            "M42,50 m-13,0 a13,13 0 1,0 26,0 a13,13 0 1,0 -26,0 Z " +
            "M64,42 H78 V46 H64 Z M64,54 H78 V58 H64 Z",
        // 儲存裝置：SSD 外殼 + 兩條晶片縫
        BadgeKind.Disk =>
            "F0 M26,30 H74 A3,3 0 0 1 77,33 V67 A3,3 0 0 1 74,70 H26 A3,3 0 0 1 23,67 V33 A3,3 0 0 1 26,30 Z " +
            "M30,38 H70 V45 H30 Z M30,52 H70 V59 H30 Z",
        // 記憶體：模組本體 + 三顆晶片
        BadgeKind.Ram =>
            "F0 M21,37 H79 A3,3 0 0 1 82,40 V56 A3,3 0 0 1 79,59 H21 A3,3 0 0 1 18,56 V40 A3,3 0 0 1 21,37 Z " +
            "M27,42 H39 V54 H27 Z M44,42 H56 V54 H44 Z M61,42 H73 V54 H61 Z",
        // 網路：地球（圓 + 經緯裂縫）
        BadgeKind.Net =>
            "F0 M50,26 a24,24 0 1,0 0.01,0 Z " +
            "M26,47 H74 V53 H26 Z M47,26 H53 V74 H47 Z",
        // 主機板：外框 + 插槽 + 晶片
        BadgeKind.Board =>
            "F0 M24,24 H76 A2,2 0 0 1 78,26 V74 A2,2 0 0 1 76,76 H24 A2,2 0 0 1 22,74 V26 A2,2 0 0 1 24,24 Z " +
            "M33,33 H51 V51 H33 Z M57,57 H69 V69 H57 Z M57,33 H69 V37 H57 Z",
        // 一般：色磚 + 中心方點
        _ =>
            "F0 M30,30 H70 V70 H30 Z M44,44 H56 V56 H44 Z",
    };
}
