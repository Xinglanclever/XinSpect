using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 外觀切換（深／淺主題與八色強調色）真的要換到已經在畫面上的東西。
///
/// 這一份存在的理由：ThemeService 的做法是「就地改寫共用筆刷的 .Color」，讓整棵已載入的視覺樹
/// 同步換色。但 WPF 從編譯後的 BAML 載入 <c>ResourceDictionary</c> 時會把裡面的 Freezable
/// <b>自動凍結</b>，凍結後改不動——於是 <see cref="ThemeService.ApplyAll"/> 整組 Set 全部靜靜落空，
/// 淺色主題與另外七個強調色從加進來的那天起就沒有生效過（深色＋曦藍剛好等於 Theme.xaml 的字面值，
/// 所以看起來「正常」）。這種失敗不會拋例外、不會有繫結錯誤，只能靠斷言筆刷可改與像素真的變了來守。
/// </summary>
public class ThemeSwitchTests
{
    /// <summary>ThemeService 會就地改寫的所有佈景資源鍵。</summary>
    private static readonly string[] BrushKeys =
    [
        "PagePlaneBrush", "SurfaceBrush", "Surface2Brush",
        "PrimaryInkBrush", "SecondaryInkBrush", "MutedInkBrush",
        "HairlineBrush", "BaselineBrush", "AccentBrush", "AccentDimBrush", "AccentInkBrush",
        "GoodBrush", "WarningBrush", "SeriousBrush", "CriticalBrush",
        "AccentGradientBrush", "HeaderGradientBrush",
    ];

