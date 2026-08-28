using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>
/// DNS 快速切換分頁：自持 DnsService，載入時列舉介面卡；選定介面卡後點預設或輸入自訂位址，
/// 透過 netsh 真實寫入 DNS，並可一鍵還原 DHCP／清除快取。套用期間停用按鈕防重入。
/// </summary>
public partial class DnsView : UserControl
{
    private readonly DnsService _svc = new();
    private bool _loaded;
    private bool _busy;

    public DnsView()
    {
        InitializeComponent();
        AdapterPicker.ItemsSource = _svc.Adapters;
        PresetList.ItemsSource = DnsService.Presets;
        _svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_svc.Status))
                Dispatcher.Invoke(() => StatusText.Text = _svc.Status);
        };
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Scan(); } };
    }

    private void Scan()
    {
        _svc.Scan();
        if (AdapterPicker.SelectedIndex < 0 && _svc.Adapters.Count > 0)
            AdapterPicker.SelectedIndex = 0;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Scan();

    private NetAdapter? Selected => AdapterPicker.SelectedItem as NetAdapter;

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (Selected is not { } adapter) { StatusText.Text = "請先選擇網路介面卡。"; return; }
        if (sender is not FrameworkElement fe || fe.DataContext is not DnsPreset preset) return;
        await Guard(() => _svc.ApplyAsync(adapter, preset));
    }

    private async void ApplyCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (Selected is not { } adapter) { StatusText.Text = "請先選擇網路介面卡。"; return; }
        await Guard(() => _svc.ApplyCustomAsync(adapter, PrimaryBox.Text, SecondaryBox.Text));
    }

    private async void Flush_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await Guard(async () => { await _svc.FlushCacheAsync(); return true; });
    }

    private async Task Guard(Func<Task<bool>> action)
    {
        _busy = true;
        IsEnabled = false;
        try { await action(); }
        finally { _busy = false; IsEnabled = true; }
    }
}
