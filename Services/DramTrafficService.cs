using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// L3 未命中與 DRAM 實際流量：每一次末級快取未命中，就是一條快取行真的從記憶體被搬上來。
/// </summary>
/// <remarks>
/// 這張卡片與「記憶體頻寬」那一張問的是不同的問題：那一張問「本程式能達成多少」，
/// 這一張問「晶片上真的發生了多少次去記憶體的往返」。用的是架構效能事件
/// LONGEST_LAT_CACHE.MISS／REFERENCE（Intel SDM 列為架構事件，跨世代編碼相同），
/// 不是需要逐微架構查表的 OFFCORE_RESPONSE 遮罩——後者填錯會得到一個看起來合理的假數字。
/// <para>
/// 即使用架構事件也<b>先自我驗證</b>：拿一段已知會讀滿 512 MB 的負載去對，計數器算出的位元組
/// 不在容許範圍內就只顯示原始計數、不換算成頻寬。這與能量計的處理方式一致。
/// </para>
/// <para>
/// <b>逐通道分佈量不到</b>：那要讀記憶體控制器（iMC）自己的效能監測計數器，而在 Skylake-X
/// 這類平台上它們只走 MMIO；本程式的唯讀路徑是 MSR 與 PCI 設定空間，到不了。到不了就說到不了。
/// </para>
/// </remarks>
public sealed class DramTrafficService : ObservableObject
{
    private const uint MsrPerfEvtSel0 = 0x186;
    private const uint MsrPmc0 = 0xC1;
    private const uint MsrGlobalCtrl = 0x38F;

    /// <summary>LONGEST_LAT_CACHE：未命中（umask 0x41）與參照（umask 0x4F），皆為架構事件。</summary>
    private const uint EvLlc = 0x2E, UmMiss = 0x41, UmRef = 0x4F;

    /// <summary>已知負載的大小：必須遠大於 L3，才能保證讀取真的落到 DRAM。</summary>
    private const long BufferBytes = 512L * 1024 * 1024;

