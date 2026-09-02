using System.Collections.ObjectModel;
using System.Diagnostics;

namespace XinSpect;

/// <summary>一個駐留計數器的量測結果。</summary>
public sealed class ResidencyRow
{
    public required string Name { get; init; }
    public required string Text { get; init; }
    public required string Note { get; init; }
}

/// <summary>
/// 隱形停頓：SMI 次數與 C-state 駐留。全部是唯讀 MSR，連效能事件都不必編程。
/// </summary>
/// <remarks>
/// 這一頁回答的是「時間去哪了」裡最看不見的一段：
/// <list type="bullet">
/// <item><b>SMI</b>（系統管理中斷）由韌體處理，發生時核心離開作業系統的視野。工作管理員永遠是
/// 0%，但音訊會爆、幀時間會有尖峰。MSR <c>0x34</c> 是唯一能看到它的地方——而且<b>只有次數，
/// 沒有時間</b>：硬體沒有記錄每次待了多久，所以本頁不換算損失時間。</item>
/// <item><b>C-state 駐留</b>告訴你封裝與核心真的睡了多久。閒著卻沒睡，代表有東西定期把它叫起來；
/// 那是實實在在的耗電與發熱，卻不會出現在任何一張使用率圖上。</item>
/// </list>
/// 取樣方式：釘在單一核心上讀兩次（相隔約一秒），以 TSC 差值為分母。
/// 每一個 MSR 讀不到就標「未實作或未開放」，不拿 0% 頂替。
/// </remarks>
public sealed class InvisibleStallService : ObservableObject
{
    private const uint MsrTsc = 0x10;
    private const uint MsrSmiCount = 0x34;

    /// <summary>封裝層駐留（依平台而異，讀不到就是讀不到）。</summary>
    private static readonly (string Name, uint Msr, string Note)[] PackageMsrs =
    [
        ("封裝 C2", 0x60D, "整顆封裝的淺層省電；核心都閒下來才進得去"),
        ("封裝 C3", 0x3F8, "更深一層，快取開始被清空"),
        ("封裝 C6", 0x3F9, "深層：核心電壓可被移除，離開時要付出喚醒延遲"),
        ("封裝 C7", 0x3FA, "最深層（部分平台未實作）"),
    ];

    /// <summary>逐核駐留（釘選的那一顆）。</summary>
    private static readonly (string Name, uint Msr, string Note)[] CoreMsrs =
    [
        ("本核 C3", 0x3FC, "這顆核自己的淺層省電"),
        ("本核 C6", 0x3FD, "這顆核的深層省電（最常見的一個）"),
        ("本核 C7", 0x3FE, "更深（部分平台未實作）"),
    ];

    /// <summary>取樣窗（毫秒）。太短會被雜訊吃掉，太長使用者等不住。</summary>
    private const int WindowMs = 1000;

