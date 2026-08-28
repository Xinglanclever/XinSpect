using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 會動的 GIF 顯示控制項：WPF 內建的 Image 只顯示 GIF 首格，故此處以 GifBitmapDecoder 取出各格，
/// 依 GIF 內記錄的每格延遲（中繼資料 /grctlext/Delay，單位 1/100 秒）以 DispatcherTimer 逐格輪播。
/// 僅在控制項可見時輪播、離開分頁時停止，不空耗；來源以 pack URI 指定（隨組件內嵌）。零外部相依。
/// </summary>
public sealed class GifImage : Image
{
    public static readonly DependencyProperty GifSourceProperty =
        DependencyProperty.Register(nameof(GifSource), typeof(string), typeof(GifImage),
            new PropertyMetadata(null, OnGifSourceChanged));

    /// <summary>GIF 來源（pack URI 或絕對／相對路徑）。</summary>
    public string? GifSource
    {
        get => (string?)GetValue(GifSourceProperty);
        set => SetValue(GifSourceProperty, value);
    }

    private BitmapFrame[]? _frames;
    private int[]? _delaysMs;
    private int _index;
    private readonly DispatcherTimer _timer = new();

    public GifImage()
    {
        _timer.Tick += OnTick;
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
        Unloaded += (_, _) => Stop();
    }

    private static void OnGifSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GifImage g) g.Load(e.NewValue as string);
    }

    private void Load(string? uri)
    {
        Stop();
        _frames = null; _delaysMs = null; _index = 0;
        if (string.IsNullOrEmpty(uri)) return;
        try
        {
            var decoder = new GifBitmapDecoder(new Uri(uri, UriKind.RelativeOrAbsolute),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            int n = decoder.Frames.Count;
            _frames = new BitmapFrame[n];
            _delaysMs = new int[n];
            for (int i = 0; i < n; i++)
            {
                var f = decoder.Frames[i];
                f.Freeze();
                _frames[i] = f;
                int centiSec = 10;   // 預設每格 100ms
                try
                {
                    if (f.Metadata is BitmapMetadata md && md.ContainsQuery("/grctlext/Delay")
                        && md.GetQuery("/grctlext/Delay") is ushort d16 && d16 > 0)
                        centiSec = d16;
                }
                catch { /* 少數 GIF 缺延遲中繼資料，採預設 */ }
                _delaysMs[i] = centiSec * 10;
            }
            Source = n > 0 ? _frames[0] : null;
            if (IsVisible) Start();
        }
        catch { /* 無效 GIF 或找不到資源時靜默，不影響版面 */ }
    }

    private void Start()
    {
        if (_frames is null || _frames.Length <= 1) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _delaysMs![_index]));
        _timer.Start();
    }

    private void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames is null || _frames.Length == 0) return;
        _index = (_index + 1) % _frames.Length;
        Source = _frames[_index];
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _delaysMs![_index]));
    }
}
