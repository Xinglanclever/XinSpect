using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 核心熱區圖的契約（不碰 WPF）：格子的資料形狀、「沒有讀值」怎麼表示、
/// 來源每秒改值時格子跟不跟得上、以及欄數演算法。
/// </summary>
/// <remarks>
/// 這一組刻意用真的 <see cref="CoreRow"/>／<see cref="CoreTempEntry"/> 當來源，不用假物件：
/// 熱區圖的格子是<b>活轉接</b>，轉接的對象就是這兩種真型別的通知名稱，用假物件測等於測假的。
/// </remarks>
public class CoreHeatmapContractTests
{
    /// <summary>假的感測來源：只有通知形狀要對，數值自己給。</summary>
    private sealed class FakeCore : ObservableObject, ICoreHeatCell
    {
        private double? _temp;
        public double? TempC { get => _temp; set { if (SetProperty(ref _temp, value)) OnPropertyChanged(nameof(TempText)); } }
        public string Name { get; init; } = "核心 #1";
        public double LoadPercent { get; set; } = 40;
        public string TempText => TempC is { } t ? $"{t:0} °C" : "—";
    }

    [Fact]
    public void 兩種逐核來源都實作熱區圖契約()
    {
        // 熱區圖只有一套畫格子的方式，所以兩個來源都必須符合同一個契約。
        // 少了這個檢查，某天有人改了其中一邊的屬性名，編譯不會紅、畫面上那一格會靜默變空。
        Assert.True(typeof(ICoreHeatCell).IsAssignableFrom(typeof(CoreRow)));
        Assert.True(typeof(ICoreHeatCell).IsAssignableFrom(typeof(CoreTempEntry)));
    }

    [Fact]
    public void 雙來源的同一個契約成員_讀得到實際數值()
    {
        var row = new CoreRow("核心 #3") { TempC = 68, LoadPercent = 55 };
        var entry = new CoreTempEntry(2, "核心 #3") { Temperature = 68, Load = 55 };

        foreach (ICoreHeatCell cell in new ICoreHeatCell[] { row, entry })
        {
            Assert.Equal("核心 #3", cell.Name);
            Assert.Equal(68, cell.TempC);
            Assert.Equal(55, cell.LoadPercent);
        }
    }

    [Fact]
    public void 逐實體核心摘要的編號_從1起算()
    {
        // 這個標籤會直接出現在畫面上（熱區圖的格子角落與統計列）。
        // 全站其他核心編號都是 1 起算，這裡若從 0 起算，同一台機器會有兩套編號。
        var entry = new CoreTempEntry(0, "核心 #1");
        Assert.Equal("核心 #1", entry.Name);
        Assert.Equal(0, entry.PhysicalCoreId);   // 內部索引仍是 0 起算（映射用），只有顯示名是 1 起算
    }

    [Fact]
    public void 沒有讀值的核_不畫顏色也不說0度()
    {
        var cell = new CoreHeatCell(0, new FakeCore { TempC = null });

        Assert.False(cell.HasReading);
        Assert.Null(cell.Fill);            // 顏色＝資料，沒有資料就沒有顏色
        Assert.Equal("—", cell.TempText);
        Assert.DoesNotContain("0 °C", cell.TempText);
    }

    [Fact]
    public void 沒有來源列的位置_仍然佔一格並說清楚是沒有感測器()
    {
        // 感測器少回幾顆時，缺的位置不能被拿掉——那會讓 18 核看起來像 8 核。
        var cell = new CoreHeatCell(4, null);

        Assert.False(cell.HasSource);
        Assert.False(cell.HasReading);
        Assert.Equal("核心 #5", cell.Name);
        Assert.Contains("沒有對應的逐核感測器", cell.Tooltip);
    }

