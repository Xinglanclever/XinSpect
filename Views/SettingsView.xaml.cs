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

        // 外觀：主題下拉與強調色色票皆由 ThemeService 提供（靜態服務，故於程式碼指派而非繫結）
        ThemeCombo.ItemsSource = ThemeService.ThemeNames;
        ThemeCombo.SelectedIndex = ThemeService.ThemeIndex;
        AccentList.ItemsSource = ThemeService.Presets;
        ShowAccentHint();

        // 外觀也可能從別處被改（命令面板 Ctrl+K 的「切換主題」）：本頁的下拉與提示要跟上，
        // 否則畫面已經是淺色，這裡卻還寫著「目前：深色主題」。
        this.OnThemeChange(() =>
        {
            if (ThemeCombo.SelectedIndex != ThemeService.ThemeIndex)
                ThemeCombo.SelectedIndex = ThemeService.ThemeIndex;
            ShowAccentHint();
        });

        // 首次進入設定頁時自動跑一次環境自檢（背景偵測，不阻塞 UI）；之後由使用者按鈕手動重檢。
        Loaded += (_, _) =>
        {
            SyncKeyBox();
            SetupSharedAiOption();
            Vm?.Diagnostics.Refresh();   // 進頁面就是最新的一份，不必等下一拍心跳
            if (Vm is { } vm && !vm.EnvCheck.HasRun && !vm.EnvCheck.IsRunning)
                _ = vm.EnvCheck.RunAsync(vm);
        };
    }

    // ── API 金鑰 ────────────────────────────────────────────────────────────
    // PasswordBox 的 Password 不是依賴屬性，無法繫結，故以程式碼雙向同步。
    // _syncingKey 防止「寫回設定 → 設定通知 → 再寫回欄位」把游標彈到開頭。
    private bool _syncingKey;

    private void SyncKeyBox()
    {
        if (Vm is not { } vm || AiKeyMasked is null) return;
        string key = vm.Settings.AiApiKey ?? "";
        if (AiKeyMasked.Password == key) return;
        _syncingKey = true;
        try { AiKeyMasked.Password = key; }
        finally { _syncingKey = false; }
    }

    private void AiKeyMasked_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingKey || Vm is not { } vm) return;
        vm.Settings.AiApiKey = AiKeyMasked.Password;
    }

    // 收回明碼時把遮蔽欄位補成最新值（明碼欄位是直接繫結，可能已被改過）。
    private void AiKeyShow_Unchecked(object sender, RoutedEventArgs e) => SyncKeyBox();

    // ── 外觀 ────────────────────────────────────────────────────────────────

    // 主題切換：ThemeService 立即改寫共用筆刷，整棵視覺樹同步換色並持久化。
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedIndex >= 0) ThemeService.ThemeIndex = ThemeCombo.SelectedIndex;
        ShowAccentHint();
    }

    // 強調色色票：以 Tag 帶回預設鍵值。
    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        ThemeService.Accent = ThemeService.FindAccent(key);
        ShowAccentHint();
    }

    private void ShowAccentHint()
    {
        if (AccentHint is not null)
            AccentHint.Text = $"目前：{ThemeService.ThemeNames[ThemeService.ThemeIndex]}主題 ・ {ThemeService.Accent.Name}（{ThemeService.Accent.Main}）";
    }

    // Host.Content 延遲載入時 DataContext 由父容器繼承；點選當下已就緒。仍以主視窗為後備。
    private MainViewModel? Vm =>
        DataContext as MainViewModel
        ?? Shell.Vm;

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

    // ── 歷史記錄 ────────────────────────────────────────────────────────────

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateToKey("history");

    // 清空歷史倉：記憶體環與磁碟檔一併歸零，無法復原故先確認。
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        var answer = MessageBox.Show(
            $"確定要清空全部歷史資料嗎？目前已保留 {vm.History.MinuteCount} 筆分鐘紀錄（{vm.History.SizeText}）。此動作無法復原。",
            "歷史記錄", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        try { vm.History.Clear(); vm.HistoryView.Reload(); } catch { /* 清空失敗維持原狀 */ }
    }

    // 清空跑分紀錄簿：這份紀錄是跑分唯一的比較基準，清掉就沒有可比的對象了，故先確認。
    private void ClearBenchLog_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        var answer = MessageBox.Show(
            $"確定要清空跑分紀錄簿嗎？目前已保留 {vm.Benchmarks.Count} 筆成績。\n\n"
            + "曦覽不內建其他機器的參考分數，這份紀錄是「與上次相比」「重複性」的唯一依據；清空後在重新累積出紀錄前，跑分只會有單次數字而無從比較。此動作無法復原。",
            "跑分紀錄簿", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        try { vm.Benchmarks.Clear(); } catch { /* 清空失敗維持原狀 */ }
    }

    // ── 診斷紀錄 ────────────────────────────────────────────────────────────

    private void DiagRefresh_Click(object sender, RoutedEventArgs e) => Vm?.Diagnostics.Refresh();

    private void DiagOpenFile_Click(object sender, RoutedEventArgs e) => Vm?.Diagnostics.OpenFile();

    private void DiagOpenFolder_Click(object sender, RoutedEventArgs e) => Vm?.Diagnostics.OpenFolder();

    // 只清畫面上那份；diag.log 不動——要附給別人看的是檔案，把它一起刪掉就本末倒置了。
    private void DiagClear_Click(object sender, RoutedEventArgs e) => Vm?.Diagnostics.Clear();

    // 切換 AI 供應商時，若端點/模型仍為另一供應商的預設或留空，帶入本供應商的建議預設值。
    private void AiProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        var vm = Vm;
        if (vm is null || !IsLoaded) return;   // 初次繫結載入時不覆寫已存設定

        const string ollamaUrl = "http://localhost:11434/v1";
        const string openAiUrl = "https://api.openai.com/v1";
        string url = (vm.Settings.AiBaseUrl ?? "").Trim();
        string model = (vm.Settings.AiModel ?? "").Trim();

        // 免費共用：端點與模型都由中轉決定，不動使用者原本填的值——
        // 他自填的金鑰與端點要原封不動留著，切回去時才不用重新輸入。
        if (vm.Settings.AiProviderEnum == AiProvider.SharedFree) { ShowSharedNote(); return; }

        ShowSharedNote();
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

    // ── 免費共用額度 ────────────────────────────────────────────────────────
    // 第三個供應商選項的文字與可用狀態是執行期決定的：作者還沒啟用中轉（程式裡沒有網址）
    // 就整項停用，讓人看得到「有這回事」但點不下去，而不是選了才吃一個連線失敗。
    private void SetupSharedAiOption()
    {
        if (AiSharedItem is null) return;
        AiSharedItem.Content = SharedAiEndpoint.OptionText;
        AiSharedItem.IsEnabled = SharedAiEndpoint.IsConfigured;
        ShowSharedNote();
    }

    private void ShowSharedNote()
    {
        if (AiSharedNote is null) return;
        bool shared = Vm?.Settings.AiProviderEnum == AiProvider.SharedFree;
        AiSharedNote.Visibility = shared || !SharedAiEndpoint.IsConfigured
            ? Visibility.Visible : Visibility.Collapsed;
        AiSharedNote.Text = !SharedAiEndpoint.IsConfigured
            ? "免費共用額度目前尚未啟用（這個版本裡還沒有中轉網址），請用本機 Ollama 或自填端點與金鑰。"
            : "共用額度的模型由 Cloudflare Workers AI 提供，經作者自架的中轉分享（這支程式裡不含任何金鑰），"
              + "只開放「一鍵評價」：自由對話、主動診斷與診斷代理不走這條額度（"
              + "代理一次提問可能連續發出六、七次請求）。Workers AI 每天的免費用量是固定的、UTC 零時歸零，"
              + "所以額度可能用完，也可能被隨時關閉；選這一項時，硬體規格與感測數據會經中轉送到 Cloudflare 的模型。";
    }

    // 一鍵重置：把系統提示詞還原為內建預設（要求 AI 客觀公正的版本）。
    private void ResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        vm.Settings.AiSystemPrompt = AiService.DefaultSystemPrompt;
        if (AiPromptHint is not null) AiPromptHint.Text = "已重置為內建預設提示詞。";
    }

    // 儲存提示詞：設定會在每次輸入時自動存檔，所以這顆按鈕真正做的是「收尾整理」——
    // 去掉頭尾空白、統一換行，全空白則退回內建預設（空提示詞會讓模型完全失去客觀性要求），
    // 然後告訴使用者實際存到哪、存了多長。不做事只印「已儲存」的按鈕是裝飾品。
    private void SavePrompt_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        string text = (vm.Settings.AiSystemPrompt ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        bool reset = text.Length == 0;
        if (reset) text = AiService.DefaultSystemPrompt;
        vm.Settings.AiSystemPrompt = text;

        if (AiPromptHint is null) return;
        AiPromptHint.Text = (reset ? "提示詞原本是空的，已退回內建預設（" : "已整理並儲存（")
            + $"{text.Length} 字）→ {SettingsService.FilePath}";
    }

    // 開啟獨立的 AI 助手分頁。
    private void OpenAiTab_Click(object sender, RoutedEventArgs e)
        => Shell.Main?.NavigateToAi();

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

    // ── 報告匯出 ────────────────────────────────────────────────────────────

    // 匯出報告：格式由使用者在另存對話框選的副檔名決定（.html / .md / .txt）。
    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        try
        {
            string? path = ReportService.Export(vm);
            if (ReportHint is not null)
                ReportHint.Text = path is null ? "已取消匯出。" : "已匯出並開啟：" + path;
        }
        catch (Exception ex)
        {
            if (ReportHint is not null) ReportHint.Text = "匯出失敗：" + ex.Message;
        }
    }

    // 複製為 Markdown：整份報告直接進剪貼簿，不落地檔案。
    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        bool ok = ReportService.CopyMarkdown(vm);
        if (ReportHint is not null)
            ReportHint.Text = ok
                ? "已複製 Markdown 報告到剪貼簿，可直接貼上論壇或議題。"
                : "剪貼簿目前被其他程式佔用，請稍後再試（或改用「匯出報告…」存成 .md）。";
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
