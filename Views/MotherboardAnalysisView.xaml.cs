using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>主機板分析頁：WMI 讀型號 → AI 查詳細規格，中間夾一道「是不是這張板子」的確認。</summary>
public partial class MotherboardAnalysisView : UserControl, IPageLifecycle
{
    private MotherboardAnalysisService? _svc;
    private bool _loaded;

    public MotherboardAnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;

            _svc = Vm?.MotherboardAnalysis;
            if (_svc is null) return;

            DataContext = _svc;
            _svc.CollectBoardInfo();
            // 不在這裡建圖片搜尋：確認卡片要等使用者按下「開始分析」才出現，
            // 否則一進頁就寫著「已開啟圖片搜尋」而其實什麼都沒開。
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
