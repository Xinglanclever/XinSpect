using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>設定分頁：更新頻率、開機自啟、感測器 CSV 記錄、硬體警示閾值與 AI 評價。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // 首次進入設定頁時自動跑一次環境自檢（背景偵測，不阻塞 UI）；之後由使用者按鈕手動重檢。
        Loaded += (_, _) =>
        {
            if (Vm is { } vm && !vm.EnvCheck.HasRun && !vm.EnvCheck.IsRunning)
                _ = vm.EnvCheck.RunAsync(vm);
        };
    }

    // Host.Content 延遲載入時 DataContext 由父容器繼承；點選當下已就緒。仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Application.Current?.MainWindow?.DataContext as MainViewModel;

    // 開始 / 停止感測器記錄：依目前狀態切換，記錄時鎖定於背景每拍寫入。
    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        if (vm.SensorLog.IsLogging)
            vm.SensorLog.StopLogging();
        else
            vm.SensorLog.StartLogging(vm.Settings);
    }

    // 於檔案總管開啟目前的記錄輸出資料夾。
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        => Vm?.SensorLog.OpenFolder(Vm.Settings);

    // 切換 AI 供應商時，若端點/模型仍為另一供應商的預設或留空，帶入本供應商的建議預設值。
    private void AiProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        var vm = Vm;
        if (vm is null || !IsLoaded) return;   // 初次繫結載入時不覆寫已存設定

        const string ollamaUrl = "http://localhost:11434/v1";
        const string openAiUrl = "https://api.openai.com/v1";
        string url = (vm.Settings.AiBaseUrl ?? "").Trim();
        string model = (vm.Settings.AiModel ?? "").Trim();

        if (vm.Settings.AiProviderEnum == AiProvider.Ollama)
        {
            if (url.Length == 0 || url == openAiUrl) vm.Settings.AiBaseUrl = ollamaUrl;
            if (model.Length == 0 || model == "gpt-4o-mini") vm.Settings.AiModel = "llama3.2";
        }
        else
        {
            if (url.Length == 0 || url == ollamaUrl) vm.Settings.AiBaseUrl = openAiUrl;
            if (model.Length == 0 || model == "llama3.2") vm.Settings.AiModel = "gpt-4o-mini";
        }
    }

    // 一鍵重置：把系統提示詞還原為內建預設（要求 AI 客觀公正的版本）。
    private void ResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        vm.Settings.AiSystemPrompt = AiService.DefaultSystemPrompt;
        if (AiPromptHint is not null) AiPromptHint.Text = "已重置為內建預設提示詞。";
    }

    // 儲存提示詞：設定於變更時已自動存檔，此處提供明確回饋。
    private void SavePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (AiPromptHint is not null) AiPromptHint.Text = "提示詞已儲存。";
    }

    // 開啟獨立的 AI 助手分頁。
    private void OpenAiTab_Click(object sender, RoutedEventArgs e)
        => (Application.Current?.MainWindow as MainWindow)?.NavigateToAi();

    // 一鍵獲取：向目前端點查詢可用模型清單，成功後於下拉選單列出。
    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.Ai.FetchModelsAsync();
    }

    // 於下拉選單挑選模型：帶入模型名稱欄位。
    private void AiModel_Picked(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is { } vm && AiModelsCombo?.SelectedItem is string name && name.Length > 0)
            vm.Settings.AiModel = name;
    }

    // 環境自檢：偵測各功能所需執行階段／驅動／服務是否就緒。
    private async void EnvCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.EnvCheck.RunAsync(vm);
    }

    // 環境自檢項目的取得連結：以系統預設瀏覽器開啟官方下載頁。
    private void EnvLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string url || url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 無可用瀏覽器時靜默略過 */ }
    }

    // 所有功能一鍵初始化：重新執行各模組偵測與載入（非破壞性，不重建計時器）。
    private async void Reinit_Click(object sender, RoutedEventArgs e)    {
        var vm = Vm;
        if (vm is null) return;
        if (ReinitBtn is not null) ReinitBtn.IsEnabled = false;
        if (ReinitHint is not null) ReinitHint.Text = "正在重新初始化所有功能，請稍候…";
        try
        {
            await vm.ReinitializeAllAsync();
            if (ReinitHint is not null) ReinitHint.Text = vm.StatusText;
        }
        catch (Exception ex)
        {
            if (ReinitHint is not null) ReinitHint.Text = "初始化時發生錯誤：" + ex.Message;
        }
        finally
        {
            if (ReinitBtn is not null) ReinitBtn.IsEnabled = true;
        }
    }
}
