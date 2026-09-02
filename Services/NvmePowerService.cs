using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// NVMe 電源狀態：把「碟宣告的省電行為」與「閒置之後第一筆讀取真的等了多久」擺在一起。
/// </summary>
/// <remarks>
/// 為什麼要有這一頁：<b>1.8.0 的延遲直方圖顯示了 p99.9 有幾筆特別慢，但沒有說為什麼。</b>
/// 最常見的原因之一是碟在閒置時降到非運作狀態，被喚醒時要付出「離開延遲」——
/// 那是規格允許的行為，不是故障，可是在檔案總管裡完全看不出來。
/// <para>
/// 宣告值取自 Identify Controller（唯讀查詢，不寫任何東西）。實測則<b>只在使用者按下按鈕時</b>執行：
/// 刻意閒置 N 毫秒，然後計時<b>單獨一筆</b> 4K 未緩衝讀取，掃過一串 N。這是唯讀的，
/// 不改電源狀態、不設 Features、不清任何紀錄。
/// </para>
/// <para>
/// 誠實界線：量到的是「整條路徑」的耗時（作業系統 → 驅動 → 控制器 → 媒體），不是純粹的離開延遲；
/// 因此判決只在實測與宣告值同一量級時才敢歸因，差得遠就明說歸因不成立。
/// </para>
/// </remarks>
public sealed class NvmePowerService : ObservableObject
{
    /// <summary>掃描的實體磁碟上限（與 S.M.A.R.T. 頁一致）。</summary>
    private const int MaxDrives = 16;

    /// <summary>每一次量測讀取的區塊大小；未緩衝 I/O 要求磁區對齊。</summary>
    private const int Block = 4096;

    /// <summary>閒置階梯（ms）。0 代表接著上一筆立刻讀，用來當基線。</summary>
    private static readonly int[] IdleSteps = [0, 25, 50, 100, 250, 500, 1000, 2000, 4000];

    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public ObservableCollection<SmartDriveRow> Drives { get; } = [];
    public ObservableCollection<NvmePowerStateRow> States { get; } = [];
    public ObservableCollection<NvmeApstRow> Apst { get; } = [];
    public ObservableCollection<IdleLatencySample> Samples { get; } = [];

