using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace XinSpect;

public partial class ToolboxView : UserControl
{
    public ToolboxView() => InitializeComponent();

    // Host.Content 延遲載入時 DataContext 由父容器繼承；仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Application.Current?.MainWindow?.DataContext as MainViewModel;

    // 點選工具按鈕：由 Tag 取回對應項目，交由工具箱服務啟動 / 導向下載
    // （若該工具已裝入插槽，服務會優先啟動插槽內的本機執行檔）
    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ToolItem tool })
            Vm?.Toolbox.Launch(tool);
    }

    // ── 內建硬體檢測：開啟全螢幕原生檢測視窗（純輸入／顯示事件，零外部相依）──
    private void ScreenTest_Click(object sender, RoutedEventArgs e) => Open(new ScreenTestWindow());
    private void MouseTest_Click(object sender, RoutedEventArgs e) => Open(new MouseTestWindow());
    private void KeyboardTest_Click(object sender, RoutedEventArgs e) => Open(new KeyboardTestWindow());
    private void SpeakerTest_Click(object sender, RoutedEventArgs e) => Open(new SpeakerTestWindow());
    private void MotionTest_Click(object sender, RoutedEventArgs e) => Open(new MotionTestWindow());

    private void Open(Window w)
    {
        w.Owner = Window.GetWindow(this);
        w.Show();
    }

    // 裝入 / 更換插槽：選擇下載好的本機可執行檔放進插槽。
    private void Slot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolItem tool } || Vm is not { } vm) return;

        var dlg = new OpenFileDialog
        {
            Title = $"為「{tool.Name}」選擇可執行檔",
            Filter = "可執行檔 (*.exe)|*.exe|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (tool.SlotPath is { Length: > 0 } cur && File.Exists(cur))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(cur);
            dlg.FileName = Path.GetFileName(cur);
        }
        if (dlg.ShowDialog() == true)
            vm.Toolbox.AssignSlot(tool, dlg.FileName);
    }

    // 移除插槽：清除該工具已裝入的本機執行檔。
    private void SlotClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ToolItem tool } && Vm is { } vm)
            vm.Toolbox.ClearSlot(tool);
    }

    // 於檔案總管開啟插槽內執行檔的所在資料夾（並選取該檔）。
    private void SlotOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolItem tool }) return;
        if (tool.SlotPath is not { Length: > 0 } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* 無檔案總管或路徑異常時靜默略過 */ }
    }
}
