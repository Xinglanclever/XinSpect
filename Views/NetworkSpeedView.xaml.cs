using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>網速測試分頁：選節點後量測延遲／下載／上傳，IProgress 於 UI 執行緒即時更新。</summary>
public partial class NetworkSpeedView : UserControl
{
    private readonly NetworkSpeedService _svc = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public NetworkSpeedView()
    {
        InitializeComponent();
        NodePicker.ItemsSource = NetworkSpeedService.Nodes;
        NodePicker.SelectedIndex = 0;   // 預設 NTU 台大
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { _cts?.Cancel(); return; }
        if (NodePicker.SelectedItem is not SpeedNode node) return;

        // WebPage 節點（如 HKBN、測速網）無可供程式直接量測的公開端點，改跳轉至內建瀏覽器開啟其官方測速頁，
        // 並帶上返回鍵：瀏覽器工具列會出現「回到測速頁面」，測完不用自己找路回來。
        if (node.Protocol == NodeProtocol.WebPage)
        {
            Shell.Main?.NavigateToBrowser(node.WebUrl, "netspeed");
            LiveText.Text = "已跳轉至內建瀏覽器";
            StatusText.Text = $"{node.Name} 未提供可供程式直接量測的公開端點，已跳轉至內建瀏覽器開啟其官方測速頁。";
            return;
        }

        _running = true;
        StartBtn.Content = "取消";
        NodePicker.IsEnabled = false;
        PingText.Text = DownText.Text = UpText.Text = "—";
        LiveText.Text = "準備中…";
        StatusText.Text = "";

        _cts = new CancellationTokenSource();
        var progress = new Progress<SpeedSample>(Update);
        try
        {
            await _svc.RunAsync(node, progress, _cts.Token);
        }
        finally
        {
            _running = false;
            StartBtn.Content = "開始測速";
            NodePicker.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Update(SpeedSample s)
    {
        if (s.PingMs > 0) PingText.Text = s.PingMs.ToString("0.0");
        if (s.DownMbps > 0) DownText.Text = s.DownMbps.ToString("0.0");
        if (s.UpMbps > 0) UpText.Text = s.UpMbps.ToString("0.0");

        LiveText.Text = s.Phase switch
        {
            "下載" when !s.Done => $"下載中 ・ 目前 {s.LiveMbps:0.0} Mbps（{s.LiveMbps / 8.0:0.0} MB/s）",
            "上傳" when !s.Done => $"上傳中 ・ 目前 {s.LiveMbps:0.0} Mbps（{s.LiveMbps / 8.0:0.0} MB/s）",
            "完成" => "測速完成",
            "取消" => "已取消",
            "錯誤" => "測試中止",
            _ => s.Status,
        };
        StatusText.Text = s.Status;
    }
}