    [Fact]
    public void 來源每秒改值_格子跟著通知顏色()
    {
        var source = new FakeCore { TempC = 45 };
        using var cell = new CoreHeatCell(0, source);

        var changed = new List<string>();
        ((INotifyPropertyChanged)cell).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        source.TempC = 88;

        Assert.Equal(88, cell.TempC);
        Assert.Equal("88 °C", cell.TempText);
        Assert.Contains(nameof(CoreHeatCell.TempC), changed);
        Assert.Contains(nameof(CoreHeatCell.Fill), changed);
        Assert.Contains(nameof(CoreHeatCell.TempText), changed);
        // 88 °C 是熱紅，跟 45 °C 的青綠不會是同一個顏色
        Assert.NotEqual(HeatScale.ColorFor(45), HeatScale.ColorFor(88));
    }

    [Fact]
    public void 轉接用別的通知名也跟得上()
    {
        // CoreTempEntry 對外叫 Temperature，契約名才叫 TempC。真實情境裡的通知名不只一種，
        // 轉接層漏接就會變成「溫度永遠停在開頁面那一刻」——比不畫還糟。
        var entry = new CoreTempEntry(0, "核心 #1");
        using var cell = new CoreHeatCell(0, entry);

        var changed = new List<string>();
        ((INotifyPropertyChanged)cell).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        entry.Temperature = 71;

        Assert.Equal(71, cell.TempC);
        Assert.Contains(nameof(CoreHeatCell.TempC), changed);
    }

    [Fact]
    public void 停用後不再跟著來源()
    {
        var source = new FakeCore { TempC = 45 };
        var cell = new CoreHeatCell(0, source);
        cell.Dispose();

        var changed = new List<string>();
        ((INotifyPropertyChanged)cell).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        source.TempC = 90;

        Assert.Empty(changed);   // 重建過的舊格子不該還掛在來源上（會累積成一群沒人看的接線）
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(8, 4)]
    [InlineData(12, 4)]
    [InlineData(16, 8)]
    [InlineData(18, 6)]
    [InlineData(24, 8)]
    [InlineData(32, 8)]
    [InlineData(64, 16)]
    public void 欄數演算法_常見核心數排成橫向晶片(int cores, int expectedColumns)
        => Assert.Equal(expectedColumns, CoreHeatLayout.Columns(cores));

    [Fact]
    public void 欄數演算法_任何核心數都不吃格子也不留整列空白()
    {
        for (int count = 1; count <= 128; count++)
        {
            int columns = CoreHeatLayout.Columns(count);
            int rows = CoreHeatLayout.Rows(count, columns);

            Assert.InRange(columns, 1, CoreHeatLayout.MaxColumns);
            Assert.True(columns * rows >= count, $"{count} 核排成 {columns}×{rows} 會吃掉格子。");
            Assert.True(columns * rows - count < columns,
                $"{count} 核排成 {columns}×{rows} 會留下整列空白。");
        }
    }

    [Fact]
    public void 色階_兩端夾住且中段分得出來()
    {
        Assert.Null(HeatScale.ColorFor(null));
        Assert.Null(HeatScale.ColorFor(double.NaN));
        Assert.Equal(HeatScale.Anchors[0].Color, HeatScale.ColorFor(-40));
        Assert.Equal(HeatScale.Anchors[^1].Color, HeatScale.ColorFor(150));

        // 62 °C 與 70 °C 要看得出來不一樣：這是真實處理器最常待的區間，
        // 整張圖只有一種綠色的話，這張圖就白畫了。
        var cool = HeatScale.ColorFor(62)!.Value;
        var warm = HeatScale.ColorFor(70)!.Value;
        int delta = Math.Abs(cool.R - warm.R) + Math.Abs(cool.G - warm.G) + Math.Abs(cool.B - warm.B);
        Assert.True(delta > 60, $"62 °C 與 70 °C 的顏色只差 {delta}（太小，看不出來）。");
    }

