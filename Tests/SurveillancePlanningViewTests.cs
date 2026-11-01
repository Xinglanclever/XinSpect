using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 監控工程規劃頁的載入煙霧測試：XAML 解析、資源鍵、預設值與第一次計算。
/// 計算本身由 <see cref="SurveillancePlanningTests"/> 釘住；這裡只確認「頁面打得開、數字有出來」。
/// </summary>
public class SurveillancePlanningViewTests
{
    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfEnv.Ensure();
                work();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "監控規劃頁測試逾時。");
        if (error is not null) throw new Xunit.Sdk.XunitException("STA 執行緒中發生例外：" + error);
    }

    [Fact]
    public void 頁面載入後十段結果都有預設值()
    {
        OnSta(() =>
        {
            var view = new SurveillancePlanningView();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            string[] outputs =
            [
                "NvrValues", "IngressValues", "PoeValues", "SwitchValues", "FiberValues",
                "DropValues", "CablingValues", "RaidValues", "WirelessValues", "UpsValues",
            ];
            foreach (string name in outputs)
            {
                var block = view.FindName(name) as TextBlock;
                Assert.True(block is not null, $"找不到 {name}");
                Assert.False(string.IsNullOrWhiteSpace(block!.Text), $"{name} 沒有算出任何東西");
            }

            var nvr = (TextBlock)view.FindName("NvrValues")!;
            Assert.Contains("TB", nvr.Text);
            Assert.Contains("天", nvr.Text);
        });
    }
}
