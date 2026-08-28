using System.Windows.Controls;

namespace XinSpect;

/// <summary>記憶體分頁：顯示 CPU-Z 等級的時序、通道、實體模組與 SPD 詳細資訊。
/// 資料由 MainViewModel（DataContext 繼承自主視窗）提供，本身僅載入版面。</summary>
public partial class MemoryView : UserControl
{
    public MemoryView() => InitializeComponent();
}