    public ObservableCollection<ResidencyRow> Rows { get; } = [];

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanMeasure)); }
    }

    public bool CanMeasure => !_busy;

    private string _status = "按下量測後會取兩次樣（相隔約一秒），全程唯讀 MSR。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private InvisibleStallVerdict _verdict = new()
    {
        Headline = "尚未量測", Severity = Severity.Neutral,
        Detail = "按下量測後會用差值算出 SMI 頻率與各 C-state 的駐留比例。",
    };
    public InvisibleStallVerdict Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }

    private string _smiTotal = "—";
    public string SmiTotalText { get => _smiTotal; private set => SetProperty(ref _smiTotal, value); }

    private string _smiRate = "—";
    public string SmiRateText { get => _smiRate; private set => SetProperty(ref _smiRate, value); }

    public void Measure()
    {
        if (_busy) return;
        IsBusy = true;
        Status = "量測中（約一秒）…";

        _ = Task.Run(Run).ContinueWith(t =>
        {
            var r = t.Result;
            Rows.Clear();
            foreach (var row in r.Rows) Rows.Add(row);
            Verdict = r.Verdict;
            SmiTotalText = r.SmiTotal;
            SmiRateText = r.SmiRate;
            Status = r.Status;
            IsBusy = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private readonly record struct Result(List<ResidencyRow> Rows, InvisibleStallVerdict Verdict,
                                          string SmiTotal, string SmiRate, string Status);

    private static Result Fail(string status) => new(
        [], new InvisibleStallVerdict { Headline = "量不到", Severity = Severity.Neutral, Detail = status },
        "—", "—", status);

    private static Result Run()
    {
        using var bridge = WinRing0Bridge.Create();
        if (!bridge.Available)
            return Fail("讀不到 MSR：" + bridge.Error + "（需要以系統管理員身分執行）。");

        var all = CpuAffinity.AllLogicalProcessors();
        if (all.Count == 0) return Fail("列不出邏輯處理器，無法釘選核心。");

        // 釘在最後一顆邏輯處理器：核心 0 要服務中斷，量到的駐留會偏低而且不具代表性
        using var pin = CpuAffinity.Pinned(all[^1]);
        if (!pin.Ok) return Fail("無法釘選核心；逐核駐留必須在同一顆核上讀兩次才有意義，因此不量。");

        try
        {
            var msrs = PackageMsrs.Concat(CoreMsrs).ToArray();
            var first = new ulong?[msrs.Length];
            for (int i = 0; i < msrs.Length; i++) first[i] = bridge.ReadMsrPair64(msrs[i].Msr);

            ulong? tsc0 = bridge.ReadMsrPair64(MsrTsc);
            ulong? smi0 = bridge.ReadMsrPair64(MsrSmiCount);
            long t0 = Stopwatch.GetTimestamp();

            Thread.Sleep(WindowMs);

            long t1 = Stopwatch.GetTimestamp();
            ulong? tsc1 = bridge.ReadMsrPair64(MsrTsc);
            ulong? smi1 = bridge.ReadMsrPair64(MsrSmiCount);
            var second = new ulong?[msrs.Length];
            for (int i = 0; i < msrs.Length; i++) second[i] = bridge.ReadMsrPair64(msrs[i].Msr);

            double seconds = (t1 - t0) / (double)Stopwatch.Frequency;
            ulong tscDelta = Delta(tsc0, tsc1) ?? 0;

            var rows = new List<ResidencyRow>();
            double? deepestPkg = null;
            for (int i = 0; i < msrs.Length; i++)
            {
                ulong? d = Delta(first[i], second[i]);
                rows.Add(new ResidencyRow
                {
                    Name = msrs[i].Name,
                    Text = InvisibleStallDecoder.ResidencyText(d, tscDelta),
                    Note = msrs[i].Note,
                });
                bool isPackage = i < PackageMsrs.Length;
                if (isPackage && d is { } dv && InvisibleStallDecoder.Percent(dv, tscDelta) is { } p && p <= 100)
                    deepestPkg = Math.Max(deepestPkg ?? 0, p);
            }

            ulong smiDelta = Delta(smi0, smi1) ?? 0;
            var verdict = InvisibleStallDecoder.Judge(smiDelta, seconds, smi1 ?? 0,
                                                     smi0 is null ? null : deepestPkg);

            return new Result(rows, verdict,
                smi1 is { } tot ? $"{tot:N0} 次" : "讀不到（MSR 0x34 未開放）",
                smi0 is null ? "—" : $"{smiDelta / seconds:0.##} 次/秒",
                $"量測完成：取樣窗 {seconds:0.00} 秒，TSC 前進 {tscDelta:N0} 個刻度。全程唯讀，沒有寫入任何暫存器。");
        }
        catch (Exception ex)
        {
            Diag.Swallow("InvisibleStallService.Run", ex, "隱形停頓量不到，卡片顯示為「量不到」。");
            return Fail("量測中發生例外，已記入診斷紀錄。");
        }
    }

    /// <summary>兩次讀值的差；任一次讀不到、或第二次比第一次小（回捲）就回 null，不猜。</summary>
    private static ulong? Delta(ulong? a, ulong? b)
        => a is { } x && b is { } y && y >= x ? y - x : null;
}
