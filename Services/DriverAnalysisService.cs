using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>
/// 基於 <see cref="DriverAuditService"/> 的掃描結果，提供分類彙總、年齡分布、
/// 重複偵測、已知問題驅動與更新建議。
/// </summary>
public sealed class DriverAnalysisService : ObservableObject
{
    // ── UI 繫結屬性 ──
    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private string _statusText = "就緒";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private int _totalAnalyzed;
    public int TotalAnalyzed { get => _totalAnalyzed; private set => SetProperty(ref _totalAnalyzed, value); }

    private int _categoriesWithIssues;
    public int CategoriesWithIssues { get => _categoriesWithIssues; private set => SetProperty(ref _categoriesWithIssues, value); }

    private int _duplicateCount;
    public int DuplicateCount { get => _duplicateCount; private set => SetProperty(ref _duplicateCount, value); }

    private int _knownProblematicCount;
    public int KnownProblematicCount { get => _knownProblematicCount; private set => SetProperty(ref _knownProblematicCount, value); }

    public ObservableCollection<DriverCategorySummary> Categories      { get; } = [];
    public ObservableCollection<DriverAgeGroup>        AgeHistogram    { get; } = [];
    public ObservableCollection<DuplicateDriverGroup>  Duplicates      { get; } = [];
    public ObservableCollection<DriverRecommendation>  Recommendations { get; } = [];

    // ── 已知問題驅動資料庫 ──
    private static readonly KnownBadDriver[] _knownBadDrivers =
    [
        new("Realtek High Definition Audio",     "6.0.1",  "老舊 Realtek 音效驅動可能導致音訊爆音或 BSOD"),
        new("Realtek PCIe GBE Family Controller","7.0",    "早期版本有封包遺失與喚醒問題"),
        new("Intel Wi-Fi 6 AX200",               "21.0",   "21.x 之前版本有連線不穩問題"),
        new("Killer Wireless",                    "1.0",    "Killer 網卡驅動常見高延遲與相容性問題"),
        new("NVIDIA GeForce",                     "450.0",  "舊版 NVIDIA 驅動可能不支援新遊戲與安全修補"),
        new("AMD Radeon",                         "20.0",   "20.x 之前的 Adrenalin 驅動有已知穩定性問題"),
        new("Synaptics SMBus TouchPad",           "19.0",   "舊版觸控板驅動可能導致游標跳動"),
        new("Broadcom 802.11",                    "7.0",    "舊版 Broadcom Wi-Fi 驅動有安全弱點"),
        new("Conexant Audio",                     "8.0",    "Conexant 音效驅動含鍵盤側錄程式的歷史漏洞"),
        new("VIA HD Audio",                       "6.0",    "VIA 音效驅動更新停滯，缺乏安全修補"),
    ];

