using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>
/// 強控台（Power Console）：瑞士工業風的工程檢視視窗。
/// </summary>
/// <remarks>
/// <para>
/// 版面語言沿用使用者提供的概念稿（等寬字、直角、編號 SECTION、INDEX 側欄），
/// 但<b>資料全部接真實生產端</b>：01 綁主 VM 的即時感測緩衝、05 當場計算本機檔案的 SHA-256。
/// 還沒接到真資料源的 SECTION（02 Ring0、03 藍色中隊、04 Bridge IPC）如實標「本版未接入」，
/// 不放概念示意圖或模擬數字冒充功能。
/// </para>
/// <para>
/// 本視窗<b>唯讀</b>：不寫任何 MSR／PCI／硬體暫存器。
/// </para>
/// </remarks>
public partial class PowerConsoleWindow : Window
{
    private readonly DispatcherTimer _stamp;

    public PowerConsoleWindow()
    {
        InitializeComponent();
        DataContext = Shell.Vm;

        _stamp = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stamp.Tick += (_, _) => LiveStamp.Text = "LIVE " + DateTime.Now.ToString("HH:mm:ss");
        _stamp.Start();

        Show("s1");
        Loaded += (_, _) => ComputeHashes();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
        => Show((sender as FrameworkElement)?.Tag as string ?? "s1");

    private void Show(string tag)
    {
        S1.Visibility = tag == "s1" ? Visibility.Visible : Visibility.Collapsed;
        S2.Visibility = tag == "s2" ? Visibility.Visible : Visibility.Collapsed;
        S3.Visibility = tag == "s3" ? Visibility.Visible : Visibility.Collapsed;
        S4.Visibility = tag == "s4" ? Visibility.Visible : Visibility.Collapsed;
        S5.Visibility = tag == "s5" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "s2" && _pciDevices is null && !_pciScanning) ScanPci();
        if (tag == "s4") SyncIpcStatus();
    }

    // ── SECTION 04：Bridge IPC（走主程式既有的引擎與協定，真 JSON 進出） ────

    private BlueSquadronEngine? BridgeEngine
        => (DataContext as MainViewModel)?.BlueSquadron.Engine;

    private void SyncIpcStatus()
    {
        var engine = BridgeEngine;
        IpcStatus.Text = engine is { Connected: true } ? "BRIDGE CONNECTED"
            : engine is { } ? "BRIDGE NOT RESPONDING"
            : "BRIDGE 未啟動（到主介面安全頁開啟藍色中隊）";
    }

    private void IpcPreset_Click(object sender, RoutedEventArgs e)
    {
        IpcRequestBox.Text = (sender as FrameworkElement)?.Tag switch
        {
            "ping" => "{\"cmd\":\"ping\"}",
            "status" => "{\"cmd\":\"status\"}",
            "posture" => "{\"cmd\":\"posture\"}",
            "threats" => "{\"cmd\":\"threats\"}",
            "baseline" => "{\"cmd\":\"baseline\"}",
            "snapshot" => "{\"cmd\":\"snapshot\"}",
            "probe" => "{\"cmd\":\"probe\"}",
            _ => IpcRequestBox.Text,
        };
    }

    private void IpcSend_Click(object sender, RoutedEventArgs e)
    {
        var engine = BridgeEngine;
        if (engine is null || !engine.Connected)
        {
            IpcResponseBox.Text = "// Bridge 沒有連線。到主介面「安全」頁開啟藍色中隊（會啟動 BlueSquadronBridge.exe），"
                + "或把 BlueSquadronBridge.exe 與主程式放同一資料夾。";
            return;
        }
        // 引擎的協定是「方法名稱＋可選參數」；這裡只收方法名稱，避免把任意 JSON 直接注入。
        string method = IpcRequestBox.Text.Trim();
        if (method.Length == 0)
        {
            IpcResponseBox.Text = "// 請輸入指令（或點上面的預設按鈕）。";
            return;
        }
        if (method.StartsWith('{'))
        {
            // 允許 {"cmd":"..."} 形式：取出 cmd 欄位再送
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(method);
                if (!doc.RootElement.TryGetProperty("cmd", out var c) || c.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    IpcResponseBox.Text = "// JSON 缺少 \"cmd\" 字串欄位。";
                    return;
                }
                method = c.GetString()!;
            }
            catch (System.Text.Json.JsonException ex)
            {
                IpcResponseBox.Text = "// JSON 解析失敗：" + ex.Message;
                return;
            }
        }

