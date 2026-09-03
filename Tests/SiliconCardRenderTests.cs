using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「有資料時」的樣板也要驗一次。
///
/// <para>
/// UI 煙霧測試逼所有分頁 Measure／Arrange，攔得到繫結路徑錯誤——但那只涵蓋<b>當下真的被建出來的
/// 視覺元素</b>。<c>ItemsControl</c> 綁在空集合上時，它的 <c>ItemTemplate</c> 根本不會被具體化，
/// 樣板裡的每一個繫結（打錯的屬性名、拼錯的轉換器）因此完全沒被求值。1.9.0 的體質評分卡有兩張
/// 這樣的表——結果表與 V/F 工作點表——兩者平常都是空的，正好落在那個盲區裡。
/// </para>
/// <para>
/// 做法：把樣本資料塞進那兩個集合，把整頁算繪進一張離屏點陣圖（算繪才會真的具體化樣板與跑
/// <c>OnRender</c>），同時用 <see cref="PresentationTraceSources"/> 攔繫結錯誤。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class SiliconCardRenderTests
{
    private sealed class CollectingListener : TraceListener
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string? message) => _sb.Append(message);
        public override void WriteLine(string? message) => _sb.AppendLine(message);
        public string Text => _sb.ToString();
    }

    [Fact]
    public void 體質評分的結果表與工作點表在有資料時不會有繫結錯誤()
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

            // 兩張表各塞兩列樣本；欄位刻意涵蓋「有值」與「讀不到」兩種情形
            var oc = vm.Overclock;
            oc.SiliconMetrics.Add(new SiliconMetric("重心工作點 f̄ ／ V̄", "4.000 GHz ／ 1.0200 V", "測試樣本"));
            oc.SiliconMetrics.Add(new SiliconMetric("同頻電壓落差 ΔV", "-40.0 mV ± 0.5", "測試樣本"));
            oc.SiliconPoints.Add(new VfPoint(1, 4.4, 1.0255, 62, 95, 37));
            oc.SiliconPoints.Add(new VfPoint(18, 3.6, 0.9545, null, null, 4));   // 溫度／功耗讀不到

            var view = new OverclockView { DataContext = vm };
            view.Measure(new Size(1280, 2400));
            view.Arrange(new Rect(0, 0, 1280, 2400));
            view.UpdateLayout();

            // 算繪才會真的具體化 ItemTemplate 並跑自繪元件的 OnRender
            var bmp = new RenderTargetBitmap(1280, 2400, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(view);

            // 自我驗證：樣板真的被具體化了嗎？沒有的話這條測試等於什麼都沒驗。
            int realised = CountVisual<System.Windows.Controls.TextBlock>(view,
                tb => tb.Text.Contains("重心工作點") || tb.Text.Contains("4.400 GHz"));
            if (realised == 0)
                problems.Add("樣板沒有被具體化：這條測試沒有驗到任何東西（檢查集合是否真的餵進去了）。");
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

    /// <summary>在視覺樹裡數符合條件的元素（用來確認樣板真的被建出來了）。</summary>
    private static int CountVisual<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int n = 0;
        if (root is T t && match(t)) n++;
        int kids = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < kids; i++) n += CountVisual(VisualTreeHelper.GetChild(root, i), match);
        return n;
    }
}
