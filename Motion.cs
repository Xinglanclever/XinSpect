using System.ComponentModel;

namespace XinSpect;

/// <summary>
/// 全站動態視覺效果的總開關（環境值）。<see cref="SettingsService.MotionEnabled"/> 是持久化的
/// 那一份，這裡是「畫圖的控制項當下能不能跑計時器」的即時答案。
/// </summary>
/// <remarks>
/// 為什麼要用靜態環境值而不是把設定傳進每個控制項：會動的控制項（<see cref="OutlineDigits"/>、
/// <see cref="CoreColumns"/>、<see cref="FanBlade"/>）散落在各分頁，很多是 XAML 直接宣告、沒有
/// 建構式注入的機會；而它們要問的只是一個布林值。用靜態值換來的代價是測試需要能重設，故
/// <see cref="Attach"/> 可重複呼叫、且會先解除上一次的訂閱。
/// <para>
/// <see cref="Suspend"/> 是另一條路徑：跑分／撞牆量測期間要讓畫面完全靜止，但不該去改使用者
/// 存起來的設定。兩者是「與」的關係——任何一邊說不動，就不動。
/// </para>
/// </remarks>
public static class Motion
{
    private static SettingsService? _settings;
    private static bool _setting = true;
    private static int _suspends;

    /// <summary>當下是否允許動畫。控制項每次要啟動計時器前都該問一次。</summary>
    public static bool Enabled => _setting && _suspends == 0;

    /// <summary><see cref="Enabled"/> 的值改變時觸發（設定被切換或量測結束）。</summary>
    public static event Action? Changed;

    /// <summary>綁上設定服務；可重複呼叫（會先解除舊訂閱），測試用得到。</summary>
    public static void Attach(SettingsService settings)
    {
        if (ReferenceEquals(_settings, settings)) return;
        if (_settings is not null) _settings.PropertyChanged -= OnSettingChanged;
        _settings = settings;
        _settings.PropertyChanged += OnSettingChanged;
        Apply(settings.MotionEnabled);
    }

    private static void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsService.MotionEnabled) && sender is SettingsService s)
            Apply(s.MotionEnabled);
    }

    private static void Apply(bool value)
    {
        if (_setting == value) return;
        bool before = Enabled;
        _setting = value;
        if (Enabled != before) Changed?.Invoke();
    }

    /// <summary>
    /// 量測期間暫停所有動畫，回傳的物件釋放時恢復。可疊套（巢狀量測各自持有一份）。
    /// </summary>
    public static IDisposable Suspend()
    {
        bool before = Enabled;
        _suspends++;
        if (Enabled != before) Changed?.Invoke();
        return new Resume();
    }

    private sealed class Resume : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            bool before = Enabled;
            if (_suspends > 0) _suspends--;
            if (Enabled != before) Changed?.Invoke();
        }
    }
}
