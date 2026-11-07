using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace XinSpect;

/// <summary>
/// DNS 快速切換：列舉實體網路介面卡與其目前 DNS 伺服器，透過 Windows 內建 netsh 真實寫入
/// 靜態 DNS 或還原為自動（DHCP）。內建常見公共 DNS 預設（Cloudflare／Google／AliDNS／DNSPod／
/// 114／Quad9），亦可自訂。變更 DNS 需系統管理員權限（本程式 manifest 已要求）。純本機、無第三方相依。
/// </summary>
public sealed class NetAdapter : INotifyPropertyChanged
{
    public string Name { get; init; } = "";           // 連線名稱（netsh 使用），如「乙太網路」
    /// <summary>審計 #36：介面名稱可能含雙引號（惡意 DHCP 主機設定），netsh 內插前跳脫。</summary>
    public string SafeName => Name.Replace("\"", "").Replace("\\", "");
    public string Description { get; init; } = "";     // 介面卡描述（晶片型號）

    private string _dnsText = "";
    public string DnsText { get => _dnsText; set { _dnsText = value; OnChanged(nameof(DnsText)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class DnsPreset
{
    public string Name { get; init; } = "";
    public string Note { get; init; } = "";
    public bool Auto { get; init; }                    // 還原為 DHCP 自動取得
    public string V4Primary { get; init; } = "";
    public string V4Secondary { get; init; } = "";
    public string V6Primary { get; init; } = "";
    public string V6Secondary { get; init; } = "";
}

public sealed class DnsService : INotifyPropertyChanged
{
    // 〔修復 v3.20〕zh-TW 系統的 netsh／ipconfig 輸出走主控台字碼頁 950（Big5），用 UTF8
    // 解碼是亂碼：型別初始化時註冊 CodePagesEncodingProvider（僅此一次），之後一律以 950
    // 解碼；註冊或 950 取不到就退回 UTF8（寧可解不出來，不能讓行程起不來）。
    private static readonly Encoding ConsoleEncoding;

    static DnsService()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ConsoleEncoding = Encoding.GetEncoding(950);
        }
        catch { ConsoleEncoding = Encoding.UTF8; }   // 提供者不可用時的後備
    }

    public ObservableCollection<NetAdapter> Adapters { get; } = new();

    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public static readonly DnsPreset[] Presets =
    {
        new() { Name = "自動（DHCP）", Note = "還原自動取得", Auto = true },
        new() { Name = "Cloudflare", Note = "1.1.1.1", V4Primary = "1.1.1.1", V4Secondary = "1.0.0.1",
                V6Primary = "2606:4700:4700::1111", V6Secondary = "2606:4700:4700::1001" },
        new() { Name = "Google", Note = "8.8.8.8", V4Primary = "8.8.8.8", V4Secondary = "8.8.4.4",
                V6Primary = "2001:4860:4860::8888", V6Secondary = "2001:4860:4860::8844" },
        new() { Name = "阿里 AliDNS", Note = "223.5.5.5", V4Primary = "223.5.5.5", V4Secondary = "223.6.6.6" },
        new() { Name = "騰訊 DNSPod", Note = "119.29.29.29", V4Primary = "119.29.29.29", V4Secondary = "182.254.116.116" },
        new() { Name = "114 DNS", Note = "114.114.114.114", V4Primary = "114.114.114.114", V4Secondary = "114.114.115.115" },
        new() { Name = "Quad9", Note = "9.9.9.9", V4Primary = "9.9.9.9", V4Secondary = "149.112.112.112",
                V6Primary = "2620:fe::fe", V6Secondary = "2620:fe::9" },
    };

    public void Scan()
    {
        Adapters.Clear();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                // 只列可用的實體乙太／無線介面，排除回送、通道、軟體虛擬
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = ni.GetIPProperties();
                var dns = props.DnsAddresses
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(a => a.ToString())
                    .ToArray();

                Adapters.Add(new NetAdapter
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    DnsText = dns.Length == 0 ? "（未設定）" : string.Join("　", dns),
                });
            }
            Status = Adapters.Count == 0
                ? "未偵測到使用中的網路介面卡。"
                : $"共 {Adapters.Count} 個使用中的網路介面卡。";
        }
        catch (Exception ex)
        {
            Status = "列舉網路介面卡失敗：" + ex.Message;
        }
    }

    /// <summary>套用預設或自訂 DNS 到指定介面卡；Auto 則還原 DHCP。每條 netsh 的退出碼都檢查，
    /// 失敗如實回報（P2-36：不再靜默降級）——「已還原／已套用」只在全部成功時說。</summary>
    public async Task<bool> ApplyAsync(NetAdapter adapter, DnsPreset preset)
    {
        if (adapter == null) { Status = "請先選擇網路介面卡。"; return false; }
        try
        {
            if (preset.Auto)
            {
                // DHCP 還原（P2-36 餘項）：兩條的退出碼都檢查，不再丟棄——
                // netsh 失敗而照樣報「已還原自動取得」＝靜默降級，使用者會以為介面已回到 DHCP。
                var (v4code, v4err) = await RunNetsh("interface", "ipv4", "set", "dnsservers",
                    "name=" + adapter.SafeName, "source=dhcp");
                if (v4code != 0)
                {
                    Status = $"還原 IPv4 DNS 為 DHCP 失敗（netsh 代碼 {v4code}）：{v4err}";
                    return false;
                }
                var (v6code, v6err) = await RunNetsh("interface", "ipv6", "set", "dnsservers",
                    "name=" + adapter.SafeName, "source=dhcp");
                if (v6code != 0)
                {
                    // IPv4 已成功、IPv6 失敗（未啟用 IPv6 的機器常見）——如實回報部分成功。
                    // 〔修復建置 2026-11-08〕await 收尾（CS4014 基線歸零）：快取沖洗完再報狀態，
                    // 不留沒人等的尾巴。
                    await FlushAsync();
                    RefreshOne(adapter);
                    Status = $"IPv4 已還原為 DHCP，但 IPv6 還原失敗（netsh 代碼 {v6code}）：{v6err}。";
                    return false;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(preset.V4Primary))
                {
                    var (code, err) = await RunNetsh("interface", "ipv4", "set", "dnsservers",
                        "name=" + adapter.SafeName, "source=static", "address=" + preset.V4Primary,
                        "register=primary", "validate=no");
                    if (code != 0)
                    {
                        Status = $"設定 IPv4 DNS 失敗（netsh 代碼 {code}）：{err}";
                        return false;
                    }
                    if (!string.IsNullOrWhiteSpace(preset.V4Secondary))
                    {
                        var (addCode, addErr) = await RunNetsh("interface", "ipv4", "add", "dnsservers",
                            "name=" + adapter.SafeName, "address=" + preset.V4Secondary, "index=2", "validate=no");
                        if (addCode != 0)
                        {
                            Status = $"新增 IPv4 次要 DNS 失敗（netsh 代碼 {addCode}）：{addErr}";
                            return false;
                        }
                    }
                }
                // 僅在預設提供 IPv6 位址時才動 IPv6，避免影響原設定
                if (!string.IsNullOrWhiteSpace(preset.V6Primary))
                {
                    var (code, err) = await RunNetsh("interface", "ipv6", "set", "dnsservers",
                        "name=" + adapter.SafeName, "source=static", "address=" + preset.V6Primary,
                        "register=primary", "validate=no");
                    if (code != 0)
                    {
                        Status = $"設定 IPv6 DNS 失敗（netsh 代碼 {code}）：{err}";
                        return false;
                    }
                    if (!string.IsNullOrWhiteSpace(preset.V6Secondary))
                    {
                        var (addCode, addErr) = await RunNetsh("interface", "ipv6", "add", "dnsservers",
                            "name=" + adapter.SafeName, "address=" + preset.V6Secondary, "index=2", "validate=no");
                        if (addCode != 0)
                        {
                            Status = $"新增 IPv6 次要 DNS 失敗（netsh 代碼 {addCode}）：{addErr}";
                            return false;
                        }
                    }
                }
            }

            await FlushAsync();   // 〔修復建置 2026-11-08〕CS4014 歸零：沖洗完再報成功
            RefreshOne(adapter);
            Status = preset.Auto
                ? $"「{adapter.SafeName}」已還原為自動取得 DNS（DHCP）。"
                : $"「{adapter.SafeName}」已套用 {preset.Name} DNS（{preset.Note}）。";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Status = "權限不足，請以系統管理員身分執行。";
            return false;
        }
        catch (Exception ex)
        {
            Status = "套用 DNS 失敗：" + ex.Message;
            return false;
        }
    }

    /// <summary>套用自訂 DNS。primary 必填、secondary 選填；依位址自動判別 IPv4／IPv6。</summary>
    public Task<bool> ApplyCustomAsync(NetAdapter adapter, string primary, string secondary)
    {
        primary = (primary ?? "").Trim();
        secondary = (secondary ?? "").Trim();
        if (!IsValidIp(primary))
        {
            Status = "主要 DNS 位址格式不正確。";
            return Task.FromResult(false);
        }
        if (secondary.Length > 0 && !IsValidIp(secondary))
        {
            Status = "次要 DNS 位址格式不正確。";
            return Task.FromResult(false);
        }
        bool v6 = primary.Contains(':');
        var preset = new DnsPreset
        {
            Name = "自訂", Note = primary,
            V4Primary = v6 ? "" : primary, V4Secondary = v6 ? "" : secondary,
            V6Primary = v6 ? primary : "", V6Secondary = v6 ? secondary : "",
        };
        return ApplyAsync(adapter, preset);
    }

    // 〔修復 v3.20〕回傳 ipconfig 的退出碼（0＝成功；-1＝無法啟動或執行例外）——先前吞掉
    // 所有錯誤、回傳 void，呼叫端只能瞎報成功。套用流程裡的兩個呼叫點維持射後不理
    // （套用成敗由 netsh 的結果認定），「清除快取」那條路則據此如實分報。
    public async Task<int> FlushAsync()
    {
        try
        {
            var (code, _) = await RunProcess("ipconfig", "/flushdns");
            return code;
        }
        catch { return -1; }
    }

    public async Task FlushCacheAsync()
    {
        int code = await FlushAsync();
        // 〔修復 v3.20〕成敗如實分報：先前不管 ipconfig 結果如何一律說「已清除」。
        Status = code == 0
            ? "已清除 DNS 解析快取。"
            : code > 0
                ? $"清除 DNS 快取失敗（ipconfig 代碼 {code}）：請確認以系統管理員身分執行後再試。"
                : "清除 DNS 快取失敗：無法執行 ipconfig /flushdns（行程沒能啟動）。";
    }

    private void RefreshOne(NetAdapter adapter)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => x.Name == adapter.Name);
            if (ni == null) return;
            var dns = ni.GetIPProperties().DnsAddresses
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(a => a.ToString()).ToArray();
            adapter.DnsText = dns.Length == 0 ? "（未設定）" : string.Join("　", dns);
        }
        catch { /* 讀不到就維持原顯示 */ }
    }

    private static bool IsValidIp(string s) =>
        !string.IsNullOrWhiteSpace(s) && System.Net.IPAddress.TryParse(s, out _);

    private static Task<(int code, string err)> RunNetsh(params string[] args) => RunProcess("netsh", args);

    /// <summary>
    /// 全程 ArgumentList（P2-36 餘項）：netsh 的每個參數以獨立 argv 元素傳遞，Windows 建立行程時
    /// 依 CRT 規則引號——介面名稱含空格／特殊字元不再有拼接面，也不必手動包 name="…"。
    /// <para>
    /// <b>SafeName 消毒保留說明</b>：ArgumentList 化之後 SafeName（剝除 " 與 \）仍是第二層防禦，
    /// 不是多餘物——netsh 內部會再把 name=… 的值拼進它自己的字串處理；消毒層留下，防的是
    /// netsh 那一側的二次解析。參數化與消毒並存，才是正確的深度防禦。
    /// </para>
    /// </summary>
    private static async Task<(int code, string err)> RunProcess(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,   // 〔修復 v3.20〕以主控台字碼頁 950 解碼（不可用時後備 UTF8）
            StandardErrorEncoding = ConsoleEncoding,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // 兩條 pipe 必須同時非同步讀取：若序列先讀 stdout 再讀 stderr，stderr 塞滿 pipe 緩衝會造成死鎖。
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(outTask, errTask);
        await p.WaitForExitAsync();
        var err = string.IsNullOrWhiteSpace(errTask.Result) ? outTask.Result.Trim() : errTask.Result.Trim();
        return (p.ExitCode, err);
    }
}
