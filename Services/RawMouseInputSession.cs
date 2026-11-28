using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>只在明確開始後接受本視窗 WM_INPUT；停止／關窗即解除自己的註冊。</summary>
internal sealed class RawMouseInputSession : IDisposable
{
    private readonly HwndSource _source;
    private readonly RawMouseCapture _capture = new(new RawMouseNativeApi(), Stopwatch.GetTimestamp,
        () => DateTimeOffset.UtcNow, Stopwatch.Frequency);
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    internal event Action? Updated;

    internal RawMouseInputSession(HwndSource source)
    {
        _source = source;
        _source.AddHook(Hook);
        _timer = new DispatcherTimer(DispatcherPriority.Background, source.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _capture.CheckDeadline();
        if (!_capture.IsRunning) _timer.Stop();
        Updated?.Invoke();
    }

    internal bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool started = _capture.Start(_source.Handle);
        if (started) _timer.Start();
        Updated?.Invoke();
        return started;
    }

    internal RawMouseSnapshot Snapshot() => _capture.Snapshot();
    internal void Reset()
    {
        if (_disposed) return;
        _timer.Stop(); _capture.Reset(); Updated?.Invoke();
    }
    internal void Stop()
    {
        if (_disposed) return;
        _timer.Stop(); _capture.Stop(); Updated?.Invoke();
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // handled 保持 false：RIM_INPUT 必須繼續交給 DefWindowProc 清理。
        // https://learn.microsoft.com/windows/win32/inputdev/wm-input
        if (!_disposed && _capture.IsRunning)
        {
            if (message == 0x00FF && wParam == 0) _capture.ProcessInput(lParam);
            else if (message == 0x00FE && wParam == 2) _capture.DeviceRemoved(lParam);
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop(); _timer.Tick -= Timer_Tick; _capture.Dispose();
        if (!_source.IsDisposed) _source.RemoveHook(Hook);
        _disposed = true;
    }
}

/// <summary>可注入 Win32／鐘的生產控制器；每個輸入處理 O(1)，統計只在 UI 更新時取快照。</summary>
internal sealed class RawMouseCapture : IDisposable
{
    private static readonly object OwnershipLock = new();
    private static RawMouseCapture? _owner;
    private readonly IRawMouseNativeApi _native;
    private readonly Func<long> _timestamp;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly long _frequency;
    private readonly Dictionary<nint, (string Id, string Display, bool Readable)> _identities = new();
    private RawMouseSampling? _sampling;
    private nint _target;
    private string? _error;
    private bool _registered, _disposed;

    internal RawMouseCapture(IRawMouseNativeApi native, Func<long> timestamp, Func<DateTimeOffset> utcNow, long frequency)
    { _native = native; _timestamp = timestamp; _utcNow = utcNow; _frequency = frequency; }
    internal bool IsRunning => _sampling?.IsRunning == true;

    internal bool Start(nint target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return true;
        _error = null; _identities.Clear();
        _sampling = new(_frequency, _timestamp(), _utcNow());
        lock (OwnershipLock)
        {
            if (target == 0 || (_owner != null && _owner != this))
                return FailStart("另一個滑鼠取樣器正在使用 Raw Input，或視窗尚未就緒。");
            try
            {
                if (!_native.TryGetMouseRegistration(out nint existing, out string? error))
                    return FailStart(error ?? "無法確認既有 Raw Input 註冊。");
                if (existing != 0) return FailStart("本程序已有 Raw Input 滑鼠接收者；未覆寫既有註冊。");
                if (!_native.Register(target, false, out error)) return FailStart(error ?? "Raw Input 註冊失敗。");
                _target = target; _registered = true; _owner = this;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return FailStart($"Raw Input 啟動失敗（{ex.GetType().Name}）。"); }
        }
    }

    private bool FailStart(string error)
    { _error = error; _sampling!.Stop(_timestamp(), error); return false; }

    internal void ProcessInput(nint input)
    {
        long now = _timestamp(); // 在任何解碼與裝置名稱查詢之前取 QPC；不是裝置內部報告時間
        if (!IsRunning) return;
        if (_sampling!.IsExpired(now)) { Stop(); return; }
        try
        {
            if (!_native.TryRead(input, out var packet, out string? error))
            {
                _sampling.ReadError(); _error = error ?? "Raw Input 讀取失敗。";
                Stop(); return;
            }
            if (!_identities.TryGetValue(packet.Device, out var identity))
            {
                if (_identities.Count >= RawMouseSampling.MaximumDevices)
                {
                    _sampling.Add(packet.Device, "ignored", "超出裝置上限", false, now, packet.X, packet.Y, packet.Flags);
                    return;
                }
                var (id, readable) = _native.Identify(packet.Device);
                identity = (id, $"滑鼠 {_identities.Count + 1} · {id}", readable);
                _identities.Add(packet.Device, identity);
            }
            _sampling.Add(packet.Device, identity.Id, identity.Display, identity.Readable, now, packet.X, packet.Y, packet.Flags);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _error = $"Raw Input 處理失敗（{ex.GetType().Name}）。"; Stop(); }
    }

    internal void DeviceRemoved(nint handle) => _sampling?.Remove(handle);
    internal void CheckDeadline() { if (IsRunning && _sampling!.IsExpired(_timestamp())) Stop(); }
    internal void Reset()
    {
        Stop(); _sampling = null; _identities.Clear(); _error = null;
    }

    internal void Stop()
    {
        _sampling?.Stop(_timestamp(), _error);
        lock (OwnershipLock)
        {
            if (!_registered) return;
            try
            {
                // 別人若在期間改了註冊，不能移除對方的接收者。
                if (!_native.TryGetMouseRegistration(out nint current, out string? error))
                    _error = error ?? "無法確認 Raw Input 註冊所有權；未移除未知接收者。";
                else if (current != _target)
                    _error = "Raw Input 註冊所有權已改變；未移除其他接收者，本窗結果不完整。";
                else if (!_native.Register(_target, true, out error))
                    _error = error ?? "Raw Input 註冊移除失敗。";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { _error = $"Raw Input 停止失敗（{ex.GetType().Name}）。"; }
            finally { _registered = false; if (_owner == this) _owner = null; }
        }
    }

    internal RawMouseSnapshot Snapshot()
    {
        var sample = _sampling?.Snapshot(_timestamp()) ?? new(false, FactAvailability.Unknown,
            "尚未開始；按「開始取樣」後在前景持續移動滑鼠，最長 10 秒。", default, default, 0, [], 0, 0);
        return _error is null ? sample : sample with { Availability = FactAvailability.ReadError, Status = _error };
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop(); _native.Dispose(); _disposed = true;
    }
}