    [Fact]
    public void 墨色_淺底配深字深底配白字()
    {
        var lime = HeatScale.ColorFor(58)!;
        var hot = HeatScale.ColorFor(92)!;

        Assert.Equal(Color.FromRgb(0x14, 0x14, 0x12), ((SolidColorBrush)HeatScale.InkFor(lime)).Color);
        Assert.Equal(Colors.White, ((SolidColorBrush)HeatScale.InkFor(hot)).Color);
        Assert.Equal(Colors.White, ((SolidColorBrush)HeatScale.InkFor(null)).Color);   // 無讀值交由主題色接手
    }

    [Fact]
    public void 色階錨點_與嚴重度分級同一條線()
    {
        // 數字顏色（Health.Cpu）與格子顏色若各說各話，讀者得先決定相信哪一個，那就等於沒有顏色語意。
        Assert.Contains(HeatScale.Anchors, a => Math.Abs(a.Temperature - 80) < 0.001);   // 80 °C 起警戒
        Assert.True(HeatScale.Hottest >= 90, "色階頂端要蓋到接近 TjMax，否則 90 °C 以上全都同色。");
        Assert.True(HeatScale.Coldest <= 40, "色階底端要低於常見閒置溫度，否則低溫全被夾在同一格。");
    }

    [Fact]
    public void 圖例_一個錨點一格且都取得到顏色()
    {
        Assert.Equal(HeatScale.Anchors.Count, HeatScale.Legend.Count);
        Assert.All(HeatScale.Legend, t =>
        {
            Assert.NotNull(t.Fill);
            Assert.EndsWith("°", t.Text);
            Assert.Equal(HeatScale.ColorFor(t.Temperature), ((SolidColorBrush)t.Fill).Color);
        });
    }
}

