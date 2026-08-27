using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace XinSpect;

/// <summary>
/// 超頻分頁。DataContext 繼承自 MainWindow（= MainViewModel），
/// 所有繫結走 <c>Overclock.*</c>；此檔僅負責把使用者操作轉呼叫服務方法，
/// 真正的硬體寫入全部發生在 <see cref="OverclockService"/>／XTU 引擎中。
/// 進入本分頁前的兩階段風險確認由 MainWindow 負責（見 OcRiskWindow）。
/// </summary>
public partial class OverclockView : UserControl
{
    public OverclockView() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;
    private OverclockService? Oc => Vm?.Overclock;

    // 核心電壓安全上限：超過此值對多數 Intel 桌上型 / HEDT（含本機 X299）皆屬高風險，套用前強制確認。
    private const double SafeVcoreCeiling = 1.40;

    // 若待套用的核心電壓超過安全上限，跳出警告要求二次確認；使用者取消回傳 false。
    private static bool ConfirmHighVoltage(OverclockService oc)
    {
        double v = oc.HighestPendingVoltage();
        if (v < SafeVcoreCeiling) return true;
        return MessageBox.Show(
            $"即將套用核心電壓 {v:0.000} V，已超過建議安全上限 {SafeVcoreCeiling:0.00} V。\n\n" +
            "過高的核心電壓可能造成當機、藍屏、過熱，長期更可能永久損壞處理器。\n" +
            "請確認你清楚此風險，且散熱與供電足以負荷。\n\n確定要繼續套用嗎？",
            "高電壓警告", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    // ── 單一旋鈕：套用 ────────────────────────────────────────────────────
    private void ApplyKnob_Click(object sender, RoutedEventArgs e)
    {
        var oc = Oc;
        if (oc is null || (sender as FrameworkElement)?.DataContext is not OcKnob knob) return;
        // 單一電壓旋鈕若超上限亦需確認
        if (knob.Kind == OcKnobKind.Voltage && knob.Target >= SafeVcoreCeiling && !ConfirmHighVoltage(oc)) return;
        _ = oc.ApplyKnob(knob);
    }

    // ── 微調鈕：Tag = 帶符號的級距索引（±1／±2／±3）→ 依旋鈕自身級距增減目標值 ──
    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not OcKnob knob) return;
        if (fe.Tag is string s && int.TryParse(s, out var idx))
            knob.NudgeByIndex(idx);
    }

    // ── 每核心明細：開啟獨立彈出視窗（純唯讀呈現，不做寫入）──────────────────
    private void OpenCoreDetail_Click(object sender, RoutedEventArgs e)
    {
        var oc = Oc;
        if (oc is null) return;
        new CoreDetailWindow(oc) { Owner = Window.GetWindow(this) }.Show();
    }

    // ── 超連結：以系統預設瀏覽器開啟（XTU 下載頁）──────────────────────────
    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* 無可用瀏覽器時靜默略過，不影響主流程 */ }
        e.Handled = true;
    }

    // ── 目標時脈：一次套用倍頻 + 外頻 ─────────────────────────────────────
    private void ApplyPlanner_Click(object sender, RoutedEventArgs e) => _ = Oc?.ApplyPlannerAsync();

    // ── Vcore：套用（聚合平台會一次寫入全部核心，見 OverclockService.ApplyVcore）──
    private void ApplyVcore_Click(object sender, RoutedEventArgs e)
    {
        var oc = Oc;
        if (oc is null || !ConfirmHighVoltage(oc)) return;
        _ = oc.ApplyVcore();
    }

    // ── 體質評分：重新測試（服務端非同步取樣約 5 秒峰值後估算；此處採即發即忘）──
    private void RetestSilicon_Click(object sender, RoutedEventArgs e) => _ = Oc?.RetestSilicon();

    // ── 看門狗：確認目前設定穩定（清除回退點、寫入最後穩定設定）───────────
    private void ConfirmStable_Click(object sender, RoutedEventArgs e) => Oc?.ConfirmStable();

    // ── Speed Optimizer ───────────────────────────────────────────────────
    private void SpeedOptOn_Click(object sender, RoutedEventArgs e) => _ = Oc?.SetSpeedOptimizer(true, false);
    private void SpeedOptExtreme_Click(object sender, RoutedEventArgs e) => _ = Oc?.SetSpeedOptimizer(true, true);
    private void SpeedOptOff_Click(object sender, RoutedEventArgs e) => _ = Oc?.SetSpeedOptimizer(false, false);

    // ── 設定檔 ────────────────────────────────────────────────────────────
    private void SaveProfile_Click(object sender, RoutedEventArgs e) => Oc?.CaptureProfile();

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var oc = Oc;
        if (oc is null) return;
        var name = string.IsNullOrWhiteSpace(oc.ProfileNameInput) ? "超頻設定" : oc.ProfileNameInput.Trim();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匯出超頻設定檔",
            Filter = "超頻設定檔 (*.ocp)|*.ocp",
            DefaultExt = ".ocp",
            FileName = name + ".ocp",
            AddExtension = true,
        };
        if (dlg.ShowDialog() == true)
            oc.ExportProfileTo(dlg.FileName);
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e) => _ = Oc?.ApplySelectedProfile();

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var oc = Oc;
        if (oc is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "匯入超頻設定檔",
            Filter = "超頻設定檔 (*.ocp)|*.ocp|所有檔案 (*.*)|*.*",
            DefaultExt = ".ocp",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            _ = oc.ImportProfileFrom(dlg.FileName);
    }

    private void ApplyLastStable_Click(object sender, RoutedEventArgs e) => _ = Oc?.ApplyLastStable();

    // ── 簡易燒機 ──────────────────────────────────────────────────────────
    private void StressStart_Click(object sender, RoutedEventArgs e) => Oc?.StartStress();
    private void StressStop_Click(object sender, RoutedEventArgs e) => Oc?.StopStress();

    // ── 全域動作 ──────────────────────────────────────────────────────────
    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        var oc = Oc;
        if (oc is null || !ConfirmHighVoltage(oc)) return;   // 全部套用可能含電壓變更，超上限先確認
        _ = oc.ApplyAll();
    }
    private void Discard_Click(object sender, RoutedEventArgs e) => _ = Oc?.DiscardAll();
    private void RestoreDefaults_Click(object sender, RoutedEventArgs e) => _ = Oc?.RestoreDefaults();
}
