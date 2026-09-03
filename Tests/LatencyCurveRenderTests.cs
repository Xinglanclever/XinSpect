using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 記憶體延遲曲線的兩張表——邊界對照表與偏差可信度表——在<b>有資料時</b>要算得出來，
/// 而且偏差欄的顏色要真的隨可信度變化。
///
/// <para>
/// 這條測試的來歷是一個實際打掉整頁的錯誤：偏差表的轉換器寫成
/// <c>{StaticResource SeverityToBrushConverter}</c>（那是 C# 類別名稱，登記的鍵叫
/// <c>SeverityToBrush</c>），<c>StaticResource</c> 找不到鍵就丟例外，被 XAML 包成
/// <c>XamlParseException</c>。從 1.7.6 一路帶到 1.8.2 都沒被抓到，因為兩張表平常都是空的，
/// <c>ItemTemplate</c> 根本不會被具體化——和 <see cref="SiliconCardRenderTests"/> 同一個盲區。
/// </para>
/// <para>
/// 顏色那一段是另一個獨立的缺陷：資料列原本給的是字串 <c>"Good"</c>／<c>"Warning"</c>，
/// 而轉換器只認 <see cref="Severity"/> 列舉，其他一律回中性灰。也就是說就算鍵名改對了，
/// 那一欄還是永遠灰的——而可信度在畫面上唯一的呈現就是這個顏色。所以這裡不只驗「畫得出來」，
/// 還要驗「三個可信度真的畫成三個不同的顏色」。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class LatencyCurveRenderTests
{
    private sealed class CollectingListener : TraceListener
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string? message) => _sb.Append(message);
        public override void WriteLine(string? message) => _sb.AppendLine(message);
        public string Text => _sb.ToString();
    }

    [Fact]
    public void 延遲曲線的邊界表與偏差表在有資料時算得出來且顏色依可信度變化()
    {
        var problems = new List<string>();
        var thread = new Thread(() => Run(problems));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "算繪逾時");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static void Run(List<string> problems)
    {
        var listener = new CollectingListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            WpfEnv.Ensure();
            var vm = new MainViewModel();          // 與煙霧測試同一個慣例例外：不呼叫 Initialize()

            var lc = vm.LatencyCurve;
            lc.BoundaryRows.Add(new LatencyBoundaryRow("L1d 38 KB", "CPUID 宣稱 L1d 32 KB"));
            lc.BoundaryRows.Add(new LatencyBoundaryRow("L3 45.0 MB", "未配對"));
            // 三列刻意各落在一個可信度級距：<20% 高、20–50% 中、>50% 低
            lc.DeviationRows.Add(new LatencyDeviationRow("L1d", "38 KB", "32 KB", "+18.8%", DeviationConfidence.High));
            lc.DeviationRows.Add(new LatencyDeviationRow("L2", "1.4 MB", "1.0 MB", "+40.0%", DeviationConfidence.Medium));
            lc.DeviationRows.Add(new LatencyDeviationRow("L3", "45.0 MB", "24.8 MB", "+81.5%", DeviationConfidence.Low));

            var view = new BenchView { DataContext = vm };
            view.Measure(new Size(1280, 4000));
            view.Arrange(new Rect(0, 0, 1280, 4000));
            view.UpdateLayout();

            // 算繪才會真的具體化 ItemTemplate
            var bmp = new RenderTargetBitmap(1280, 4000, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(view);

            // 自我驗證：樣板真的被具體化了嗎？沒有的話這條測試等於什麼都沒驗。
            if (FindByText(view, "L1d 38 KB") is null)
                problems.Add("邊界表沒有被具體化：這條測試沒有驗到那張表。");

            CheckColour(view, "+18.8%", Severity.Good, problems);
            CheckColour(view, "+40.0%", Severity.Warning, problems);
            CheckColour(view, "+81.5%", Severity.Critical, problems);

            // 三個可信度必須畫成三個不同的顏色；全部相同就是又退回中性灰了
            var colours = new[] { "+18.8%", "+40.0%", "+81.5%" }
                .Select(t => FindByText(view, t)?.Foreground as SolidColorBrush)
                .Where(b => b is not null)
                .Select(b => b!.Color)
                .Distinct()
                .Count();
            if (colours != 3)
                problems.Add($"偏差欄只出現 {colours} 種顏色——三個可信度應該是三種顏色。"
                           + "轉換器拿不到 Severity 時會一律回中性灰，繫結的屬性型別要對得上。");
        }
        catch (Exception ex)
        {
            problems.Add("算繪失敗：" + ex);
        }
        finally
        {
            foreach (var line in listener.Text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("BindingExpression path error") ||
                    t.Contains("Cannot convert") ||
                    t.Contains("Cannot find governing FrameworkElement"))
                    problems.Add("繫結錯誤：" + t);
            }
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    /// <summary>比對某一列偏差文字的前景色，是否就是該可信度對應的狀態色。</summary>
    private static void CheckColour(DependencyObject root, string text, Severity expected, List<string> problems)
    {
        var tb = FindByText(root, text);
        if (tb is null)
        {
            problems.Add($"偏差表找不到「{text}」那一列：樣板沒被具體化，顏色也就沒驗到。");
            return;
        }
        if (tb.Foreground is not SolidColorBrush actual)
        {
            problems.Add($"「{text}」的前景不是純色筆刷：{tb.Foreground?.GetType().Name ?? "null"}。");
            return;
        }
        var want = SeverityToBrushConverter.Brush(expected).Color;
        if (actual.Color != want)
            problems.Add($"「{text}」的顏色是 {actual.Color}，應該是 {expected} 的 {want}。");
    }

    /// <summary>在視覺樹裡找出文字完全相符的第一個 <see cref="TextBlock"/>。</summary>
    private static TextBlock? FindByText(DependencyObject root, string text)
    {
        if (root is TextBlock tb && tb.Text == text) return tb;
        int kids = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < kids; i++)
        {
            var hit = FindByText(VisualTreeHelper.GetChild(root, i), text);
            if (hit is not null) return hit;
        }
        return null;
    }
}
