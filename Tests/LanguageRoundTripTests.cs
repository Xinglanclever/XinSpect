using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 2026-10-08 使用者回報「簡體模式夾英文、英文模式夾繁中」的回歸釘子。
/// 根因一（簡體夾英文）：英語模式下 T() 寫進控制項的英文字串，被 FromOriginal 當「原文」存槽，
/// 之後切簡體時原樣卡英文。修法：存槽前先 EnglishStrings.Reverse 反查回繁中鍵；
/// 簡體路徑的 T() 同樣先 Reverse 再 ToSimplified。
/// 根因二（英文夾繁中）：翻譯表缺口（本輪補 80 條）與檔案對話框 Filter 整串過 T() 查無此鍵
/// （改為逐段翻顯示名）。
/// 本類會切 LanguageService 的靜態語言旗標，必須與其他 WPF 測試同列序列化 collection，
/// 否則與 EraCalendarTests（讀同一旗標決定繁／英格式）平行時互踩。
/// </summary>
[Collection(WpfCollection.Name)]
public class LanguageRoundTripTests
{
    [Fact]
    public void 英語模式寫入的文字切簡體不得卡英文()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            // 模擬真實順序：英語模式下 T() 產生英文字串 → 寫進控制項 → ConvertVisualTree 存槽 → 切簡體轉換。
            LanguageService.SetEnglishForTests(true);
            try
            {
                string en = LanguageService.T("顯示主視窗");   // T() 產物："Show main window"
                Assert.Equal("Show main window", en);

                var btn = new Button { Content = en };
                var win = Host(btn);
                win.Show(); Pump();

                LanguageService.SetEnglishForTests(false);
                LanguageService.ConvertVisualTree(win, simplified: true);   // 切簡體
                Assert.Equal("显示主窗口", (string)btn.Content);            // 不得停在英文
                win.Close();
            }
            finally { LanguageService.SetEnglishForTests(false); }
        });
    }

    [Fact]
    public void 英語模式寫入的未收錄字串切簡體應轉為簡體()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            // 未收錄字串：英語模式原樣（繁中）→ 存槽原樣 → 簡體模式照常 LCMapStringEx 轉換。
            LanguageService.SetEnglishForTests(true);
            try
            {
                var tb = new TextBlock { Text = "這串沒有收錄在翻譯表" };
                var win = Host(tb);
                win.Show(); Pump();

                LanguageService.SetEnglishForTests(false);
                LanguageService.ConvertVisualTree(win, simplified: true);
                Assert.Equal("这串没有收录在翻译表", tb.Text);
                win.Close();
            }
            finally { LanguageService.SetEnglishForTests(false); }
        });
    }

    [Fact]
    public void Reverse與Lookup必須互為反函數_抽樣()
    {
        // 修正機制的地基：Reverse(英文) 回繁中鍵，Lookup(繁中鍵) 回同一英文。
        string[] samples = { "Show main window", "Memory", "Utilities", "Overview", "Executable (*.exe)", "Waiting for first scan…" };
        foreach (var en in samples)
        {
            var zh = EnglishStrings.Reverse(en);
            Assert.True(zh is not null, $"Reverse 查不到：{en}");
            Assert.Equal(en, EnglishStrings.Lookup(zh!));
        }
        Assert.Null(EnglishStrings.Reverse("definitely-not-a-translation"));
    }

    [Fact]
    public void T_英語後切簡體_經T也要轉換()
    {
        // 不經視覺樹、直接 T() 的路徑（HelpDot、TrayService 等 143 個呼叫點）。
        LanguageService.SetEnglishForTests(true);
        try
        {
            string en = LanguageService.T("顯示主視窗");
            LanguageService.SetSimplifiedForTests(true);
            string simp = LanguageService.T(en);            // 英文字串餵給簡體模式的 T()
            Assert.Equal("显示主窗口", simp);                // 不得原樣卡英文
        }
        finally { LanguageService.SetSimplifiedForTests(false); LanguageService.SetEnglishForTests(false); }
    }

    [Fact]
    public void 檔案對話框Filter逐段翻譯後的模式段必須保持可解析()
    {
        // Filter 格式契約：顯示名|模式 成對。翻譯只動顯示名，模式段 *.exe / *.* 原樣。
        string filter = LanguageService.T("可執行檔 (*.exe)") + "|*.exe|" + LanguageService.T("所有檔案 (*.*)") + "|*.*";
        var segs = filter.Split('|');
        Assert.Equal(4, segs.Length);
        Assert.Equal("*.exe", segs[1]);
        Assert.Equal("*.*", segs[3]);
    }

    [Fact]
    public void 有繫結的TextBlock轉換後繫結必須存活且跟隨來源()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            // 2026-10-08 修的 1,015 處：繫結的隱式 Run 被舊碼直寫蓋掉繫結，
            // 文字凍在轉換當下的那一版（時鐘、狀態列全死）。三種模式都不得再發生。
            var src = new ProbeSrc { T = "記憶體" };
            var tb = new TextBlock();
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ProbeSrc.T)) { Source = src });
            var win = Host(tb);
            win.Show(); Pump();
            try
            {
                foreach (var (eng, simp) in new[] { (false, false), (false, true), (true, false) })
                {
                    LanguageService.SetEnglishForTests(eng);
                    LanguageService.ConvertVisualTree(win, simp);
                    Pump();
                    Assert.True(tb.GetBindingExpression(TextBlock.TextProperty) is not null,
                        $"繫結被蓋掉（eng={eng}, simp={simp}）");
                }
                LanguageService.SetEnglishForTests(false);
                src.T = "顯示卡"; Pump();
                Assert.Equal("顯示卡", tb.Text);   // 來源改值要跟著動
            }
            finally { LanguageService.SetEnglishForTests(false); win.Close(); }
        });
    }

    private sealed class ProbeSrc : System.ComponentModel.INotifyPropertyChanged
    {
        private string _t = "";
        public string T { get => _t; set { _t = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(T))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "語言往返測試逾時");
        if (error is not null) throw error;
    }

    private static Window Host(params FrameworkElement[] children)
    {
        var panel = new System.Windows.Controls.StackPanel();
        foreach (var c in children) panel.Children.Add(c);
        return new Window { Content = panel, Width = 200, Height = 100, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
    }

    private static void Pump()
        => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
}
