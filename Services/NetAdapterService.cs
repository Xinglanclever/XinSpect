using System.Collections.ObjectModel;
using System.Management;

namespace XinSpect;

/// <summary>一張網卡：RSS 判讀 ＋ 它的進階屬性清單。</summary>
public sealed class NetAdapterConfigRow
{
    public required string Name { get; init; }
    public required string RssText { get; init; }
    public required Severity RssSeverity { get; init; }
    public required string QueueText { get; init; }
    public required List<NetAdapterProperty> Properties { get; init; }
    public bool HasProperties => Properties.Count > 0;
}

/// <summary>
/// 網卡進階屬性實況：RSS 接收佇列與驅動自己開放的每一項設定。
/// </summary>
/// <remarks>
/// 資料來自 <c>root\StandardCimv2</c> 的 <c>MSFT_NetAdapterRssSettingData</c> 與
/// <c>MSFT_NetAdapterAdvancedPropertySettingData</c>（唯讀 WMI 查詢，與 PowerShell 的
/// <c>Get-NetAdapterAdvancedProperty</c> 同一份資料）。
/// <para>
/// 為什麼值得一頁：這些設定藏在裝置管理員的「進階」分頁裡，一張網卡幾十項，
/// 而其中 RSS 直接決定收包處理能不能分到多顆核心上——與「中斷落在哪顆核」那張卡是同一個故事的兩半。
/// </para>
/// <para>界線：屬性一律原樣呈現，只多附上登錄關鍵字（跨語言唯一穩定的識別）。不改任何設定。</para>
/// </remarks>
public sealed class NetAdapterService : ObservableObject
{
    private const string Scope = @"root\StandardCimv2";

    public ObservableCollection<NetAdapterConfigRow> Rows { get; } = [];

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }

    public bool CanRefresh => !_busy;

    private string _status = "按「重新讀取」查詢網卡的 RSS 與進階屬性。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _loaded;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    public void Refresh()
    {
        if (_busy) return;
        IsBusy = true;
        Status = "正在查詢…";
        _ = Task.Run(Collect).ContinueWith(t =>
        {
            Rows.Clear();
            var (rows, status) = t.Result;
            foreach (var r in rows) Rows.Add(r);
            Status = status;
            IsBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static (List<NetAdapterConfigRow> Rows, string Status) Collect()
    {
        var rows = new List<NetAdapterConfigRow>();
        int cores = Environment.ProcessorCount;

        var props = new Dictionary<string, List<NetAdapterProperty>>(StringComparer.OrdinalIgnoreCase);
        var rss = new Dictionary<string, (bool Enabled, int Queues, int MaxProc, int BaseProc)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var s = new ManagementObjectSearcher(Scope,
                "SELECT Name, DisplayName, DisplayValue, RegistryKeyword FROM MSFT_NetAdapterAdvancedPropertySettingData");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = Str(o, "Name");
                    if (name.Length == 0) continue;
                    if (!props.TryGetValue(name, out var list)) props[name] = list = [];
                    list.Add(NetAdapterDecoder.Property(Str(o, "DisplayName"), Str(o, "DisplayValue"), Str(o, "RegistryKeyword")));
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("NetAdapterService.AdvancedProperties", ex, "網卡進階屬性讀不到，該區塊留空。");
        }

        try
        {
            using var s = new ManagementObjectSearcher(Scope,
                "SELECT Name, Enabled, NumberOfReceiveQueues, MaxProcessors, BaseProcessorNumber FROM MSFT_NetAdapterRssSettingData");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string name = Str(o, "Name");
                    if (name.Length == 0) continue;
                    rss[name] = (Bool(o, "Enabled"), Int(o, "NumberOfReceiveQueues"),
                                 Int(o, "MaxProcessors"), Int(o, "BaseProcessorNumber"));
                }
        }
        catch (Exception ex)
        {
            Diag.Swallow("NetAdapterService.Rss", ex, "RSS 設定讀不到，判讀顯示為讀不到。");
        }

        foreach (string name in props.Keys.Union(rss.Keys, StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            bool hasRss = rss.TryGetValue(name, out var r);
            var (text, sev) = hasRss
                ? NetAdapterDecoder.JudgeRss(r.Enabled, r.Queues, cores)
                : ("這張網卡沒有回報 RSS 設定（虛擬介面與部分驅動不支援）。", Severity.Neutral);

            rows.Add(new NetAdapterConfigRow
            {
                Name = name,
                RssText = text,
                RssSeverity = sev,
                QueueText = hasRss
                    ? $"啟用：{(r.Enabled ? "是" : "否")} ・ 接收佇列 {(r.Queues > 0 ? r.Queues.ToString() : "未回報")}"
                      + $" ・ 可用處理器上限 {(r.MaxProc > 0 ? r.MaxProc.ToString() : "未回報")}"
                      + $" ・ 起始處理器 {r.BaseProc}"
                    : "—",
                Properties = props.TryGetValue(name, out var list) ? list : [],
            });
        }

        if (rows.Count == 0)
            return (rows, "沒有查到任何網卡設定。這兩個 WMI 類別需要系統管理員權限，"
                        + "且虛擬介面通常不會回報進階屬性。");

        int weak = rows.Count(x => x.RssSeverity == Severity.Warning);
        return (rows, weak == 0
            ? $"共 {rows.Count} 張介面。RSS 設定沒有明顯問題。全程唯讀，沒有改任何設定。"
            : $"共 {rows.Count} 張介面，其中 {weak} 張的收包處理集中在單一核心（見各張的判讀）。全程唯讀。");
    }

    private static string Str(ManagementObject o, string p)
    {
        try { return o[p]?.ToString() ?? ""; } catch { return ""; }
    }

    private static int Int(ManagementObject o, string p)
    {
        try { return o[p] is null ? 0 : Convert.ToInt32(o[p]); } catch { return 0; }
    }

    private static bool Bool(ManagementObject o, string p)
    {
        try { return o[p] is not null && Convert.ToBoolean(o[p]); } catch { return false; }
    }
}