    /// <summary>每條快取行只碰一個位元組就夠——要的是「搬了幾條行」，不是算術。</summary>
    private const int Stride = 64;

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanMeasure)); }
    }

    public bool CanMeasure => !_busy;

    private string _status = "按下量測後，會跑一段已知大小的讀取負載，並用它驗證計數器。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _validation = "尚未驗證。";
    public string ValidationText { get => _validation; private set => SetProperty(ref _validation, value); }

    private Severity _severity = Severity.Neutral;
    public Severity Severity { get => _severity; private set => SetProperty(ref _severity, value); }

    private string _miss = "—";
    public string MissText { get => _miss; private set => SetProperty(ref _miss, value); }

    private string _reference = "—";
    public string ReferenceText { get => _reference; private set => SetProperty(ref _reference, value); }

    private string _hit = "—";
    public string HitText { get => _hit; private set => SetProperty(ref _hit, value); }

    private string _traffic = "—";
    public string TrafficText { get => _traffic; private set => SetProperty(ref _traffic, value); }

    /// <summary>逐通道為什麼沒有——固定文字，讓「量不到」跟「沒做」長得不一樣。</summary>
    public string ChannelNote =>
        "逐通道分佈：量不到。那要讀記憶體控制器自己的效能監測計數器，而在本平台上它們只走 MMIO 映射；"
        + "本程式的唯讀路徑（MSR 與 PCI 設定空間）到不了，因此不列一組猜出來的通道分佈。";

    public void Measure()
    {
        if (_busy) return;
        IsBusy = true;
        Status = "正在量測（配置 512 MB 並讀滿一次）…";

        _ = Task.Run(Run).ContinueWith(t =>
        {
            var r = t.Result;
            Status = r.Status;
            ValidationText = r.Validation;
            Severity = r.Severity;
            MissText = r.Miss;
            ReferenceText = r.Reference;
            HitText = r.Hit;
            TrafficText = r.Traffic;
            IsBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private readonly record struct Result(string Status, string Validation, Severity Severity,
                                          string Miss, string Reference, string Hit, string Traffic);

    private static Result Empty(string status) =>
        new(status, "沒有量到計數，因此沒有可驗證的東西。", Severity.Neutral, "—", "—", "—", "—");

    private static Result Run()
    {
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available)
            return Empty("讀不到效能計數器：" + bridge.Error
                       + "（需要以系統管理員身分執行；本卡片不會拿估計值頂替）。");

        // 挑最後一顆邏輯處理器：核心 0 要服務中斷，量測期間的干擾最大
        var all = CpuAffinity.AllLogicalProcessors();
        if (all.Count == 0) return Empty("列不出邏輯處理器，無法釘選核心。");
        using var pin = CpuAffinity.Pinned(all[^1]);
        if (!pin.Ok) return Empty("無法把量測執行緒釘在單一核心上，計數會混到別的核，因此不量。");

        // 緩衝區必須在計數器啟用<b>之前</b>就配置並寫過一遍：寫入本身也會造成 L3 未命中
        // （write-allocate 要先把行讀進來），把它算進來會讓量到的位元組正好變成兩倍——
        // 自我驗證第一次就是這樣抓到的。
        byte[] buf;
        try { buf = new byte[BufferBytes]; }
        catch (OutOfMemoryException) { return Empty("配置不到 512 MB，因此不下任何結論。"); }
        Fill(buf);

        ulong savedGlobal = bridge.ReadMsrPair64(MsrGlobalCtrl) ?? 0;
        ulong savedSel0 = bridge.ReadMsrPair64(MsrPerfEvtSel0) ?? 0;
        ulong savedSel1 = bridge.ReadMsrPair64(MsrPerfEvtSel0 + 1) ?? 0;

        long misses, references;
        double seconds;
        try
        {
            Write(bridge, MsrGlobalCtrl, savedGlobal & ~0x3UL);      // 只停 PMC0、PMC1
            Write(bridge, MsrPerfEvtSel0, Sel(EvLlc, UmMiss));
            Write(bridge, MsrPerfEvtSel0 + 1, Sel(EvLlc, UmRef));
            Write(bridge, MsrPmc0, 0);
            Write(bridge, MsrPmc0 + 1, 0);
            Write(bridge, MsrGlobalCtrl, savedGlobal | 0x3UL);

            long t0 = Stopwatch.GetTimestamp();
            long touched = ReadAll(buf);
            long t1 = Stopwatch.GetTimestamp();

            misses = (long)(bridge.ReadMsrPair64(MsrPmc0) ?? 0);
            references = (long)(bridge.ReadMsrPair64(MsrPmc0 + 1) ?? 0);
            seconds = (t1 - t0) / (double)Stopwatch.Frequency;
            if (touched == 0) return Empty("負載沒有真的跑起來，因此不下任何結論。");
        }
        catch (Exception ex)
        {
            Diag.Swallow("DramTrafficService.Run", ex, "DRAM 流量量不到，卡片顯示為「—」。");
            return Empty("量測中發生例外，已記入診斷紀錄。");
        }
        finally
        {
            Write(bridge, MsrGlobalCtrl, savedGlobal & ~0x3UL);
            Write(bridge, MsrPerfEvtSel0, savedSel0);
            Write(bridge, MsrPerfEvtSel0 + 1, savedSel1);
            Write(bridge, MsrGlobalCtrl, savedGlobal);
        }

        var v = DramTrafficDecoder.Validate(BufferBytes, DramTrafficDecoder.Bytes(misses));
        double? hit = DramTrafficDecoder.HitPercent(references, misses);

        return new Result(
            Status: $"量測完成：讀滿 {BufferBytes / (1024 * 1024)} MB 花了 {seconds:0.000} 秒，"
                  + "計數器只在這段期間啟用，結束後已還原成原來的設定。",
            Validation: v.Text,
            Severity: v.Passed ? Severity.Good : Severity.Warning,
            Miss: $"{misses:N0} 次",
            Reference: references > 0 ? $"{references:N0} 次" : "—",
            Hit: hit is { } h ? $"{h:0.0}%" : "—（參照計數不可用或與未命中不一致，不硬算）",
            Traffic: DramTrafficDecoder.TrafficText(misses, seconds, v.Passed));
    }

    /// <summary>寫一遍讓分頁真的落地（否則讀到的是零頁，不會去記憶體）。<b>不在計數視窗內</b>。</summary>
    private static void Fill(byte[] buf)
    {
        for (long i = 0; i < buf.LongLength; i += Stride) buf[i] = 1;
    }

    /// <summary>把緩衝區讀滿一次；回傳實際碰過的位元組數。這一段才是被計數的負載。</summary>
    private static long ReadAll(byte[] buf)
    {
        long sum = 0;
        for (long i = 0; i < buf.LongLength; i += Stride) sum += buf[i];
        return sum == 0 ? 0 : buf.LongLength;
    }

    private static void Write(WinRing0Bridge b, uint msr, ulong value)
        => b.WriteMsrPair(msr, (uint)(value & 0xFFFFFFFF), (uint)(value >> 32));

    /// <summary>PERFEVTSEL：事件、umask、USR＋OS、啟用。</summary>
    private static ulong Sel(uint ev, uint um)
        => ev | (um << 8) | (1UL << 16) | (1UL << 17) | (1UL << 22);
}
