using System.Collections.ObjectModel;
using System.Management;

namespace XinSpect;

/// <summary>
/// 驅動程式稽核：列出這台機器上實際安裝的每一支驅動，把<b>簽章狀態</b>與<b>驅動日期</b>攤開來看。
/// 「未簽章」與「關鍵類別的驅動老到不合理」這兩件事，裝置管理員要一個一個點進內容才看得到，
/// 這裡一次列完並排序，可搜尋。
/// </summary>
/// <remarks>
/// <para>
/// 資料來源是 WMI 的 <c>Win32_PnPSignedDriver</c>——作業系統自己的驅動存放區記錄，<b>全程唯讀</b>，
/// 不安裝、不移除、不回捲任何驅動。同一支驅動（同名稱＋同版本＋同 INF）掛在多個裝置實例上時合併成一列。
/// </para>
/// <para>
/// 誠實界線：①WMI 沒回報日期就顯示「—」，不拿 INF 的檔案時間充數；②微軟隨附驅動的日期是佔位值，
/// 判讀時明說不以年紀評斷（見 <see cref="DriverAuditDecoder"/>）；③這裡不比對「線上最新版本」——
/// 那需要連到各家廠商的更新服務，不是本機讀得到的事，所以只說「值得去官網對一下」，不假裝知道結果。
/// </para>
/// </remarks>
public sealed class DriverAuditService : ObservableObject
{
    private readonly List<DriverRow> _all = [];

    /// <summary>目前顯示的列（已套用搜尋與「只顯示需要注意的」）。</summary>
    public ObservableCollection<DriverRow> Rows { get; } = [];

    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。按「重新掃描」列出已安裝的驅動（唯讀）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _summary = "—";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private bool _onlyFlagged;
    /// <summary>只顯示被標記的（未簽章、或關鍵類別而且明顯老舊）。</summary>
    public bool OnlyFlagged
    {
        get => _onlyFlagged;
        set { if (SetProperty(ref _onlyFlagged, value)) ApplyFilter(); }
    }

    private string _filter = "";
    /// <summary>搜尋字串：裝置、類別、版本、提供者、INF 任一命中即顯示。</summary>
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value ?? "")) ApplyFilter(); }
    }

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "查詢驅動存放區中…（第一次查詢 WMI 可能要幾秒）";
        Rows.Clear();
        _all.Clear();
        Summary = "—";
        try
        {
            var (summary, rows) = await Task.Run(() => ScanAll(DateTime.Now));
            _all.AddRange(rows);
            Summary = summary;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Summary = "無法讀取驅動清單：" + ex.Message;
            Status = "讀取失敗。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in _all)
        {
            if (_onlyFlagged && r.Severity == 0) continue;
            if (!DriverAuditDecoder.Matches(r, _filter)) continue;
            Rows.Add(r);
        }
        Status = _all.Count == 0
            ? "尚未讀取。"
            : Rows.Count == _all.Count
                ? $"顯示全部 {_all.Count} 支驅動。"
                : $"顯示 {Rows.Count} / {_all.Count} 支驅動（其餘被搜尋或篩選條件擋下）。";
    }

    /// <summary>同一支驅動的合併鍵：名稱＋版本＋INF；掛在幾個裝置上另外計數。</summary>
    private sealed class Bucket
    {
        public string Device = "", Cls = "", Version = "", Provider = "", Inf = "";
        public string? RawDate;
        public bool Signed;
        public int Count;
    }

    private static (string Summary, List<DriverRow> Rows) ScanAll(DateTime now)
    {
        var buckets = new Dictionary<(string, string, string), Bucket>();
        int instances = 0;

        using var search = new ManagementObjectSearcher("root\\CIMV2",
            "SELECT DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName, IsSigned, InfName "
            + "FROM Win32_PnPSignedDriver");

        foreach (ManagementObject o in search.Get())
        {
            using (o)
            {
                string name = Text(o, "DeviceName");
                if (name.Length == 0) continue;      // 沒有名字的實例列出來只是一排空白
                string ver = Text(o, "DriverVersion");
                string inf = Text(o, "InfName");
                instances++;

                var key = (name, ver, inf);
                if (!buckets.TryGetValue(key, out var b))
                {
                    b = new Bucket
                    {
                        Device = name,
                        Cls = Text(o, "DeviceClass"),
                        Version = ver,
                        Provider = Text(o, "DriverProviderName"),
                        Inf = inf,
                        RawDate = o["DriverDate"] as string,
                        Signed = o["IsSigned"] as bool? ?? false,
                    };
                    buckets[key] = b;
                }
                b.Count++;
            }
        }

        var rows = new List<DriverRow>(buckets.Count);
        foreach (var b in buckets.Values)
        {
            var date = DriverAuditDecoder.ParseCimDate(b.RawDate);
            var (verdict, severity) = DriverAuditDecoder.Judge(b.Signed, date, b.Provider, b.Cls, now);
            rows.Add(new DriverRow
            {
                Device = b.Device, DeviceClass = b.Cls, Version = b.Version, Date = date,
                Provider = b.Provider, Signed = b.Signed, Inf = b.Inf, Instances = b.Count,
                Verdict = verdict, Severity = severity,
            });
        }

        // 需要注意的排最前面；同一級之內按類別、名稱排，方便一眼掃過同類裝置
        rows.Sort((x, y) =>
        {
            int c = y.Severity.CompareTo(x.Severity);
            if (c != 0) return c;
            c = string.Compare(x.ClassText, y.ClassText, StringComparison.CurrentCulture);
            return c != 0 ? c : string.Compare(x.Device, y.Device, StringComparison.CurrentCulture);
        });

        int unsigned = rows.Count(r => r.Severity == 2);
        int old = rows.Count(r => r.Severity == 1);
        string summary = $"共 {rows.Count} 支驅動（{instances} 個裝置實例）　・　未簽章 {unsigned} 支"
                       + $"　・　關鍵類別中日期超過 {DriverAuditDecoder.OldYears} 年 {old} 支";
        return (summary, rows);
    }

    private static string Text(ManagementObject o, string prop)
    {
        try { return (o[prop] as string)?.Trim() ?? ""; }
        catch (ManagementException ex) { Diag.Swallow($"讀取驅動欄位 {prop}", ex, "該欄位以空白處理"); return ""; }
    }
}