/// <summary>
/// 核心熱區圖的<b>渲染</b>測試：真的建構控制項、真的跑版面、真的把樣板套用起來，
/// 並由繫結追蹤攔下路徑錯誤。這是舊版缺的那一層——舊測試只驗證「建構得起來」，
/// 而樣板要等真的有資料才會被套用，所以「有資料時畫不畫得出來」從來沒人驗過。
/// </summary>
[Collection(WpfCollection.Name)]
public class CoreHeatmapRenderTests
{
    private sealed class CollectingListener : TraceListener
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string? message) => _sb.Append(message);
        public override void WriteLine(string? message) => _sb.AppendLine(message);
        public string Drain() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "熱區圖渲染測試逾時。");
        if (failure is not null) throw failure;
    }

    private static List<CoreRow> Rows(int count, double firstTemp = 60)
    {
        var rows = new List<CoreRow>();
        for (int i = 0; i < count; i++)
            rows.Add(new CoreRow($"核心 #{i + 1}") { TempC = firstTemp + i, LoadPercent = 30 + i });
        return rows;
    }

    private static CoreHeatmap Layout(CoreHeatmap map, double width = 900)
    {
        map.Measure(new Size(width, 400));
        map.Arrange(new Rect(0, 0, width, 400));
        map.UpdateLayout();
        return map;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>畫面上真的長出來的格子（＝套用了樣板的那幾格）。</summary>
    private static List<Border> Tiles(FrameworkElement map)
        => [.. Descendants(map).OfType<Border>()
            .Where(b => b.DataContext is CoreHeatCell && b.CornerRadius.TopLeft == 8)];

    [Fact]
    public void 十八顆核心_畫出十八格且沒有繫結錯誤()
    {
        OnSta(() =>
        {
            var failures = new List<string>();
            var app = WpfEnv.Ensure();
            PresentationTraceSources.Refresh();
            var listener = new CollectingListener();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            try
            {
                var map = Layout(new CoreHeatmap { Cores = Rows(18), PhysicalCores = 18 });

                if (map.Cells.Count != 18) failures.Add($"格子數 {map.Cells.Count}，預期 18。");
                if (map.Columns != 6) failures.Add($"欄數 {map.Columns}，預期 6（18 核排 6×3）。");
                if (map.BoardWidth <= 0 || map.BoardHeight <= 0) failures.Add("核區沒有量到大小，樣板可能沒套用。");
                if (!map.HasCells) failures.Add("HasCells 是 false，畫面會顯示「沒有可畫的核心」。");

                var tiles = Tiles(map);
                if (tiles.Count != 18) failures.Add($"畫面上只有 {tiles.Count} 格，預期 18 格。");

                // 底色＝溫度：第一顆（60 °C）應該拿到色階上 60 °C 的顏色，不是卡片底色
                if (tiles.Count > 0 && tiles[0].Background is SolidColorBrush b)
                {
                    if (b.Color != HeatScale.ColorFor(60)) failures.Add($"第一格底色 {b.Color}，預期 {HeatScale.ColorFor(60)}。");
                }
                else failures.Add("第一格沒有底色——溫度的顏色沒畫上去。");

                if (!map.SummaryText.Contains("實體核心 ×18")) failures.Add("統計列沒說有幾顆實體核心。");
                if (!map.SummaryText.Contains("讀值 18/18 格")) failures.Add($"統計列沒講涵蓋率：{map.SummaryText}");
                if (!map.SummaryText.Contains("最熱核心")) failures.Add("統計列沒指出最熱是哪一顆。");

                foreach (var line in listener.Drain().Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    if (t.Contains("BindingExpression path error") || t.Contains("Cannot convert") ||
                        t.Contains("Cannot find governing FrameworkElement"))
                        failures.Add("繫結錯誤：" + t);
                }
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        });
    }

    [Fact]
    public void 感測器只回八顆_仍然畫十八格且缺的如實留白()
    {
        OnSta(() =>
        {
            var app = WpfEnv.Ensure();
            var map = Layout(new CoreHeatmap { Cores = Rows(8), PhysicalCores = 18 }, 900);

            Assert.Equal(18, map.Cells.Count);                      // 只補不裁
            Assert.Equal(18, Tiles(map).Count);                     // 畫面上真的 18 格
            Assert.All(map.Cells.Take(8), c => Assert.True(c.HasReading));
            Assert.All(map.Cells.Skip(8), c =>
            {
                Assert.False(c.HasReading);
                Assert.Null(c.Fill);                                // 沒有挑一個「看起來像資料」的顏色
                Assert.Equal("—", c.TempText);
            });

            Assert.Contains("讀值 8/18 格", map.SummaryText);
            Assert.Contains("實體核心 ×18", map.SummaryText);
        });
    }

    [Fact]
    public void 讀值比拓樸多_不裁掉資料並在統計列說明白()
    {
        OnSta(() =>
        {
            var app = WpfEnv.Ensure();
            var map = Layout(new CoreHeatmap { Cores = Rows(36), PhysicalCores = 18 });

            Assert.Equal(36, map.Cells.Count);                      // 有讀值的格永遠不因拓樸數字被吃掉
            Assert.Contains("讀值格數多於拓樸實體核心數", map.SummaryText);
        });
    }

    [Fact]
    public void 沒有逐核感測器_不畫格子也不假裝有溫度()
    {
        OnSta(() =>
        {
            var app = WpfEnv.Ensure();
            var map = Layout(new CoreHeatmap { Cores = new ObservableCollection<CoreRow>() });

            Assert.Empty(map.Cells);
            Assert.False(map.HasCells);
            Assert.Equal("", map.SummaryText);
            Assert.Empty(Tiles(map));
        });
    }

    [Fact]
    public void 集合就地新增_格子跟著長出來()
    {
        OnSta(() =>
        {
            var app = WpfEnv.Ensure();
            var source = new ObservableCollection<CoreRow>(Rows(4));
            var map = Layout(new CoreHeatmap { Cores = source, PhysicalCores = 4 });
            Assert.Equal(4, map.Cells.Count);

            source.Add(new CoreRow("核心 #5") { TempC = 61 });
            map.UpdateLayout();

            Assert.Equal(5, map.Cells.Count);
            Assert.Equal(5, Tiles(map).Count);
        });
    }
}