    // ── 分類優先序 (越高越重要) ──
    private static readonly Dictionary<string, int> _categoryPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Display"]  = 5,
        ["Net"]      = 5,
        ["MEDIA"]    = 4,
        ["hdc"]      = 4,   // 儲存控制器
        ["SCSIAdapter"] = 4,
        ["DiskDrive"]   = 4,
        ["USB"]      = 3,
        ["SYSTEM"]   = 3,
        ["HIDClass"] = 2,
        ["Mouse"]    = 2,
        ["Keyboard"] = 2,
    };

    // ── 分析 ──
    public async Task AnalyzeAsync(DriverAuditService auditService)
    {
        if (IsLoading) return;
        IsLoading  = true;
        StatusText = "正在分析驅動程式…";
        Categories.Clear();
        AgeHistogram.Clear();
        Duplicates.Clear();
        Recommendations.Clear();

        try
        {
            // 確保稽核資料已載入
            if (auditService.AllRows.Count == 0)
                auditService.Refresh();

            // 等待掃描完成（Refresh 是射後不理的 async，輪詢 IsLoading）
            while (auditService.IsLoading)
                await Task.Delay(200);

            var drivers = auditService.AllRows.ToList();
            await Task.Run(() => Analyze(drivers));

            StatusText = $"分析完成 — {TotalAnalyzed} 個驅動程式";
        }
        catch (Exception ex)
        {
            StatusText = $"分析失敗：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Analyze(List<DriverRow> drivers)
    {
        TotalAnalyzed = drivers.Count;
        var today = DateTime.Now;

        // ── 1. 分類彙總 ──
        var groups = drivers.GroupBy(d => MapCategory(d.DeviceClass));
        int catIssues = 0;
        var catList = new List<DriverCategorySummary>();
        foreach (var g in groups.OrderBy(g => g.Key))
        {
            int total  = g.Count();
            int issues = g.Count(d => d.Severity > 0);
            if (issues > 0) catIssues++;
            catList.Add(new DriverCategorySummary(g.Key, total, issues));
        }
        CategoriesWithIssues = catIssues;
        App.Current.Dispatcher.Invoke(() =>
        {
            foreach (var c in catList) Categories.Add(c);
        });

        // ── 2. 年齡分布 ──
        int under1 = 0, y1to3 = 0, y3to5 = 0, over5 = 0, unknown = 0;
        foreach (var d in drivers)
        {
            if (!d.Date.HasValue) { unknown++; continue; }
            int days = (int)(today.Date - d.Date.Value.Date).TotalDays;
            int years = days / 365;
            if      (years < 1) under1++;
            else if (years < 3) y1to3++;
            else if (years < 5) y3to5++;
            else                over5++;
        }
        App.Current.Dispatcher.Invoke(() =>
        {
            AgeHistogram.Add(new DriverAgeGroup("< 1 年",   under1));
            AgeHistogram.Add(new DriverAgeGroup("1 - 3 年", y1to3));
            AgeHistogram.Add(new DriverAgeGroup("3 - 5 年", y3to5));
            AgeHistogram.Add(new DriverAgeGroup("> 5 年",   over5));
            if (unknown > 0)
                AgeHistogram.Add(new DriverAgeGroup("日期不明", unknown));
        });

        // ── 3. 重複偵測 ──
        var dupGroups = drivers
            .Where(d => !string.IsNullOrEmpty(d.Inf))
            .GroupBy(d => d.Inf, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(d => d.Version).Distinct().Count() > 1)
            .ToList();
        DuplicateCount = dupGroups.Count;
        App.Current.Dispatcher.Invoke(() =>
        {
            foreach (var g in dupGroups)
            {
                var versions = string.Join(", ", g.Select(d => d.Version).Distinct());
                Duplicates.Add(new DuplicateDriverGroup(g.Key, g.Count(), versions));
            }
        });

        // ── 4. 已知問題驅動 ──
        int knownBad = 0;
        var recs = new List<DriverRecommendation>();

        foreach (var d in drivers)
        {
            foreach (var bad in _knownBadDrivers)
            {
                if (!d.Device.Contains(bad.NamePattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 不比版本。這裡的門檻（NVIDIA 450.0、Realtek 6.0.1…）是**行銷版本**，
                // 而 Win32_PnPSignedDriver.DriverVersion 是**廠商套件版本**（本機 NVIDIA 是
                // 32.0.15.7688）——兩套編號不能比大小：32 < 450 會讓**每一台**裝 NVIDIA 的機器
                // 都被報成「驅動過舊」。離線也比不出真正的「最新版」，所以只陳述
                // 「這類驅動在本機上、且這個家族有已知問題」，要不要更新由使用者自己判斷。
                knownBad++;
                recs.Add(new DriverRecommendation(
                    d.Device, d.Version, RecommendPriority.Low,
                    $"{bad.Reason}（本機版本為 {d.Version}，請自行至廠商官網確認是否已有更新）"));
            }
        }
        KnownProblematicCount = knownBad;

        // ── 5. 老舊 + 關鍵類別 → 建議 ──
        foreach (var d in drivers.Where(d => d.Severity == 1 && !recs.Any(r => r.DriverName == d.Device)))
        {
            int prio = _categoryPriority.GetValueOrDefault(d.DeviceClass, 1);
            var level = prio >= 4 ? RecommendPriority.High
                      : prio >= 3 ? RecommendPriority.Medium
                      :             RecommendPriority.Low;
            string reason = $"驅動程式日期為 {d.DateText}，已超過 {DriverAuditDecoder.OldYears} 年";
            if (prio >= 4)
                reason += "（屬關鍵類別，建議優先更新）";
            recs.Add(new DriverRecommendation(d.Device, d.Version, level, reason));
        }

        // 未簽章也加建議
        foreach (var d in drivers.Where(d => !d.Signed && !recs.Any(r => r.DriverName == d.Device)))
        {
            recs.Add(new DriverRecommendation(
                d.Device, d.Version, RecommendPriority.Medium,
                "未經數位簽章，可能有安全風險"));
        }

        App.Current.Dispatcher.Invoke(() =>
        {
            foreach (var r in recs.OrderByDescending(r => r.Priority))
                Recommendations.Add(r);
        });
    }

    private static string MapCategory(string deviceClass) => deviceClass?.ToUpperInvariant() switch
    {
        "DISPLAY"     => "顯示",
        "NET"         => "網路",
        "MEDIA"       => "音訊",
        "HDC"         => "儲存",
        "SCSIADAPTER" => "儲存",
        "DISKDRIVE"   => "儲存",
        "USB"         => "USB",
        "SYSTEM"      => "系統",
        "HIDCLASS"    => "輸入",
        "MOUSE"       => "輸入",
        "KEYBOARD"    => "輸入",
        "PRINTER"     => "印表機",
        "BLUETOOTH"   => "藍牙",
        null or ""    => "其他",
        _             => "其他",
    };
}

// ── 資料記錄 ──
public record DriverCategorySummary(string Category, int Total, int IssueCount)
{
    public string Display => IssueCount > 0
        ? $"{Category}（{Total} 個，{IssueCount} 個有問題）"
        : $"{Category}（{Total} 個）";
}

public record DriverAgeGroup(string Label, int Count);

public record DuplicateDriverGroup(string InfName, int EntryCount, string Versions);

public enum RecommendPriority { Low, Medium, High }

public record DriverRecommendation(
    string DriverName,
    string CurrentVersion,
    RecommendPriority Priority,
    string Reason)
{
    public string PriorityDisplay => Priority switch
    {
        RecommendPriority.High   => "高",
        RecommendPriority.Medium => "中",
        _                        => "低",
    };
}

public record KnownBadDriver(string NamePattern, string MinSafeVersion, string Reason);
