using System.Globalization;

namespace XinSpect;

/// <summary>
/// 固定容量的時間序列環形緩衝，供即時走勢圖使用。
/// 每次 Push 後觸發 <see cref="Updated"/>，走勢圖控制項據此重繪。
/// </summary>
/// <remarks>
/// 「沒讀到」與「量到 0」在此嚴格分開：<see cref="Push(double?)"/> 傳入 <c>null</c>（或 NaN／±∞）
/// 代表本機沒有這顆感測器或本輪讀取失敗，該點<b>不計入</b>最小／平均／最大，數值文字顯示「—」。
/// 若不這麼做，沒有溫度感測器的機器會在畫面上看到「0 °C」與一條貼底的直線——那是假數字，不是量測結果。
/// 緩衝本身仍存 0，是為了讓走勢圖的時間軸保持等距（NaN 會讓幾何運算失效）；圖層則在
/// <see cref="HasData"/> 為 false 時整段隱藏，不畫出那條 0 值線。
/// </remarks>
public sealed class MetricHistory : ObservableObject
{
    private readonly double[] _buf;
    private readonly bool[] _real;  // 對應 _buf 各格是否為真實讀值
    private int _count;
    private int _head;              // 下一個寫入位置
    private int _measured;          // 緩衝內真實讀值的個數
    private bool _lastReal;         // 最後一次 Push 是否為真實讀值
    private readonly string _fmt;

    public int Capacity { get; }
    public string Unit { get; }
    /// <summary>固定上限；為 null 時走勢圖自動縮放。</summary>
    public double? FixedMax { get; }

    public event Action? Updated;

    public MetricHistory(int capacity = 90, string unit = "%", double? fixedMax = 100, string fmt = "0")
    {
        Capacity = Math.Max(2, capacity);
        Unit = unit;
        FixedMax = fixedMax;
        _fmt = fmt;
        _buf = new double[Capacity];
        _real = new bool[Capacity];
    }

    public double Current { get; private set; }
    public double Min { get; private set; }
    public double Max { get; private set; }
    public double Avg { get; private set; }

    /// <summary>緩衝內是否有任何真實讀值；false 表示這台機器沒有這項讀值，畫面不應畫線也不應報數字。</summary>
    public bool HasData => _measured > 0;

    public string CurrentText => _lastReal ? Text(Current) : "—";
    public string MinText => Text(Min);
    public string MaxText => Text(Max);
    public string AvgText => Text(Avg);

    private string Text(double v) =>
        _measured == 0 ? "—" : $"{v.ToString(_fmt, CultureInfo.InvariantCulture)} {Unit}".TrimEnd();

    /// <summary>推入一筆讀值。<c>null</c>／NaN／±∞ 一律視為「沒讀到」，不計入統計。</summary>
    public void Push(double? value)
    {
        bool real = value is double raw && !double.IsNaN(raw) && !double.IsInfinity(raw);
        double v = real ? value!.Value : 0;

        _buf[_head] = v;
        _real[_head] = real;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;

        Current = v;
        _lastReal = real;

        // 統計只看真實讀值：沒讀到的點既不拉低最小值，也不稀釋平均
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            int slot = (_head - _count + i + Capacity) % Capacity;
            if (!_real[slot]) continue;
            double s = _buf[slot];
            if (s < min) min = s;
            if (s > max) max = s;
            sum += s;
            n++;
        }
        _measured = n;
        Min = n > 0 ? min : 0;
        Max = n > 0 ? max : 0;
        Avg = n > 0 ? sum / n : 0;

        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(MinText));
        OnPropertyChanged(nameof(MaxText));
        OnPropertyChanged(nameof(AvgText));
        OnPropertyChanged(nameof(HasData));
        Updated?.Invoke();
    }

    /// <summary>依時間順序（舊 → 新）複製目前樣本。</summary>
    public double[] Snapshot()
    {
        var result = new double[_count];
        for (int i = 0; i < _count; i++)
            result[i] = _buf[(_head - _count + i + Capacity) % Capacity];
        return result;
    }
}

/// <summary>網路介面卡即時資訊列。</summary>
public sealed class NetAdapterRow : ObservableObject
{
    public NetAdapterRow(string id, string name)
    {
        Id = id;
        Name = name;
        DownHistory = new MetricHistory(90, "", null, "0.0");
        UpHistory = new MetricHistory(90, "", null, "0.0");
    }

    public string Id { get; }
    public string Name { get; }

    public string Description { get; set; } = "—";
    public string TypeText { get; set; } = "—";
    public string Mac { get; set; } = "—";
    public string Ipv4 { get; set; } = "—";
    public string Ipv6 { get; set; } = "—";
    public string LinkSpeedText { get; set; } = "—";

    // ── 深度網路組態（System.Net.NetworkInformation，開機讀取一次）────────────
    public string Gateway { get; set; } = "—";       // 預設閘道
    public string SubnetMask { get; set; } = "—";     // 子網路遮罩（由 IPv4 前綴長度換算）
    public string Dns { get; set; } = "—";            // DNS 伺服器（可多筆）
    public string DhcpText { get; set; } = "—";       // DHCP 啟用與伺服器
    public string DnsSuffix { get; set; } = "—";      // 連線特定 DNS 尾碼
    public string MtuText { get; set; } = "—";        // 最大傳輸單元

    public MetricHistory DownHistory { get; }
    public MetricHistory UpHistory { get; }

    private double _downBps, _upBps;
    public double DownBps { get => _downBps; set { if (SetProperty(ref _downBps, value)) OnPropertyChanged(nameof(DownText)); } }
    public double UpBps { get => _upBps; set { if (SetProperty(ref _upBps, value)) OnPropertyChanged(nameof(UpText)); } }

    public string DownText => Rate(_downBps);
    public string UpText => Rate(_upBps);

    public static string Rate(double bps)
    {
        if (bps >= 1_048_576) return $"{bps / 1_048_576:0.0} MB/s";
        if (bps >= 1024) return $"{bps / 1024:0.0} KB/s";
        return $"{bps:0} B/s";
    }
}

/// <summary>行程佔用列（CPU% 與工作集）。</summary>
public sealed class ProcRow : ObservableObject
{
    public ProcRow(int pid, string name) { Pid = pid; Name = name; }

    public int Pid { get; }
    public string Name { get; }

    private double _cpuPercent;
    public double CpuPercent { get => _cpuPercent; set { if (SetProperty(ref _cpuPercent, value)) OnPropertyChanged(nameof(CpuText)); } }

    private double _ramMB;
    public double RamMB { get => _ramMB; set { if (SetProperty(ref _ramMB, value)) OnPropertyChanged(nameof(RamText)); } }

    private int _threads;
    public int Threads { get => _threads; set { if (SetProperty(ref _threads, value)) OnPropertyChanged(nameof(ThreadsText)); } }

    public string CpuText => $"{_cpuPercent:0.0} %";
    public string RamText => _ramMB >= 1024 ? $"{_ramMB / 1024:0.00} GB" : $"{_ramMB:0} MB";
    public string ThreadsText => _threads > 0 ? _threads.ToString(CultureInfo.InvariantCulture) : "—";
    public string PidText => Pid.ToString(CultureInfo.InvariantCulture);
}