    private int _selected = -1;
    public int SelectedIndex { get => _selected; set => SetProperty(ref _selected, value); }

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanRead)); OnPropertyChanged(nameof(CanMeasure)); } }
    }

    public bool CanRead => !_busy;
    public bool CanMeasure => !_busy && States.Count > 0;

    private string _status = "選擇一顆 NVMe 磁碟後按「讀取」。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _apstStatus = "尚未讀取。";
    public string ApstStatus { get => _apstStatus; private set => SetProperty(ref _apstStatus, value); }

    private string _verdictHead = "尚未量測";
    public string VerdictHeadline { get => _verdictHead; private set => SetProperty(ref _verdictHead, value); }

    private string _verdictDetail = "按下「量閒置後的第一筆」才有實測值可以對照宣告的離開延遲。";
    public string VerdictDetail { get => _verdictDetail; private set => SetProperty(ref _verdictDetail, value); }

    private int _verdictSeverity;
    public int VerdictSeverity
    {
        get => _verdictSeverity;
        private set { if (SetProperty(ref _verdictSeverity, value)) OnPropertyChanged(nameof(VerdictBadge)); }
    }

    /// <summary>判決的顏色語意：0＝中性（沒事或判不出來）、1＝有可解釋的停頓、2＝超出宣告值。</summary>
    public Severity VerdictBadge => _verdictSeverity switch
    {
        2 => Severity.Serious,
        1 => Severity.Warning,
        _ => Severity.Neutral,
    };

    private string _progress = "";
    public string Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    private bool _enumerated;

    /// <summary>
    /// 第一次進頁時才去列磁碟。開機就對 16 個實體磁碟各發一次查詢，只為了一個使用者可能永遠不會開的頁面，
    /// 那是不必要的；也讓「建構頁面」在測試裡維持不碰硬體。
    /// </summary>
    public void EnsureDrives()
    {
        if (_enumerated) return;
        _enumerated = true;
        _ = RefreshDrivesAsync();
    }

    /// <summary>列出 NVMe 磁碟。非 NVMe 的碟沒有這套電源狀態表，不列進來假裝可以看。</summary>
    public async Task RefreshDrivesAsync()
    {
        var found = await Task.Run(() =>
        {
            var list = new List<SmartDriveRow>();
            for (int i = 0; i < MaxDrives; i++)
            {
                uint bus = StorageSmartService.TryGetBusType(i, out string name);
                if (bus is 16 or 17) list.Add(new SmartDriveRow(i, $"PhysicalDrive{i}（{name}）"));
            }
            return list;
        });

        Drives.Clear();
        foreach (var d in found) Drives.Add(d);
        if (Drives.Count > 0)
        {
            SelectedIndex = 0;
            Status = $"找到 {Drives.Count} 顆 NVMe 磁碟。按「讀取」取得宣告的電源狀態。";
        }
        else
        {
            Status = "沒有找到 NVMe 磁碟（本頁只適用 NVMe；SATA 的電源管理不走這套電源狀態表）。";
        }
    }

    /// <summary>讀宣告值：Identify Controller 的電源狀態表 ＋ APST 支援與門檻。</summary>
    public void Read()
    {
        if (_busy || SelectedIndex < 0 || SelectedIndex >= Drives.Count) return;
        int index = Drives[SelectedIndex].Index;
        IsBusy = true;
        Status = $"正在讀取 PhysicalDrive{index} 的 Identify Controller…";

        _ = Task.Run(() =>
        {
            byte[]? id = StorageSmartService.TryReadNvmeIdentify(index);
            byte[]? feat = id is null ? null : TryReadApstFeature(index);
            return (id, feat);
        }).ContinueWith(t =>
        {
            var (id, feat) = t.Result;
            States.Clear();
            Apst.Clear();
            Samples.Clear();
            ResetVerdict();

            if (id is null)
            {
                Status = "讀不到 Identify Controller：這顆碟的驅動不支援此查詢，或需要以系統管理員身分執行。"
                       + "讀不到就是讀不到，本頁不會拿規格書的數字填空。";
                IsBusy = false;
                return;
            }

            foreach (var r in NvmePowerDecoder.PowerStates(id)) States.Add(r);
            bool apst = NvmePowerDecoder.ApstSupported(id);
            _apstSupported = apst;

            if (feat is not null)
            {
                foreach (var r in NvmePowerDecoder.ApstTable(feat, States.Count)) Apst.Add(r);
                ApstStatus = apst
                    ? "碟宣告支援 APST，下表是它目前設定的降態門檻。"
                    : "碟未宣告支援 APST，但仍讀到了門檻表；以碟的宣告為準看待。";
            }
            else
            {
                ApstStatus = apst
                    ? "碟宣告支援 APST（自主降態），但這台機器的驅動不允許用唯讀查詢取回門檻表——"
                      + "所以「幾毫秒之後降到哪一階」讀不到，本頁不猜。下方的實測可以間接看出它的行為。"
                    : "碟未宣告支援 APST。降態若真的發生，是由主機端的電源管理決定的（Windows 的 NVMe 閒置電源政策）。";
            }

            int nop = States.Count(s => s.NonOperational);
            Status = $"共 {States.Count} 個電源狀態，其中 {nop} 個是非運作（睡眠）狀態。"
                   + "這些是裝置宣告的數字，不是量到的。";
            IsBusy = false;
            OnPropertyChanged(nameof(CanMeasure));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>實測：閒置 N 毫秒之後，單獨一筆 4K 讀取要多久。全程唯讀。</summary>
    public void Measure()
    {
        if (_busy || SelectedIndex < 0 || SelectedIndex >= Drives.Count || States.Count == 0) return;
        int index = Drives[SelectedIndex].Index;
        IsBusy = true;
        Samples.Clear();
        ResetVerdict();

        var declared = States.ToList();
        bool apst = _apstSupported;

        _ = Task.Run(() => Sweep(index, p => Progress = p))
            .ContinueWith(t =>
            {
                IsBusy = false;
                Progress = "";
                if (t.Result is not { } samples)
                {
                    Status = $"量不到：開不了 PhysicalDrive{index} 做未緩衝讀取（通常是需要系統管理員身分）。"
                           + "上方的宣告值仍然有效。";
                    return;
                }

                foreach (var s in samples) Samples.Add(s);
                var v = NvmePowerDecoder.Verdict(declared, apst, samples);
                VerdictHeadline = v.Headline;
                VerdictDetail = v.Detail;
                VerdictSeverity = v.Severity;
                Status = $"量測完成：{samples.Count} 個閒置階梯，全部是唯讀的單筆讀取。";
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>碟是否宣告支援 APST（讀 Identify 時記下，判決要用）。</summary>
    private bool _apstSupported;

    private void ResetVerdict()
    {
        VerdictHeadline = "尚未量測";
        VerdictDetail = "按下「量閒置後的第一筆」才有實測值可以對照宣告的離開延遲。";
        VerdictSeverity = 0;
    }

    // ── 實測 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 逐級閒置後量單筆讀取。每一筆讀不同位移，避免碟上的快取讓第二次變快而看不出降態。
    /// </summary>
    private static unsafe List<IdleLatencySample>? Sweep(int index, Action<string> progress)
    {
        // 這趟有大半時間是刻意的閒置，動畫在那期間重繪就等於量到「有活動時的閒置」
        using var quiet = Motion.Suspend();

        void* buf = NativeMemory.AlignedAlloc(Block, Block);
        try
        {
            using var h = File.OpenHandle($@"\\.\PhysicalDrive{index}",
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite, NoBuffering);

            var span = new Span<byte>(buf, Block);
            var rnd = new Random(20260902);
            var result = new List<IdleLatencySample>();

            // 先讀一筆把路徑熱起來（驅動、頁表、佇列），這一筆不計入
            RandomAccess.Read(h, span, 0);

            foreach (int idle in IdleSteps)
            {
                progress($"閒置 {idle} ms 後量一筆…");
                if (idle > 0) Thread.Sleep(idle);

                // 位移取磁區對齊的隨機值，範圍限在前 8 GB（不必知道容量，也避開尾端未分配）
                long off = (long)rnd.Next(1, 2_000_000) * Block;
                long t0 = Stopwatch.GetTimestamp();
                RandomAccess.Read(h, span, off);
                long t1 = Stopwatch.GetTimestamp();

                result.Add(new IdleLatencySample(idle, (t1 - t0) * 1_000_000.0 / Stopwatch.Frequency));
            }
            return result;
        }
        catch (Exception ex)
        {
            Diag.Swallow("NvmePowerService.Sweep", ex, "閒置後首筆讀取量不到；宣告值不受影響。");
            return null;
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    /// <summary>
    /// 嘗試以唯讀查詢取 Get Features 0x0C（APST）的資料區。
    /// Windows 只對部分 Feature 開放這條路，取不到是常態——取不到就回 null，由呼叫端如實說明。
    /// </summary>
    private static byte[]? TryReadApstFeature(int index) => StorageSmartService.TryReadNvmeFeature(index, 0x0C, 256);
}