    [Fact]
    public void 所有佈景色一律以DynamicResource取用()
    {
        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(RepoRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
                foreach (string key in BrushKeys)
                    if (lines[i].Contains($"StaticResource {key}"))
                        offenders.Add($"{Path.GetFileName(path)}:{i + 1} → {key}");
        }

        Assert.True(offenders.Count == 0,
            "佈景色若以 StaticResource 取用，換主題／強調色時不會更新（換色的做法是整支換掉資源項目，"
            + "而放進資源字典的筆刷會被 WPF 凍結、改不動）。請改為 DynamicResource：\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void 切換主題與強調色時筆刷顏色真的改變()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            ThemeService.Initialize();

            ThemeService.Theme = AppTheme.Dark;
            var darkSurface = ColorOf("SurfaceBrush");
            var darkInk = ColorOf("PrimaryInkBrush");

            ThemeService.Theme = AppTheme.Light;
            Assert.NotEqual(darkSurface, ColorOf("SurfaceBrush"));
            Assert.NotEqual(darkInk, ColorOf("PrimaryInkBrush"));

            ThemeService.Accent = ThemeService.FindAccent("blue");
            var blue = ColorOf("AccentBrush");
            var blueGrad = GradientTopOf("AccentGradientBrush");
            var blueHeader = GradientTopOf("HeaderGradientBrush");

            ThemeService.Accent = ThemeService.FindAccent("crimson");
            Assert.NotEqual(blue, ColorOf("AccentBrush"));
            Assert.NotEqual(blueGrad, GradientTopOf("AccentGradientBrush"));
            Assert.NotEqual(blueHeader, GradientTopOf("HeaderGradientBrush"));
        });
    }

    /// <summary>
    /// 端到端：<b>同一棵</b>已經顯示中的視覺樹，切換外觀後重繪的像素必須真的不一樣。
    /// 只驗資源項目換掉了不夠——DynamicResource 的失效通知只會送到真的掛在視窗裡的樹，
    /// 所以這裡必須把頁面放進一個（挪到畫面外的）Window 並 Show 出來，才是實際情境。
    /// </summary>
    [Fact]
    public void 已載入的頁面切換外觀後重繪結果不同()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            ThemeService.Initialize();
            ThemeService.Theme = AppTheme.Dark;
            ThemeService.Accent = ThemeService.FindAccent("blue");

            var page = PageRegistry.Find("about")!.Factory();
            var host = new System.Windows.Controls.Border
            {
                Background = (Brush)Application.Current.Resources["PagePlaneBrush"],
                Child = page, Width = 1000, Height = 700,
            };
            var win = new Window
            {
                Content = host, Width = 1020, Height = 740,
                Left = -4000, Top = -4000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            };
            try
            {
                win.Show();
                Pump();
                byte[] dark = Snapshot(host);

                ThemeService.Theme = AppTheme.Light;
                ThemeService.Accent = ThemeService.FindAccent("crimson");
                Pump();
                byte[] light = Snapshot(host);

                int diff = 0;
                for (int i = 0; i < dark.Length; i++) if (dark[i] != light[i]) diff++;
                double pct = 100.0 * diff / dark.Length;
                Assert.True(pct > 20,
                    $"切換主題與強調色後，同一棵視覺樹重繪只有 {pct:0.0}% 的位元組不同——"
                    + "畫面等於沒換色（預期整片底色與文字都會變，遠超 20%）。");
            }
            finally { win.Close(); }
        });
    }

    /// <summary>
    /// 自繪控制項（<c>OnRender</c> 裡直接拿 <see cref="VizPalette"/> 的筆刷畫）換外觀後必須重畫。
    /// 資源筆刷是凍結的，已經畫進視覺內容的那一支不會自己變色——不重畫的話，
    /// 不動的自繪元件會一直停在舊配色（淺色主題下就成了白底白字）。
    /// 這裡刻意把容器底色設成與佈景無關的固定灰，讓像素差異只可能來自元件本身重畫。
    /// </summary>
    [Fact]
    public void 自繪控制項換外觀後也重畫()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            ThemeService.Initialize();
            ThemeService.Theme = AppTheme.Dark;

            var digits = new OutlineDigits { Text = "0123456789", DigitHeight = 56 };
            var host = new System.Windows.Controls.Border
            {
                Background = Brushes.Gray, Child = digits, Width = 600, Height = 120,
            };
            var win = new Window
            {
                Content = host, Width = 620, Height = 160,
                Left = -4000, Top = -4000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            };
            try
            {
                win.Show();
                Pump();
                byte[] dark = Snapshot(host);

                ThemeService.Theme = AppTheme.Light;
                Pump();
                byte[] light = Snapshot(host);

                int diff = 0;
                for (int i = 0; i < dark.Length; i++) if (dark[i] != light[i]) diff++;
                double pct = 100.0 * diff / dark.Length;
                Assert.True(pct > 0.5,
                    $"OutlineDigits 在換主題後只有 {pct:0.00}% 的位元組不同——自繪內容沒有重畫，"
                    + "仍停在舊的墨色（應由 ThemeAware 訂閱 ThemeService.Changed 觸發重繪）。");
            }
            finally { win.Close(); }
        });
    }

    /// <summary>讓版面配置、Loaded 事件與資源失效通知真的跑完。</summary>
    private static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Thread.Sleep(40);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "XinSpect.csproj"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }

    // ── 工具 ──────────────────────────────────────────────────────────────

    private static Color ColorOf(string key)
        => ((SolidColorBrush)Application.Current.Resources[key]!).Color;

    private static Color GradientTopOf(string key)
        => ((LinearGradientBrush)Application.Current.Resources[key]!).GradientStops[0].Color;

    private static byte[] Snapshot(FrameworkElement el)
    {
        var rtb = new RenderTargetBitmap(
            (int)el.Width, (int)el.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        int stride = rtb.PixelWidth * 4;
        var buf = new byte[stride * rtb.PixelHeight];
        rtb.CopyPixels(buf, stride, 0);
        return buf;
    }

    /// <summary>
    /// WPF 需要 STA 執行緒。<b>並且</b>：ThemeService 的 setter 會把偏好寫進
    /// %APPDATA%\XinSpect\appearance.json——那是使用者真正的外觀設定，測試不得留下痕跡，
    /// 故備份原檔內容並於結束後原樣寫回。
    /// </summary>
    private static void RunSta(Action body)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XinSpect", "appearance.json");
        string? backup = File.Exists(path) ? File.ReadAllText(path) : null;

        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally
            {
                try
                {
                    if (backup is not null) File.WriteAllText(path, backup);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch { /* 還原失敗不該掩蓋測試本身的結果 */ }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "外觀切換測試逾時");
        if (error is not null) throw error;
    }
}
