using System.Diagnostics;
using System.Text;
using System.Windows;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// UI 煙霧測試（ROADMAP 第一部第 1 項）：逐一「真的建構」所有導覽頁、實用工具子頁與五個全螢幕檢測視窗，
/// 並以 Measure／Arrange 逼繫結求值，再由 <see cref="PresentationTraceSources"/>.DataBindingSource
/// 攔下繫結路徑錯誤與轉換失敗——從根上斷掉「XAML 繫結寫錯、進頁面才閃退」這個反覆發生的失敗模式。
/// </summary>
/// <remarks>
/// ⚠ 本類是「測試不建構 MainViewModel」慣例的唯一例外：繫結路徑必須對著真的屬性形狀求值才有意義，
/// 手工假物件維護成本高且會隨介面演進爛掉。安全性論證——這裡建構的是<b>未呼叫 Initialize()</b> 的
/// MainViewModel：StartupSequence／MetricsPump 不會啟動，因此 <b>SensorService／LibreHardwareMonitor
/// 不會被建立、不碰任何硬體</b>；GpuOcService 的 NVML／NVAPI 亦為延遲初始化。副作用僅限讀取
/// %APPDATA% 的既有狀態檔，與 WingetService 開機時同款的背景 winget 偵測（唯讀）。
///
/// WPF 需要 STA 執行緒與 Application 執行個體（StaticResource 解析用），故全部工作排入一條專屬
/// STA 執行緒，其餘純函式測試不受影響。視窗類別只建構不 Show()，Loaded 事件在測試中不會觸發。
/// </remarks>
public class UiSmokeTests
{
    /// <summary>把 DataBindingSource 的輸出原樣收集成字串的監聽器。</summary>
    private sealed class CollectingListener : TraceListener
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string? message) => _sb.Append(message);
        public override void WriteLine(string? message) => _sb.AppendLine(message);
        public string Drain() { var s = _sb.ToString(); _sb.Clear(); return s; }
    }

    [Fact]
    public void 所有導覽頁_實用工具與全螢幕視窗皆可建構且無繫結錯誤()
    {
        var failures = new List<string>();
        var thread = new Thread(() => Run(failures));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // 逾時保護：任何檢視在建構時卡死（如等待網路）都不得讓整個測試回合無限等下去
        if (!thread.Join(TimeSpan.FromMinutes(3)))
            failures.Add("煙霧測試逾時（3 分鐘未完成），可能有檢視在建構時阻塞。");

        Assert.True(failures.Count == 0,
            $"UI 煙霧測試發現 {failures.Count} 項問題：\n" + string.Join("\n", failures));
    }

    private static void Run(List<string> failures)
    {
        // WPF 環境：StaticResource 解析需要 Application 與佈景資源字典（合併內容與 App.xaml 相同）
        var app = Application.Current;
        if (app is null)
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/XinSpect;component/Themes/Theme.xaml"),
            });
        }

        PresentationTraceSources.Refresh();
        var listener = new CollectingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        try
        {
            var vm = new MainViewModel();   // 唯一例外（見類別註解）；絕不呼叫 Initialize()

            // ── 自我驗證 ──────────────────────────────────────────────
            // 刻意綁一個不存在的路徑，確認監聽器真的攔得到路徑錯誤——
            // 否則這套煙霧測試可能靜默失效（抓不到東西以為一切正常），那比沒有測試更糟。
            var probe = new System.Windows.Controls.TextBlock { DataContext = vm };
            probe.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("絕對不存在的屬性路徑"));
            probe.Measure(new Size(10, 10));
            probe.Arrange(new Rect(0, 0, 10, 10));
            if (!listener.Drain().Contains("BindingExpression path error"))
            {
                failures.Add("監聽器自我驗證失敗：刻意製造的繫結路徑錯誤未被攔到，煙霧測試形同失效。");
                return;
            }

            var targets = new List<(string Name, Func<FrameworkElement> Make)>();
            foreach (var d in PageRegistry.Pages)
                targets.Add(($"頁面「{d.Title}」", d.Factory));
            foreach (var d in PageRegistry.Utilities)
                targets.Add(($"工具「{d.Title}」", d.Factory));
            targets.Add(("視窗「ScreenTestWindow」", () => new ScreenTestWindow()));
            targets.Add(("視窗「MouseTestWindow」", () => new MouseTestWindow()));
            targets.Add(("視窗「KeyboardTestWindow」", () => new KeyboardTestWindow()));
            targets.Add(("視窗「SpeakerTestWindow」", () => new SpeakerTestWindow()));
            targets.Add(("視窗「MotionTestWindow」", () => new MotionTestWindow()));

            foreach (var (name, make) in targets)
            {
                try
                {
                    var fe = make();
                    fe.DataContext = vm;
                    // 多數繫結要等版面配置才求值；給一個常見桌面尺寸逼它們真的跑一次
                    fe.Measure(new Size(1280, 800));
                    fe.Arrange(new Rect(0, 0, 1280, 800));
                }
                catch (Exception ex)
                {
                    failures.Add($"{name} 建構／量測失敗：{ex.GetType().Name}：{ex.Message}");
                }
            }

            // 三類致訊號（ROADMAP 指定）：路徑錯誤、轉換失敗、找不到宿主元素
            foreach (var line in listener.Drain().Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("BindingExpression path error") ||
                    t.Contains("Cannot convert") ||
                    t.Contains("Cannot find governing FrameworkElement"))
                    failures.Add("繫結錯誤：" + t);
            }
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
}