        IpcSendButton.IsEnabled = false;
        string cmd = method;
        Task.Run(() => engine.SendCmd(cmd, 10000))
            .ContinueWith(t =>
            {
                IpcSendButton.IsEnabled = true;
                SyncIpcStatus();
                var resp = t.Result;
                IpcResponseBox.Text = resp is { } json
                    ? json.GetRawText()
                    : "// 無回應（逾時或 Bridge 已停止）。";
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── SECTION 02：PCI 設定空間＋MSR（全唯讀） ─────────────────────────────

    private IReadOnlyList<PciConsoleDevice>? _pciDevices;
    private bool _pciScanning;

    private void ScanPci()
    {
        _pciScanning = true;
        PciStatus.Text = "掃描中…（bus 0–255，背景執行）";
        PciList.ItemsSource = null;
        PciDetail.Text = "";
        Task.Run(() => PowerConsoleRing0Service.ScanPci())
            .ContinueWith(t =>
            {
                _pciScanning = false;
                if (t.Result.Devices is not { } devices)
                {
                    PciStatus.Text = "掃描失敗：" + t.Result.Error;
                    return;
                }
                _pciDevices = devices;
                PciStatus.Text = $"找到 {devices.Count} 台裝置（唯讀快照）。";
                PciList.ItemsSource = devices
                    .OrderBy(d => d.Bus).ThenBy(d => d.Device).ThenBy(d => d.Function)
                    .Select(d => $"{d.Bus:X2}:{d.Device:X2}.{d.Function:X0}  {d.VendorId:X4}:{d.DeviceId:X4}  {d.Name}")
                    .ToList();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void PciScan_Click(object sender, RoutedEventArgs e)
    {
        if (!_pciScanning) ScanPci();
    }

    private void PciList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pciDevices is null || PciList.SelectedIndex < 0) return;
        var ordered = _pciDevices.OrderBy(d => d.Bus).ThenBy(d => d.Device).ThenBy(d => d.Function).ToList();
        if (PciList.SelectedIndex >= ordered.Count) return;
        var d = ordered[PciList.SelectedIndex];

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{d.Bus:X2}:{d.Device:X2}.{d.Function:X0}  {d.Name}");
        sb.AppendLine($"VEN {d.VendorId:X4} ・ DEV {d.DeviceId:X4} ・ 類別 {d.BaseClass:X2}{d.SubClass:X2} ・ ProgIf {d.ProgIf:X2} ・ Rev {d.Revision:X2}");
        for (int row = 0; row < 16; row++)
        {
            sb.Append($"{row * 16:X2}  ");
            for (int col = 0; col < 16; col++)
                sb.Append(((d.ConfigSpace[row] >> ((col % 4) * 8)) & 0xFF).ToString("X2")).Append(' ');
            sb.AppendLine();
        }
        PciDetail.Text = sb.ToString();
    }

    private void MsrRead_Click(object sender, RoutedEventArgs e)
    {
        string text = MsrIndexBox.Text.Trim().Replace("0x", "").Replace("0X", "");
        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint index))
        {
            MsrResult.Text = "索引格式不對：請輸入十六進位，例如 0x19C。";
            return;
        }
        MsrReadButton.IsEnabled = false;
        Task.Run(() => PowerConsoleRing0Service.ReadMsr(index))
            .ContinueWith(t =>
            {
                MsrReadButton.IsEnabled = true;
                var r = t.Result;
                MsrResult.Text = r.Error is { } err ? $"MSR 0x{r.Index:X8}：{err}"
                    : $"MSR 0x{r.Index:X8} = 0x{r.Value:X16}\nEDX 0x{r.Edx:X8} ・ EAX 0x{r.Eax:X8}";
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>當場計算本機部署物的 SHA-256。檔案不存在就說不存在，不列空列。</summary>
    private void ComputeHashes()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null || !File.Exists(exe))
            {
                HashList.Text = "找不到目前執行檔的路徑，無法計算。";
                return;
            }

            string dir = Path.GetDirectoryName(exe)!;
            var rows = new List<(string Name, string Path)> { ("XinSpect.exe", exe) };
            foreach (string name in new[] { "BlueSquadronBridge.exe", "XinSpectDeploy.exe" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) rows.Add((name, p));
            }

            HashList.Inlines.Clear();
            foreach (var (name, path) in rows)
            {
                var fi = new FileInfo(path);
                string hash;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                    hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();

                var run = new System.Windows.Documents.Run(
                    $"{name}  ·  {fi.Length:N0} bytes\n  {hash}\n\n")
                {
                    FontFamily = (FontFamily)Resources["MonoFont"],
                };
                HashList.Inlines.Add(run);
            }

            HashNote.Text = "以下雜湊是剛剛當場對本機檔案計算的 SHA-256；與 GitHub Release 的 SHA256SUMS.txt 比對即可驗證完整性"
                + "（本控台不連網，下載清單請自行開 Release 頁）。";
        }
        catch (Exception ex)
        {
            HashList.Text = "計算失敗：" + ex.GetType().Name + "：" + ex.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _stamp.Stop();
        base.OnClosed(e);
    }
}
