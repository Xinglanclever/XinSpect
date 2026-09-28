using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 繁簡轉換的視覺樹遍歷：往返不得漂移（claim 7 的殺手 bug），且要涵蓋 Run 與 ToolTip（claim 6）。
/// </summary>
/// <remarks>
/// 舊做法就地把譯文再往回轉：「暫存」與「快取」都→「缓存」，反向先命中「快取」，
/// 於是原文「暫存」切一圈永久變「快取」。正解是首次轉換時保存原文，永遠從原文出發轉換。
/// WPF 需 STA，且與其他 WPF 測試共用同一 Application，故掛 <see cref="WpfCollection"/>。
/// </remarks>
[Collection(WpfCollection.Name)]
public class LanguageTreeTests
{
    [Fact]
    public void 繁簡往返必須回到原文而非在譯文上再轉()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            var tb = new TextBlock { Text = "暫存" };   // 「暫存」與「快取」都→「缓存」
            var win = Host(tb);
            try
            {
                win.Show(); Pump();
                LanguageService.ConvertVisualTree(win, true);
                Assert.Equal("缓存", tb.Text);
                LanguageService.ConvertVisualTree(win, false);
                Assert.Equal("暫存", tb.Text);          // 不是「快取」
                // 多次往返仍穩定（快取頁多次切換不得漂移）
                for (int i = 0; i < 3; i++)
                {
                    LanguageService.ConvertVisualTree(win, true);
                    LanguageService.ConvertVisualTree(win, false);
                }
                Assert.Equal("暫存", tb.Text);
            }
            finally { win.Close(); }
        });
    }

    [Fact]
    public void 涵蓋Run行內文字與ToolTip()
    {
        RunSta(() =>
        {
            WpfEnv.Ensure();
            var tb = new TextBlock();
            tb.Inlines.Add(new Run("暫存"));
            var btn = new Button { Content = "暫存", ToolTip = "暫存" };
            var panel = new StackPanel();
            panel.Children.Add(tb);
            panel.Children.Add(btn);
            var win = Host(panel);
            try
            {
                win.Show(); Pump();
                LanguageService.ConvertVisualTree(win, true);
                Assert.Equal("缓存", ((Run)tb.Inlines.FirstInline).Text);
                Assert.Equal("缓存", btn.ToolTip);
                LanguageService.ConvertVisualTree(win, false);
                Assert.Equal("暫存", ((Run)tb.Inlines.FirstInline).Text);
                Assert.Equal("暫存", btn.ToolTip);
            }
            finally { win.Close(); }
        });
    }

    private static Window Host(UIElement child) => new()
    {
        Content = child, Width = 300, Height = 200, Left = -4000, Top = -4000,
        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
    };

    private static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Thread.Sleep(30);
        }
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(2)), "語言樹測試逾時");
        if (error is not null) throw error;
    }
}
