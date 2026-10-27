using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 每一個畫面都真的能被建起來。
///
/// <para>
/// 這一條補的是一個被如實申報過很多次的缺口：「UI 只到編譯通過，沒有真的開過」。
/// XAML 的錯誤分兩層——型別與事件處理常式由編譯器抓，但<b>資源鍵、字型、內嵌資源、
/// 建構子裡的初始化</b>都只在執行期才會炸，而炸的是「使用者點到那一頁」的那一刻。
/// </para>
/// <para>
/// 這裡對每個有無參數建構子的 <see cref="UserControl"/>／<see cref="Window"/> 做一次
/// <c>new</c>：不顯示、不觸發 <c>Loaded</c>、不碰硬體。建不起來就是缺陷——
/// 使用者第一次打開那一頁會看到同樣的例外。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class ViewConstructionTests
{
    /// <summary>在專屬 STA 執行緒上建一個 WPF Application（含佈景字典）並執行 <paramref name="action"/>。</summary>
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfEnv.Ensure();
                action();
            }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
            Name = "view-construction",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "建立 WPF 畫面時逾時（可能有建構子在做阻塞式初始化）");
        if (error is not null)
            throw new Xunit.Sdk.XunitException("建立 WPF 畫面時失敗：" + error.GetType().Name + "：" + error.Message
                                               + "\n" + error.StackTrace);
    }

    /// <summary>
    /// 建構子會把整個應用程式啟動起來的畫面：不列入本測試，理由寫在
    /// <see cref="ExcludedBecauseItStartsTheApp"/>。
    /// </summary>
    private static readonly Type[] Excluded = [typeof(MainWindow)];

    private const string ExcludedBecauseItStartsTheApp =
        "MainWindow 的建構子會建立整個 MainViewModel（感測引擎、導覽、主題、語言）並掛上不會卸下的訂閱；"
        + "在測試行程裡 new 它等於把一個活著的程式啟動起來，會讀硬體也會留下計時器。"
        + "它的 XAML 由資源鍵守門與版號守門間接守住，其餘每個畫面都由本測試實際建構。";

    /// <summary>組件裡所有「使用者看得到的畫面」：UserControl 與 Window，且有不吃參數的建構子。</summary>
    private static List<Type> ViewTypes() => typeof(App).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition)
        .Where(t => typeof(UserControl).IsAssignableFrom(t) || typeof(Window).IsAssignableFrom(t))
        .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
        .Where(t => !Excluded.Contains(t))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void 每一個畫面都能被建起來()
    {
        var types = ViewTypes();
        Assert.True(types.Count >= 40, $"只找到 {types.Count} 個畫面——反射掃描可能壞了");
        Assert.Contains(typeof(MainWindow), Excluded);   // 排除清單不能悄悄漏掉它

        var failures = new List<string>();
        OnSta(() =>
        {
            foreach (var type in types)
            {
                try
                {
                    object? instance = Activator.CreateInstance(type);
                    Assert.NotNull(instance);
                    (instance as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                    failures.Add($"{type.Name}：{inner.GetType().Name}：{inner.Message}");
                }
            }
        });

        Assert.True(failures.Count == 0,
            $"以下畫面建不起來（使用者第一次打開那一頁會看到同樣的例外）：\n{string.Join("\n", failures)}");
    }

    /// <summary>
    /// 內建瀏覽器起始頁是一段內嵌 HTML：它不在 XAML 的編譯範圍裡，寫壞了只有打開瀏覽器才看得到。
    /// 這裡只驗最基本的結構（不是驗外觀）：有 doctype、有搜尋表單、所有連結都是 https。
    /// </summary>
    [Fact]
    public void 瀏覽器起始頁的連結全部是https且結構完整()
    {
        string html = BrowserHome.Html;
        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("input", html, StringComparison.OrdinalIgnoreCase);

        var hrefs = System.Text.RegularExpressions.Regex.Matches(html, @"href=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(hrefs.Count >= 8, $"起始頁只找到 {hrefs.Count} 個連結");
        Assert.DoesNotContain(hrefs, h => !h.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        // 起始頁是在管理員權杖的分頁裡載入的：不該出現 http:// 或 javascript:
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }
}
