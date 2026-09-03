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
        ?? Shell.Vm;

    // 點選工具按鈕：由 Tag 取回對應項目，交由工具箱服務啟動 / 導向下載
    // （若該工具已裝入插槽，服務會優先啟動插槽內的本機執行檔）
    //
    // 「危險」等級的工具若已裝入插槽，按下去是真的啟動一支會寫韌體或抹碟的程式，
    // 所以先問一次。只在插槽情況下問：沒有插槽時這顆鈕只會開官方下載頁，開網頁不需要確認。
    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolItem tool }) return;
        if (tool is { Risk: ToolRisk.Danger, HasSlot: true } && !ConfirmDanger(tool)) return;
        Vm?.Toolbox.Launch(tool);
    }

    private static bool ConfirmDanger(ToolItem tool)
        => MessageBox.Show(
            $"即將啟動「{tool.Name}」。\n\n{tool.RiskNote}\n\n"
            + "曦覽只負責把它啟動起來，之後你在那支程式裡做什麼、造成什麼後果，本程式無法介入也無法還原。\n\n"
            + "確定要啟動嗎？",
            "危險工具確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    // ── 工具箱不做導覽 ────────────────────────────────────────────
    //    1.6.2 起這一頁沒有任何「跳到曦覽某頁」或「開啟內建檢測視窗」的按鈕：
    //    硬體檢測搬到「實用工具 → 硬體檢測」，其餘自家功能本來就在左側欄與 Ctrl+K。
    //    1.9.0 起連「曦覽內建：X」那枚不可點的對照標籤也移除了，對照說明只留在主按鈕的提示裡。──

    // 清除搜尋詞。
    private void ClearFilter_Click(object sender, RoutedEventArgs e) => Vm?.Toolbox.ClearFilter();

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
