using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XinSpect;

/// <summary>
/// 事件時間軸分頁：篩選／搜尋歷來事件，雙擊任一筆即跳至歷史回放的對應時刻。
/// </summary>
public partial class EventsView : UserControl
{
    public EventsView() => InitializeComponent();

    private EventsService? Svc => List.DataContext as EventsService;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not TimelineEvent ev) return;
        var win = Window.GetWindow(this) as MainWindow;
        win?.NavigateToHistory(ev.TimeUtc);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var svc = Svc;
        if (svc is null || svc.All.Count == 0) return;

        var answer = MessageBox.Show(
            $"確定要清空全部 {svc.All.Count} 筆事件紀錄嗎？此動作無法復原。",
            "事件時間軸", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK) svc.Clear();
    }
}
