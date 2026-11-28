using System.Diagnostics;
using System.IO;

namespace XinSpect;

/// <summary>Records selected live sensor identities locally. Selection is locked until the next session.</summary>
public sealed class SensorLogService : ObservableObject
{
    private readonly object _gate = new();
    private readonly Func<long> _timestamp;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, DateTimeOffset, (string Path, TextWriter Writer)> _createFile;
    private SensorCsvSession? _session;
    private SensorLogSensorDefinition[]? _pendingSensors;
    private long _startedTimestamp;
    private bool _isLogging;
    private int _rowCount;
    private long _csvRecordCount;
    private int _selectedSensorCount;
    private string _currentFile = "—";
    private string _statusSource = "尚未記錄";
    private string? _statusRawPrefix;
    private string _statusRawSuffix = "";
    private string _lastError = "";

    public SensorLogService() : this(Stopwatch.GetTimestamp, () => DateTimeOffset.UtcNow, SensorCsvFile.Create) { }
    internal SensorLogService(Func<long> timestamp, Func<DateTimeOffset> utcNow,
        Func<string, DateTimeOffset, (string Path, TextWriter Writer)> createFile)
    {
        _timestamp = timestamp; _utcNow = utcNow; _createFile = createFile;
    }

    public bool IsLogging { get => _isLogging; private set { if (SetProperty(ref _isLogging, value)) OnPropertyChanged(nameof(ButtonText)); } }
    public string ButtonText => _isLogging ? "停止記錄" : "開始記錄";
    /// <summary>Complete sampled frames; CSV has one record per selected sensor in each frame.</summary>
    public int RowCount { get => _rowCount; private set { if (SetProperty(ref _rowCount, value)) OnPropertyChanged(nameof(RowCountText)); } }
    public long CsvRecordCount { get => _csvRecordCount; private set => SetProperty(ref _csvRecordCount, value); }
    public int SelectedSensorCount { get => _selectedSensorCount; private set => SetProperty(ref _selectedSensorCount, value); }
    public string RowCountText => $"已記錄 {_rowCount} 次取樣";
    public string CurrentFile { get => _currentFile; private set => SetProperty(ref _currentFile, value); }
    public string StatusText => _statusRawPrefix is null
        ? LanguageService.T(_statusSource)
        : LanguageService.T(_statusRawPrefix) + _statusRawSuffix;
    /// <summary>Localized status projection; its binding is intentionally not translated as one mixed string.</summary>
    public string DisplayStatusText => StatusText;
    /// <summary>Retained after StopLogging; cleared only by an explicit new start.</summary>
    public string LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    /// <summary>Compatibility entry point. First Sample captures selection before creating any file.</summary>
    public void StartLogging(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (_isLogging) return;
            ResetSessionState();
            IsLogging = true;
            SetStatus("等待感測器取樣與記錄選取…");
        }
    }

    public void StartLogging(SettingsService settings, SensorService live)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(live);
        lock (_gate)
        {
            if (_isLogging) return;
            ResetSessionState();
            var sensors = CaptureSelection(live);
            if (sensors.Length == 0) { SetStatus("請先在感測器頁勾選要記錄的感測器。"); return; }
            _pendingSensors = sensors;
            SelectedSensorCount = sensors.Length;
            IsLogging = true;
            SetStatus("等待感測器取樣…");
            SampleFrame(settings, live.CurrentSensorFrame);
        }
    }

    private static SensorLogSensorDefinition[] CaptureSelection(SensorService live)
        => live.AllSensors.Where(row => row.IsSelectedForLog).Select(row =>
            new SensorLogSensorDefinition(row.Id, row.DeviceId, row.DeviceName, row.Name, row.TypeText, row.Unit)).ToArray();

    private void ResetSessionState()
    {
        LastError = ""; CurrentFile = "—"; RowCount = 0; CsvRecordCount = 0; SelectedSensorCount = 0;
        _primaryError = ""; _closeError = null;
        _pendingSensors = null;
        _startedTimestamp = _timestamp();
    }

    public void StopLogging()
    {
        lock (_gate)
        {
            if (!_isLogging && _session is null) return;
            CloseSession();
            IsLogging = false;
            _pendingSensors = null;
            if (LastError.Length != 0) { SetErrorStatus(); return; }
            var elapsed = TimeSpan.FromSeconds(Math.Max(0, ((double)_timestamp() - _startedTimestamp) / Stopwatch.Frequency));
            SetStatus(_rowCount > 0
                ? $@"已停止 ・ 共 {_rowCount} 次取樣／{_csvRecordCount} 筆感測值 ・ 歷時 {elapsed:hh\:mm\:ss}"
                : "已停止；未取得取樣");
        }
    }

    public void Sample(SensorService live, SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (!_isLogging) return;
            if (_pendingSensors is null && _session is null)
            {
                _pendingSensors = CaptureSelection(live);
                SelectedSensorCount = _pendingSensors.Length;
                if (_pendingSensors.Length == 0)
                {
                    IsLogging = false; SetStatus("請先在感測器頁勾選要記錄的感測器。"); return;
                }
            }
            SampleFrame(settings, live.CurrentSensorFrame);
        }
    }

    // Hardware-independent production seam: tests use the same file/session/error handling as Sample.
    internal void StartFrames(SettingsService settings, IReadOnlyList<SensorReading> selected)
    {
        lock (_gate)
        {
            if (_isLogging) return;
            ResetSessionState();
            if (selected.Count == 0) { SetStatus("請先在感測器頁勾選要記錄的感測器。"); return; }
            _pendingSensors = selected.Select(SensorLogSensorDefinition.FromReading).ToArray();
            SelectedSensorCount = selected.Count; IsLogging = true; SetStatus("等待感測器取樣…");
        }
    }

    internal void SampleFrame(SettingsService settings, SensorFrame frame)
    {
        lock (_gate)
        {
            if (!_isLogging || frame.Sequence <= 0 || _pendingSensors is null && _session is null) return;
            try
            {
                long now = _timestamp();
                if (_session is null)
                {
                    var (path, writer) = _createFile(settings.LogFolder, _utcNow());
                    CurrentFile = path;
                    try { _session = new SensorCsvSession(writer, _pendingSensors!, _startedTimestamp); }
                    catch
                    {
                        try { writer.Dispose(); } catch { /* Preserve the primary initialization failure. */ }
                        throw;
                    }
                    _pendingSensors = null;
                }
                var maxAge = TimeSpan.FromSeconds(Math.Max(5, settings.UpdateIntervalSec * 3));
                if (_session.TryWriteFrame(frame, now, TimeSpan.FromSeconds(settings.LogIntervalSec), maxAge))
                {
                    RowCount = _session.FrameCount; CsvRecordCount = _session.RecordCount;
                    SetStatus($"記錄中… {_session.SensorCount} 個感測器；選取變更於下次記錄生效");
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _statusRawPrefix = null;
                _statusRawSuffix = "";
                _primaryError = ex.Message;
                CloseSession();
                _pendingSensors = null; IsLogging = false;
                SetErrorStatus();
            }
        }
    }

    private void CloseSession()
    {
        var session = _session; _session = null;
        try { session?.Dispose(); }
        catch (Exception ex)
        {
            if (LastError.Length == 0)
            {
                LastError = ex.Message;
                _primaryError = ex.Message;
            }
            else
            {
                LastError += "；關閉檔案：" + ex.Message;
                _closeError = ex.Message;
            }
        }
    }

    public void OpenFolder(SettingsService settings)
    {
        try
        {
            Directory.CreateDirectory(settings.LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{settings.LogFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SetMixedStatus("無法開啟記錄資料夾：", ex.Message); }
    }

    private string _primaryError = "";
    private string? _closeError;

    private void SetStatus(string source)
    {
        _statusSource = source;
        _statusRawPrefix = null;
        _statusRawSuffix = "";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DisplayStatusText));
    }

    private void SetMixedStatus(string productPrefix, string rawSuffix)
    {
        _statusSource = "";
        _statusRawPrefix = productPrefix;
        _statusRawSuffix = rawSuffix;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DisplayStatusText));
    }

    private void SetErrorStatus()
    {
        _statusSource = "";
        _statusRawPrefix = "記錄失敗：";
        _statusRawSuffix = _primaryError
            + (_closeError is null ? "" : LanguageService.T("；關閉檔案：") + _closeError);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DisplayStatusText));
    }
}
