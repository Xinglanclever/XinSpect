using System.Collections.ObjectModel;

namespace XinSpect;

/// <summary>健康總評中的單一指標列（狀態燈 + 數值 + 明細）。就地更新，避免集合重建。</summary>
public sealed class HealthItem : ObservableObject
{
    public HealthItem(string label) => Label = label;
    public string Label { get; }

    private string _value = "—";
    public string ValueText { get => _value; set => SetProperty(ref _value, value); }

    private string _detail = "";
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }

    private Severity _sev = Severity.Neutral;
    public Severity Severity
    {
        get => _sev;
        set { if (SetProperty(ref _sev, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => _sev switch
    {
        Severity.Good => "良好",
        Severity.Warning => "注意",
        Severity.Serious => "偏高",
        Severity.Critical => "警示",
        _ => "—",
    };
}

/// <summary>
/// 健康總評：彙整 CPU/GPU/磁碟溫度、負載、磁碟空間與壽命為狀態燈清單，
/// 並依各項嚴重度扣分算出 0–100 綜合分數與整體評語。每秒由 MainViewModel 更新。
/// </summary>
public sealed class HealthReport : ObservableObject
{
    public ObservableCollection<HealthItem> Items { get; } = new();

    private readonly HealthItem _cpuT = new("CPU 溫度");
    private readonly HealthItem _cpuL = new("CPU 負載");
    private readonly HealthItem _gpuT = new("GPU 溫度");
    private readonly HealthItem _memL = new("記憶體負載");
    private readonly HealthItem _diskT = new("磁碟溫度");
    private readonly HealthItem _diskS = new("磁碟空間");
    private readonly HealthItem _diskLife = new("磁碟壽命／健康");

    public HealthReport()
    {
        Items.Add(_cpuT); Items.Add(_cpuL); Items.Add(_gpuT);
        Items.Add(_memL); Items.Add(_diskT); Items.Add(_diskS); Items.Add(_diskLife);
    }

    private int _score = 100;
    public int Score { get => _score; private set { if (SetProperty(ref _score, value)) OnPropertyChanged(nameof(ScoreText)); } }
    public string ScoreText => _score.ToString();

    private Severity _scoreSev = Severity.Good;
    public Severity ScoreSeverity { get => _scoreSev; private set => SetProperty(ref _scoreSev, value); }

    private string _summary = "尚未評估";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private string _advice = "";
    public string Advice { get => _advice; private set => SetProperty(ref _advice, value); }

    public void Update(SensorService? live, VolumeService vols)
    {
        int penalty = 0;

        void Set(HealthItem it, string value, Severity sev, string detail = "")
        {
            it.ValueText = value;
            it.Severity = sev;
            it.Detail = detail;
            penalty += sev switch
            {
                Severity.Warning => 8,
                Severity.Serious => 18,
                Severity.Critical => 32,
                _ => 0,
            };
        }

        // CPU 溫度 / 負載
        Set(_cpuT, live?.CpuTemp is double ct ? $"{ct:0} °C" : "—", Health.Cpu(live?.CpuTemp));
        Set(_cpuL, live is not null ? $"{live.CpuLoad:0} %" : "—", live is not null ? Health.Load(live.CpuLoad) : Severity.Neutral);

        // GPU 溫度
        if (live is { HasGpu: true, PrimaryGpu: { } gpu })
            Set(_gpuT, gpu.TempC is double gt ? $"{gt:0} °C" : "—", Health.Gpu(gpu.TempC), gpu.Name);
        else
            Set(_gpuT, "無獨立顯示卡", Severity.Neutral);

        // 記憶體負載
        Set(_memL, live is not null ? $"{live.MemLoad:0} %" : "—", live is not null ? Health.Load(live.MemLoad) : Severity.Neutral,
            live?.MemUsageText ?? "");

        // 磁碟溫度（取最高）
        double? maxDiskTemp = null; string hotDisk = "";
        if (live is not null)
            foreach (var d in live.Drives)
                if (d.TempC is double dt && (maxDiskTemp is null || dt > maxDiskTemp)) { maxDiskTemp = dt; hotDisk = d.Name; }
        Set(_diskT, maxDiskTemp is double mdt ? $"{mdt:0} °C" : "—", Health.Disk(maxDiskTemp), hotDisk);

        // 磁碟空間（取最滿）
        VolumeInfo? fullest = null;
        foreach (var v in vols.Volumes)
            if (fullest is null || v.UsedFraction > fullest.UsedFraction) fullest = v;
        if (fullest is not null)
            Set(_diskS, $"{fullest.Name} {fullest.UsedFraction * 100:0}%", Health.Space(fullest.UsedFraction * 100), fullest.FreeText);
        else
            Set(_diskS, "—", Severity.Neutral);

        // 磁碟壽命／健康：SSD/NVMe 取最低剩餘壽命；HDD 取 S.M.A.R.T. 磁區健康；兩者取較嚴重者
        double? minLife = null; string lifeDisk = "";
        Severity diskSev = Severity.Neutral; string sevDisk = ""; string sevDetail = "";
        if (live is not null)
            foreach (var d in live.Drives)
            {
                if (d.RemainingLife is double rl && (minLife is null || rl < minLife)) { minLife = rl; lifeDisk = d.Name; }
                if ((int)d.HealthSeverity > (int)diskSev) { diskSev = d.HealthSeverity; sevDisk = d.Name; sevDetail = d.HealthDetail; }
            }
        Severity lifeSev = LifeSeverity(minLife);
        Severity combined = (Severity)Math.Max((int)lifeSev, (int)diskSev);
        string diskVal = minLife is double ml ? $"{ml:0} %"
                       : diskSev == Severity.Neutral ? "無 S.M.A.R.T. 資料"
                       : sevDetail.Length > 0 ? sevDetail : "S.M.A.R.T. 異常";
        // 明細指向較嚴重的那顆磁碟：HDD 磁區問題不輕於 SSD 壽命時顯示 HDD，否則顯示壽命最低的磁碟
        string diskDetail = diskSev != Severity.Neutral && (int)diskSev >= (int)lifeSev && sevDisk.Length > 0
                          ? (sevDetail.Length > 0 ? $"{sevDisk}：{sevDetail}" : sevDisk)
                          : lifeDisk;
        Set(_diskLife, diskVal, combined, diskDetail);

        // 綜合分數與評語
        int score = Math.Clamp(100 - penalty, 0, 100);
        Score = score;
        ScoreSeverity = score >= 85 ? Severity.Good : score >= 70 ? Severity.Warning : score >= 50 ? Severity.Serious : Severity.Critical;
        Summary = ScoreSeverity switch
        {
            Severity.Good => "系統狀態良好，各項指標正常。",
            Severity.Warning => "整體正常，部分指標偏高，建議留意。",
            Severity.Serious => "多項指標偏高，建議檢查散熱與負載。",
            _ => "偵測到警示指標，建議立即檢查散熱與磁碟。",
        };

        var flagged = Items.Where(i => i.Severity is Severity.Serious or Severity.Critical).Select(i => i.Label).ToList();
        Advice = flagged.Count > 0 ? "需留意：" + string.Join("、", flagged) : "無異常項目";
    }

    private static Severity LifeSeverity(double? life)
        => !life.HasValue ? Severity.Neutral
         : life >= 50 ? Severity.Good
         : life >= 25 ? Severity.Warning
         : life >= 10 ? Severity.Serious
         : Severity.Critical;
}
