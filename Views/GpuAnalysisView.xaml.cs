using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>顯示卡分析頁：與主機板分析同模式，對象換成顯示卡。</summary>
public partial class GpuAnalysisView : UserControl, IPageLifecycle
{
    private GpuAnalysisService? _svc;
    private bool _loaded;

    public GpuAnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;

            _svc = Vm?.GpuAnalysis;
            if (_svc is null) return;

            DataContext = _svc;
            _svc.CollectGpuInfo();
            // 不在這裡建圖片搜尋：確認卡片要等使用者按下「開始分析」才出現。
        };
    }

    public void OnActivated() { }
    public void OnDeactivated() { }

    /// <summary>開始分析＝先開圖片搜尋讓使用者確認型號；AI 詳細介紹等確認後才跑。</summary>
    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_svc is null) return;
        _svc.BuildImageSearchUrl();
        OpenImage();
    }

    /// <summary>確認是這張 → 才真的去問 AI。</summary>
    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_svc is null) return;
        await _svc.ConfirmImageAsync();
        await _svc.AnalyzeAsync();
    }

    private void SearchAgain_Click(object sender, RoutedEventArgs e) => _svc?.SearchAgain();

    private void OpenImage_Click(object sender, RoutedEventArgs e) => OpenImage();

    private void OpenImage()
    {
        string? url = _svc?.ImageSearchUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 打不開瀏覽器就算了 */ }
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;
}
