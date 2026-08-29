using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// 場景設定檔分頁：把「風扇曲線樣板 + Windows 電源計劃 + 顯示卡功耗／溫度上限」綁成
/// 一鍵可切的取向（靜音／均衡／效能／自訂）。所有動作都是真實寫入，缺少的能力會如實略過。
/// 實際執行與落地都在 <see cref="ProfileService"/>。
/// </summary>
public partial class ScenesView : UserControl
{
    public ScenesView() => InitializeComponent();

    // 套用場景；按鈕的 Tag 即場景鍵（套用期間由 NotBusy 停用按鈕，故此處不必再擋）。
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }
            && (DataContext as MainViewModel)?.Profiles is ProfileService svc)
            await svc.ApplyAsync(key);
    }
}
