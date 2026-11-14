using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        if (tool.Kind == ToolKind.CustomApp && !ConfirmCustomApp(tool)) return;
        if (tool is { Risk: ToolRisk.Danger, HasSlot: true } && !ConfirmDanger(tool)) return;
        Vm?.Toolbox.Launch(tool);
    }

    private static bool ConfirmCustomApp(ToolItem tool)
        => XMsg.Show(
            $"即將啟動自訂程式「{tool.Name}」。\n\n路徑：{tool.Target}\n\n"
            + "它會以目前 Windows 帳戶權限執行，XinSpect 不會限制或隔離它。只啟動你信任的程式。\n\n"
            + "確定要繼續嗎？",
            "自訂程式確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void AddCustomTool_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || EditCustomTool(Window.GetWindow(this), null) is not { } definition) return;
        try { vm.Toolbox.AddCustomTool(definition); }
        catch (ArgumentException ex) { XMsg.Show(ex.Message, "無法新增工具", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CustomEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolItem { IsCustom: true } tool } || Vm is not { } vm) return;
        if (EditCustomTool(Window.GetWindow(this), tool) is not { } definition) return;
        try { vm.Toolbox.UpdateCustomTool(tool, definition); }
        catch (ArgumentException ex) { XMsg.Show(ex.Message, "無法更新工具", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CustomRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ToolItem { IsCustom: true } tool })
            Vm?.Toolbox.RemoveCustomTool(tool);
    }

    private static ToolboxCustomTool? EditCustomTool(Window? owner, ToolItem? current)
    {
        var name = new TextBox { Text = current?.Name ?? "", MinWidth = 340, MaxLength = 80 };
        var group = new TextBox { Text = current?.Group ?? "自訂工具", MaxLength = 48 };
        var target = new TextBox { Text = current?.Target ?? "" };
        var description = new TextBox
        {
            Text = current?.Description ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 64,
            MaxLength = 500,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var arguments = new TextBox
        {
            Text = current is { Kind: ToolKind.CustomApp } ? string.Join(Environment.NewLine, current.LaunchArguments) : "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 76,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var root = new StackPanel { Margin = new Thickness(20), MinWidth = 520 };
        void AddField(string label, FrameworkElement input)
        {
            root.Children.Add(new TextBlock { Text = LanguageService.T(label), Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.SemiBold });
            root.Children.Add(input);
        }

        AddField("名稱", name);
        AddField("分類", group);
        root.Children.Add(new TextBlock { Text = LanguageService.T("EXE 路徑或 HTTP/HTTPS 網址"), Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.SemiBold });
        var targetRow = new DockPanel();
        var browse = new Button { Content = LanguageService.T("選擇 EXE…"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        DockPanel.SetDock(browse, Dock.Right);
        targetRow.Children.Add(browse);
        targetRow.Children.Add(target);
        root.Children.Add(targetRow);
        AddField("說明", description);
        AddField("啟動參數（每行一個；僅 EXE 適用）", arguments);
        root.Children.Add(new TextBlock
        {
            Text = LanguageService.T("自訂 EXE 會以目前帳戶權限執行，XinSpect 不會沙箱化或攔截其行為。"),
            Foreground = Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 8),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = LanguageService.T("取消"), IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Button { Content = LanguageService.T("儲存工具"), IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = LanguageService.T(current is null ? "新增自訂工具" : "編輯自訂工具"),
            Content = root,
            SizeToContent = SizeToContent.Height,
            Width = 600,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = LanguageService.T("選擇自訂工具 EXE"),
                Filter = LanguageService.T("可執行檔 (*.exe)") + "|*.exe",
                CheckFileExists = true,
            };
            if (target.Text.Length > 0 && !Uri.TryCreate(target.Text, UriKind.Absolute, out _))
                dialog.FileName = target.Text;
            if (dialog.ShowDialog(window) != true) return;
            target.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        };

        ToolboxCustomTool? result = null;
        accept.Click += (_, _) =>
        {
            string[] args = arguments.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool isUrl = Uri.TryCreate(target.Text.Trim(), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https";
            result = new ToolboxCustomTool
            {
                Name = name.Text,
                Group = group.Text,
                Description = description.Text,
                Kind = isUrl ? ToolKind.WebLink : ToolKind.CustomApp,
                Target = target.Text,
                Arguments = args,
            };
            window.DialogResult = true;
        };

        return window.ShowDialog() == true ? result : null;
    }

    private static bool ConfirmDanger(ToolItem tool)
        => XMsg.Show(
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
    // ── 一鍵裝機（winget）：自 SetupView 併入的事件處理 ──────────────────

    private WingetService? WingetVm => Vm?.Winget;

    private void SelectRec_Click(object sender, RoutedEventArgs e) => WingetVm?.SelectRecommended();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => WingetVm?.SelectAll();
    private void Clear_Click(object sender, RoutedEventArgs e) => WingetVm?.ClearSelection();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (WingetVm is { } w && w.CanInstall) await w.InstallSelectedAsync();
    }

    private void GetWinget_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-windows-store://pdp/?ProductId=9nblggh4nns1",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Diag.Swallow("開啟 Store", ex, "無法開啟 Microsoft Store 頁面"); }
    }

    private void Slot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolItem tool } || Vm is not { } vm) return;

        var dlg = new OpenFileDialog
        {
            Title = LanguageService.T($"為「{tool.Name}」選擇可執行檔"),
            Filter = LanguageService.T("可執行檔 (*.exe)") + "|*.exe|" + LanguageService.T("所有檔案 (*.*)") + "|*.*",   // Filter 逐段翻顯示名，模式原樣（同 StressBridgeView）
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
