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

    /// <summary>套用預設或自訂 DNS 到指定介面卡；Auto 則還原 DHCP。回報是否成功。</summary>
    public async Task<bool> ApplyAsync(NetAdapter adapter, DnsPreset preset)
    {
        if (adapter == null) { Status = "請先選擇網路介面卡。"; return false; }
        try
        {
            if (preset.Auto)
            {
                await RunNetsh($"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp");
                await RunNetsh($"interface ipv6 set dnsservers name=\"{adapter.Name}\" source=dhcp");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(preset.V4Primary))
                {
                    var (code, err) = await RunNetsh(
                        $"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=static address={preset.V4Primary} register=primary validate=no");
                    if (code != 0)
                    {
                        Status = $"設定 IPv4 DNS 失敗（netsh 代碼 {code}）：{err}";
                        return false;
                    }
                    if (!string.IsNullOrWhiteSpace(preset.V4Secondary))
                        await RunNetsh($"interface ipv4 add dnsservers name=\"{adapter.Name}\" address={preset.V4Secondary} index=2 validate=no");
                }
                // 僅在預設提供 IPv6 位址時才動 IPv6，避免影響原設定
                if (!string.IsNullOrWhiteSpace(preset.V6Primary))
                {
                    await RunNetsh($"interface ipv6 set dnsservers name=\"{adapter.Name}\" source=static address={preset.V6Primary} register=primary validate=no");
                    if (!string.IsNullOrWhiteSpace(preset.V6Secondary))
                        await RunNetsh($"interface ipv6 add dnsservers name=\"{adapter.Name}\" address={preset.V6Secondary} index=2 validate=no");
                }
            }

            await FlushAsync();
            RefreshOne(adapter);
            Status = preset.Auto
                ? $"「{adapter.Name}」已還原為自動取得 DNS（DHCP）。"
                : $"「{adapter.Name}」已套用 {preset.Name} DNS（{preset.Note}）。";
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

    public async Task FlushAsync()
    {
        try { await RunProcess("ipconfig", "/flushdns"); } catch { /* 非致命 */ }
    }

    public async Task FlushCacheAsync()
    {
        await FlushAsync();
        Status = "已清除 DNS 解析快取。";
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

    private static Task<(int code, string err)> RunNetsh(string args) => RunProcess("netsh", args);

    private static async Task<(int code, string err)> RunProcess(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string stdout = await p.StandardOutput.ReadToEndAsync();
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var err = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        return (p.ExitCode, err);
    }
}
