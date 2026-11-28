using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;

namespace XinSpect;

/// <summary>Opening the window does not read hardware; only the explicit Start click captures.</summary>
public partial class CpuPackagePowerDiagnosticWindow : Window
{
    private readonly CpuPackagePowerDiagnosticService _service;
    private CancellationTokenSource? _cancellation;
    private CpuPackagePowerDiagnosticResult? _result;
    private bool _busy;

    public CpuPackagePowerDiagnosticWindow() : this(new CpuPackagePowerDiagnosticService()) { }
    public CpuPackagePowerDiagnosticWindow(CpuPackagePowerDiagnosticService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; _result = null; SetBusy(true);
        EvidenceBox.Text = "正在讀取有限序列；未啟動負載。同步 I/O 尚未返回前不釋放通道。";
        StatusText.Text = "被動取樣中…";
        try
        {
            _result = await _service.CaptureAsync(cancellation.Token);
            StatusText.Text = $"{_result.Availability}：{_result.Reason}";
            EvidenceBox.Text = Format(_result);
        }
        catch (Exception ex)
        {
            StatusText.Text = "診斷失敗：" + ex.Message;
            EvidenceBox.Text = "沒有可發布的新結果。";
        }
        finally { _cancellation = null; SetBusy(false); }
    }

    internal static string Format(CpuPackagePowerDiagnosticResult result)
    {
        var b = new StringBuilder();
        b.AppendLine($"狀態：{result.Availability}；{result.Reason}");
        b.AppendLine($"UTC：{result.StartedAtUtc:O} → {result.FinishedAtUtc:O}；QPC frequency={result.QpcFrequency}");
        if (result.Platform is { } p)
            b.AppendLine($"CPU：{p.Vendor} family={p.Family:X} model=0x{p.Model:X} stepping={p.Stepping}；Windows packages={p.WindowsPackageCount}；CPUID package={p.CpuidPackageId}；G{p.Processor.Group}/LP{p.Processor.Index}");
        if (result.Source is { } s)
        {
            b.AppendLine($"來源：{s.BackendName} [{s.BackendId}/{s.TrustTier}]；mode={s.HostMode}；lease={s.LeaseIdentity}；app={s.ApplicationVersion}");
            b.AppendLine($"telemetry library={s.TelemetryLibraryVersion ?? "unknown"}；declared module SHA-256={s.DeclaredModuleSha256 ?? "unknown"}（既有通道的宣告，不是此次重新量測模組雜湊）");
        }
        foreach (var sample in result.Samples)
        {
            b.AppendLine();
            b.AppendLine($"樣本 {sample.Index + 1}：{sample.CapturedAtUtc:O}；{sample.State}");
            foreach (var read in new[] { sample.Units, sample.Energy })
                if (read is not null)
                    b.AppendLine($"  {read.AddressHex} raw={read.RawHex ?? "null"}；QPC [{read.BeforeQpc}, {read.AfterQpc}]；{read.Availability}；{read.Reason}");
            if (sample.Interval is { } interval)
            {
                b.AppendLine($"  Δcount(mod32)={interval.Modulo32Counts}；Δt={interval.Seconds:0.000000} s；{interval.JoulesPerCount:G10} J/count");
                b.AppendLine($"  未驗證估值：{(interval.UnverifiedWatts is double w ? $"{w:0.######} W" : "不發布瓦數")}；one-count resolution={interval.OneCountResolutionWatts:G6} W");
                b.AppendLine("  " + interval.Limitation);
            }
        }
        b.AppendLine(); b.AppendLine(result.Limitations);
        b.AppendLine("來源更新時間：" + result.SourceUpdateTime);
        b.AppendLine("規格：" + result.Specification);
        return b.ToString();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _result is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匯出本次 CPU 功耗被動診斷", Filter = "JSON evidence (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true, FileName = "cpu-package-power-diagnostic.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string json = _result.ToJson();
            SetBusy(true);
            await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(false));
            StatusText.Text = "本次診斷 JSON 已匯出至您選擇的檔案。";
        }
        catch (Exception ex) { StatusText.Text = "匯出失敗：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; StartButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy && _cancellation is not null;
        ExportButton.IsEnabled = !busy && _result is not null;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel(); CancelButton.IsEnabled = false;
        StatusText.Text = "已要求取消；等待目前同步讀取返回後保留證據並停止。";
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        if (_cancellation is not null) Cancel_Click(sender ?? this, new RoutedEventArgs());
    }
}
