using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 2026-10-07 修復的英語模式契約：視覺樹轉換必須涵蓋英語（翻譯表、未收錄回繁中原文）、
/// 與繁簡同一條「永遠從保存原文出發」的規則——繁↔英↔簡往返可逆、冪等，不會漂移。
/// WPF 需 STA，掛 <see cref="WpfCollection"/>。
/// </summary>
[Collection(WpfCollection.Name)]
public class EnglishTreeTests
{
    [Fact]
    public void 英語模式_收錄字串轉英文_未收錄保留繁中()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            var mapped = new TextBlock { Text = "總覽" };      // 導覽標題，已收錄
            var unmapped = new TextBlock { Text = "xyz未收錄xyz" };
            var win = Host(mapped, unmapped);
            LanguageService.SetEnglishForTests(true);
            try
            {
                win.Show(); Pump();
                LanguageService.ConvertVisualTree(win, false);  // 英語分支由 _isEnglish 決定
                Assert.Equal("Overview", mapped.Text);
                Assert.Equal("xyz未收錄xyz", unmapped.Text);   // 未收錄＝繁中原文，不是空字串
                // 再轉一次仍冪等（永遠從原文出發，不會在譯文上再轉）
                LanguageService.ConvertVisualTree(win, false);
                Assert.Equal("Overview", mapped.Text);
            }
            finally
            {
                LanguageService.SetEnglishForTests(false);
                win.Close();
            }
        });
    }

    [Fact]
    public void 英繁簡三向往返不得漂移()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            var tb = new TextBlock { Text = "暫存" };   // 「暫存」在翻譯表外，英語保留繁中原文
            var mapped = new Button { Content = "記憶體" }; // 已收錄
            var win = Host(tb, mapped);
            LanguageService.SetEnglishForTests(false);
            try
            {
                win.Show(); Pump();
                for (int i = 0; i < 3; i++)
                {
                    LanguageService.SetEnglishForTests(true);
                    LanguageService.ConvertVisualTree(win, false);   // 繁→英
                    Assert.Equal("暫存", tb.Text);
                    Assert.Equal("Memory", mapped.Content);
                    LanguageService.SetEnglishForTests(false);
                    LanguageService.ConvertVisualTree(win, true);    // →簡
                    Assert.Equal("缓存", tb.Text);   // 「暫存」經詞組表轉簡體是「缓存」（與 LanguageTreeTests 同一口徑）
                    Assert.Equal("内存", (string)mapped.Content);
                    LanguageService.ConvertVisualTree(win, false);   // →繁（還原原文）
                    Assert.Equal("暫存", tb.Text);
                    Assert.Equal("記憶體", (string)mapped.Content);
                }
            }
            finally { win.Close(); }
        });
    }

    [Fact]
    public void 翻譯表覆蓋XAML收割字串_抽樣釘住()
    {
        // 從 XAML 收割的高頻字串抽樣：都必須查得到英文
        foreach (var zh in new[] { "⟳ 全面掃描", "開始跑分", "溫度上限", "記憶體頻寬", "取消", "套用",
                                   "執行緒", "原始值", "還原自動", "韌體安全",
                                   "掃描中……", "做法：{0}", "結束 曦覽", "歡迎使用曦覽", "剩餘" })
            Assert.True(EnglishStrings.Lookup(zh) is not null, $"缺翻譯：{zh}");
        // 未收錄回 null（呼叫方回退原文）
        Assert.Null(EnglishStrings.Lookup(" definitely-not-in-table "));
    }

    [Fact]
    public void 英語翻譯表涵蓋所有導覽頁與工具的標題分組和提示()
    {
        foreach (var page in PageRegistry.Pages.Concat(PageRegistry.Utilities))
        {
            Assert.True(EnglishStrings.Lookup(page.Title) is not null,
                $"Missing English page title: {page.Key} / {page.Title}");
            Assert.True(EnglishStrings.Lookup(page.Group) is not null,
                $"Missing English navigation group: {page.Key} / {page.Group}");
            if (!string.IsNullOrWhiteSpace(page.Hint))
                Assert.True(EnglishStrings.Lookup(page.Hint) is not null,
                    $"Missing English page hint: {page.Key} / {page.Hint}");
        }
    }

    [Fact]
    public void 英語翻譯表涵蓋常見操作與AI介面文案()
    {
        foreach (var text in new[]
        {
            "上一個", "下一個", "傳送內容", "匯出對話", "載入上次規劃", "儲存這份規劃",
            "測試連線（只查模型清單）", "重新載入本輪事實", "開啟百科（可翻閱）", "頁內尋找",
            "送出前遮蔽可識別資訊", "代理模式每輪暫停顯示送出內容", "代理模式的工具結果也過遮蔽",
            "這個頁面沒有載入成功", "本版未接入", "唯讀硬體事實 ・ 讀得到才報，讀不到就說讀不到",
            "磁碟健康狀況如何？請查磁碟健康與剩餘壽命，若有風險請說明該做什麼。",
            "這台機器的溫度正常嗎？請查所有溫度、風扇現況與歷史統計，判斷有沒有過熱或降頻。",
            "這套配置玩遊戲夠用嗎？請查規格與顯示卡讀值，說明大概能對應什麼畫質設定。",
            "時脈 {0}", "記憶體 {0}", "目前存放方式：{0}", "Rank：{0}",
            "SECTION 01 // 即時遙測（本機現值）", "SECTION 05 // 執行檔 SHA-256 對帳（本機現算）",
            "以下雜湊是開啟這一頁當場對本機檔案計算的 SHA-256；比對對象是 GitHub Release 的 SHA256SUMS.txt。",
            "伺服器角色與選用功能只要安裝了就佔三樣成本：磁碟空間、開機與維護時間，以及可被利用的程式碼面積。沒在用的角色不會因為「沒開」就沒有風險——檔案在、服務定義在、更新要跟著裝。本區只陳述裝了什麼與服務狀態，不建議移除任何項目。",
            "域＝鍵的第一段（ac.dmar.summary 的域是 ac）。「本輪量到」只算真的讀到值的那幾把。",
        })
            Assert.True(EnglishStrings.Lookup(text) is not null, $"Missing English UI translation: {text}");
    }

    [Fact]
    public void 下拉選單項目也要轉_不是只有主視窗()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            var combo = new ComboBox();
            combo.Items.Add(new ComboBoxItem { Content = "記憶體" });
            combo.Items.Add(new ComboBoxItem { Content = "未收錄字串" });
            var win = Host(combo);
            try
            {
                win.Show(); Pump();
                LanguageService.SetEnglishForTests(true);
                LanguageService.ConvertVisualTree(win, false);
                Assert.Equal("Memory", ((ComboBoxItem)combo.Items[0]!).Content);
                Assert.Equal("未收錄字串", ((ComboBoxItem)combo.Items[1]!).Content);
            }
            finally
            {
                LanguageService.SetEnglishForTests(false);
                win.Close();
            }
        });
    }

    [Fact]
    public void 英語模式的紀年清單要換成英文且與模式對齊()
    {
        var names = EraCalendar.GetNamesEnglish();
        Assert.Equal(5, names.Length);                       // 與繁體同長度（含民國）
        Assert.Equal("Gregorian", names[0]);
        Assert.Equal(names.Length, EraCalendar.GetNames(false).Length);
        // 英語清單走「非簡體」的索引映射，所以選民國仍要套到民國
        Assert.Equal(EraMode.Minguo, EraCalendar.FromIndex(simplified: false, names.Length - 4));
    }

    [Fact]
    public void 格式字串轉換器_英語查表_繁簡照轉_未收錄回原文()
    {
        // 模板以 ConverterParameter 傳入，轉換器先翻譯模板再套 string.Format。
        // 這是 StringFormat 在轉換器之後才執行所造成的缺口（Binding 產生的字串以前都不轉）。
        var conv = LangFormatConverter.Instance;

        LanguageService.SetEnglishForTests(true);
        try
        {
            Assert.Equal("8 cores", conv.Convert(8, typeof(string), "{0} 核", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("12 s", conv.Convert(12, typeof(string), "{0} 秒", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("Source: 42", conv.Convert(42, typeof(string), "來源：{0}", System.Globalization.CultureInfo.InvariantCulture));
            // 未收錄模板＝原文模板，仍要把值填進去（不是留空、不是丟掉值）
            Assert.Equal("未收錄X：7", conv.Convert(7, typeof(string), "未收錄X：{0}", System.Globalization.CultureInfo.InvariantCulture));
            // 數字格式規格要原樣生效
            Assert.Equal("1,234 items", conv.Convert(1234, typeof(string), "{0:N0} 筆", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { LanguageService.SetEnglishForTests(false); }

        // 繁體：模板原樣，值照填
        Assert.Equal("8 核", conv.Convert(8, typeof(string), "{0} 核", System.Globalization.CultureInfo.InvariantCulture));
        // 簡體：模板轉簡體（「秒」繁簡同形，用「執行緒」驗真的轉了）
        LanguageService.SetSimplifiedForTests(true);
        try
        {
            Assert.Equal("8 线程", conv.Convert(8, typeof(string), "{0} 執行緒", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { LanguageService.SetSimplifiedForTests(false); }
        // null 值＝空字串，不是 "0"
        Assert.Equal("", conv.Convert(null, typeof(string), "{0} 核", System.Globalization.CultureInfo.InvariantCulture));
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
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "英語樹測試逾時");
        if (error is not null) throw error;
    }

    private static Window Host(params FrameworkElement[] children)
    {
        var panel = new System.Windows.Controls.StackPanel();
        foreach (var c in children) panel.Children.Add(c);
        return new Window { Content = panel, Width = 200, Height = 100, ShowInTaskbar = false, ShowActivated = false };
    }

    private static void Pump()
        => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
}
