using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 起始頁<b>真的</b>載進 WebView2 而且看得到內容——不是只檢查那段 HTML 字串長什麼樣。
///
/// <para>
/// 這一條補的是 2026-10-26 的一個真實故障：第 291 輪加上導覽白名單之後，
/// <c>WebView2.NavigateToString</c> 送出的 <c>data:text/html…</c> 導覽被當成「頁面裡的 data: 連結」
/// 取消掉，正式版的瀏覽器首頁變成一片空白。既有的「起始頁結構」測試看的是 HTML 字串，
/// 它當然是綠的——壞掉的是<b>載入那一步</b>。所以這裡開一個真的 WebView2 把頁面載起來，
/// 再從 DOM 讀回標題與內文。
/// </para>
/// <para>
/// 需要 WebView2 執行階段與一個可用的桌面工作階段；跑不動時會明確說出是哪一種失敗，
/// 不會安靜地當成通過。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class BrowserRenderTests
{
    [Fact]
    public void 起始頁真的載進WebView2而且看得到內容()
    {
        string? failure = null;
        string dom = "";

        var thread = new Thread(() =>
        {
            try
            {
                WpfEnv.Ensure();
                var view = new BrowserView();
                var host = new Window
                {
                    Width = 900, Height = 640, Left = -4000, Top = 0,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                    Content = view,
                };
                host.Show();

                var web = (WebView2)view.FindName("Web");
                var deadline = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < deadline && web.CoreWebView2 is null) { Pump(); Thread.Sleep(50); }

                var core = web.CoreWebView2;
                if (core is null)
                {
                    failure = "WebView2 沒有初始化（多半是沒有安裝 Evergreen Runtime，或這個工作階段沒有桌面）";
                }
                else
                {
                    // 等起始頁的導覽結束：文件標題出現，或逾時。
                    var until = DateTime.UtcNow.AddSeconds(25);
                    while (DateTime.UtcNow < until)
                    {
                        Pump();
                        var probe = RunScript(core, "document.title");
                        if (probe is { Length: > 0 }) break;
                        Thread.Sleep(100);
                    }

                    // 讓版面跑完一輪再讀內文（NavigateToString 是非同步的）
                    for (int i = 0; i < 20; i++) { Pump(); Thread.Sleep(50); }
                    dom = RunScript(core, "document.title + '｜' + document.body.innerText") ?? "";
                    if (dom.Length == 0) failure = "起始頁載入後 DOM 是空的（導覽被取消或沒有送出）";
                }

                host.Close();
                Pump();
            }
            catch (Exception ex) { failure ??= ex.GetType().Name + "：" + ex.Message; }
        })
        { IsBackground = true, Name = "browser-render" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "載入起始頁逾時");
        Assert.True(failure is null, "起始頁沒有載起來：" + failure);

        Assert.Contains("硬體導航", dom);        // 標題或品牌列
        Assert.Contains("CPU-Z", dom);           // 卡片清單確實畫出來了
    }

    /// <summary>在頁面裡執行一段腳本並把 JSON 字串結果解回 .NET 字串（中文會被轉成 \uXXXX）。</summary>
    private static string? RunScript(CoreWebView2 core, string script)
    {
        var task = core.ExecuteScriptAsync(script);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(30); }
        if (!task.IsCompleted || task.IsFaulted) return null;

        string json = task.Result;
        try { return JsonSerializer.Deserialize<string>(json) ?? ""; }
        catch (JsonException) { return null; }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new DispatcherOperationCallback(f => { ((DispatcherFrame)f!).Continue = false; return null; }), frame);
        Dispatcher.PushFrame(frame);
    }
}
