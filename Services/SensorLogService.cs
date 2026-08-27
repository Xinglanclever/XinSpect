using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace XinSpect;

/// <summary>感測器記錄：把即時感測值依設定間隔持續寫入 CSV，供事後以試算表分析。
/// 全為真實讀值（來自 <see cref="SensorService"/>），無模擬。</summary>
public sealed class SensorLogService : ObservableObject
{
    private StreamWriter? _writer;
    private DateTime _lastWrite = DateTime.MinValue;
    private DateTime _startedAt;

    private bool _isLogging;
    public bool IsLogging { get => _isLogging; private set { if (SetProperty(ref _isLogging, value)) OnPropertyChanged(nameof(ButtonText)); } }
    public string ButtonText => _isLogging ? "停止記錄" : "開始記錄";

    private int _rowCount;
    public int RowCount { get => _rowCount; private set { if (SetProperty(ref _rowCount, value)) OnPropertyChanged(nameof(RowCountText)); } }
    public string RowCountText => $"已記錄 {_rowCount} 列";

    private string _currentFile = "—";
    public string CurrentFile { get => _currentFile; private set => SetProperty(ref _currentFile, value); }

    private string _statusText = "尚未記錄";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>開始記錄：於設定資料夾建立時間戳 CSV，寫入表頭。</summary>
    public void StartLogging(SettingsService settings)
    {
        if (_isLogging) return;
        try
        {
            Directory.CreateDirectory(settings.LogFolder);
            var name = $"XinSpect_感測記錄_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var path = Path.Combine(settings.LogFolder, name);
            // UTF-8 BOM：讓 Excel 正確辨識中文表頭
            _writer = new StreamWriter(path, append: false, new UTF8Encoding(true)) { AutoFlush = true };
            _writer.WriteLine("時間,CPU負載(%),CPU溫度(°C),CPU時脈(MHz),記憶體負載(%),GPU負載(%),GPU溫度(°C),顯示記憶體(MB)");
            CurrentFile = path;
            RowCount = 0;
            _lastWrite = DateTime.MinValue;
            _startedAt = DateTime.Now;
            IsLogging = true;
            StatusText = "記錄中…";
        }
        catch (Exception ex)
        {
            StatusText = "無法開始記錄：" + ex.Message;
            IsLogging = false;
            _writer = null;
        }
    }

    /// <summary>停止記錄並關閉檔案。</summary>
    public void StopLogging()
    {
        if (!_isLogging && _writer is null) return;
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
        IsLogging = false;
        StatusText = _rowCount > 0
            ? $"已停止 ・ 共 {_rowCount} 列 ・ 歷時 {(DateTime.Now - _startedAt):hh\\:mm\\:ss}"
            : "已停止";
    }

    /// <summary>由主計時器每拍呼叫；依設定間隔決定是否寫入一列。</summary>
    public void Sample(SensorService live, SettingsService settings)
    {
        if (!_isLogging || _writer is null) return;
        var now = DateTime.Now;
        if ((now - _lastWrite).TotalSeconds + 0.05 < settings.LogIntervalSec) return;
        _lastWrite = now;
        try
        {
            var g = live.PrimaryGpu;
            var sb = new StringBuilder();
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss")).Append(',');
            sb.Append(N(live.CpuLoad)).Append(',');
            sb.Append(live.CpuTemp is double ct ? N(ct) : "").Append(',');
            sb.Append(N(live.CpuClock)).Append(',');
            sb.Append(N(live.MemLoad)).Append(',');
            sb.Append(g is not null ? N(g.LoadPercent) : "").Append(',');
            sb.Append(g?.TempC is double gt ? N(gt) : "").Append(',');
            sb.Append(g is not null ? N(g.VramUsedMB) : "");
            _writer.WriteLine(sb.ToString());
            RowCount++;
        }
        catch (Exception ex)
        {
            StatusText = "寫入中斷：" + ex.Message;
            StopLogging();
        }
    }

    public void OpenFolder(SettingsService settings)
    {
        try
        {
            Directory.CreateDirectory(settings.LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{settings.LogFolder}\"") { UseShellExecute = true });
        }
        catch { /* 開啟資料夾為選用操作 */ }
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
