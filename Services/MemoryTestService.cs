using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>單一圖樣的測試結果。</summary>
public sealed class MemoryTestRow
{
    public MemoryTestRow(string name, string detail, long errors, double seconds, double mbPerSec, string? first)
    {
        Name = name;
        Detail = detail;
        Errors = errors;
        Seconds = seconds;
        MbPerSec = mbPerSec;
        FirstError = first;
    }

    public string Name { get; }
    public string Detail { get; }
    public long Errors { get; }
    public double Seconds { get; }
    public double MbPerSec { get; }
    /// <summary>第一處不符的位移與內容；沒有錯誤時為 <c>null</c>。</summary>
    public string? FirstError { get; }

    public string ResultText => Errors == 0 ? "通過" : $"{Errors:N0} 處不符";
    public Severity Severity => Errors == 0 ? Severity.Good : Severity.Critical;
    public string TimeText => $"{Seconds:0.0} s";
    public string ThroughputText => MbPerSec > 0 ? $"{MbPerSec:N0} MB/s" : "—";
    /// <summary>有錯就顯示第一處不符的實際位址與內容，否則說明這輪圖樣在抓什麼。</summary>
    public string NoteText => FirstError ?? Detail;
}

/// <summary>
/// 記憶體圖樣檢測：在自己配置到的一段記憶體上做真實的寫入／回讀比對，抓卡死的位元、
/// 相鄰位元干擾與位址解碼錯誤。全部在本行程內完成，不需要任何外部程式。
/// </summary>
/// <remarks>
/// <b>這不是 MemTest86 的替代品，也不假裝是。</b>作業系統的虛擬記憶體讓應用程式無法指定實體位址、
/// 更碰不到已被系統與其他行程佔用的頁面，因此本測試只能覆蓋「當下配置得到的那一段」——
/// 通過不代表整條記憶體沒問題，但<b>只要出現一處不符，那就是真的量到了</b>。
/// 徹底的診斷仍須在開機前環境（MemTest86 等）跑滿整條記憶體。
/// <para>
/// 刻意「整段寫完再整段回讀」而不是寫一格驗一格：後者會從快取讀回，量到的是 CPU 快取而非記憶體本身。
/// </para>
/// </remarks>
public sealed class MemoryTestService : ObservableObject
{
    private const long Mib = 1024 * 1024;
    private const long Gib = 1024 * Mib;
    private const int BlockUlongs = 8 * 1024 * 1024;   // 每塊 64 MiB：進度與取消的粒度，也避開單一巨大配置
    private const long BlockBytes = BlockUlongs * 8L;
    private const long Reserve = 1 * Gib;              // 留給系統與本程式：吃乾可用記憶體只會測到分頁檔

    private const ulong P55 = 0x5555555555555555UL;
    private const ulong PAA = 0xAAAAAAAAAAAAAAAAUL;

    /// <summary>圖樣清單。順序＝執行順序；說明是要讓使用者知道每一輪到底在抓什麼。</summary>
    private static readonly (string Name, string Detail)[] Patterns =
    {
        ("全 0 / 全 1",  "每個位元先全寫 0 再全寫 1，抓永遠卡在某一態的位元"),
        ("0x55 / 0xAA", "相鄰位元交錯翻轉，抓相鄰位元之間互相干擾"),
        ("位址即資料",   "每格寫入自己的位移量，抓位址解碼錯誤（寫這裡卻讀到別處）"),
        ("移動反轉",     "整段 0x55 後升序改 0xAA、再降序改回，抓寫入時波及鄰格的干擾"),
        ("隨機圖樣",     "固定種子的偽隨機資料，補上規則圖樣照不到的位元組合"),
    };

    public string[] SizeChoices { get; } = { "256 MB", "512 MB", "1 GB", "2 GB", "4 GB" };

    private int _sizeIndex = 2;
    public int SizeIndex
    {
        get => _sizeIndex;
        set { if (SetProperty(ref _sizeIndex, value)) OnPropertyChanged(nameof(PlanText)); }
    }

    private long Requested => 256 * Mib << _sizeIndex;

    /// <summary>實際會測多少：受可用記憶體扣掉保留量後的上限約束，並向下對齊到區塊。</summary>
    public string PlanText
    {
        get
        {
            long avail = AvailableBytes(), size = Planned(avail);
            return size < BlockBytes
                ? $"可用記憶體約 {avail / (double)Gib:0.0} GB，扣掉保留的 1 GB 後不足一個區塊（64 MB），目前無法測試。"
                : $"預計測試 {size / (double)Mib:N0} MB（共 {size / BlockBytes} 個 64 MB 區塊）；"
                  + $"目前可用約 {avail / (double)Gib:0.0} GB，已保留 1 GB 給系統。";
        }
    }

    private long Planned(long avail)
    {
        long size = Math.Min(Requested, Math.Max(0, avail - Reserve));
        return size - size % BlockBytes;
    }

    private static long AvailableBytes() => (long)(new MemoryService().ReadStats().AvailGB * Gib);

    private CancellationTokenSource? _cts;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未測試";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始測試」在一段實際配置到的記憶體上做寫入／回讀比對。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<MemoryTestRow> Rows { get; } = new();

    private string _tested = "—", _errors = "—", _elapsed = "—", _speed = "—";
    public string TestedText { get => _tested; private set => SetProperty(ref _tested, value); }
    public string ErrorText { get => _errors; private set => SetProperty(ref _errors, value); }
    public string ElapsedText { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string SpeedText { get => _speed; private set => SetProperty(ref _speed, value); }

    private Severity _verdict = Severity.Neutral;
    /// <summary>整體結論：全數通過為 Good，出現任何不符即 Critical（記憶體錯誤沒有「輕微」）。</summary>
    public Severity Verdict { get => _verdict; private set => SetProperty(ref _verdict, value); }
    public void Start()
    {
        if (IsRunning) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        long avail = AvailableBytes(), size = Planned(avail);
        if (size < BlockBytes)
        {
            Phase = "無法開始";
            StatusLine = $"可用記憶體不足：目前約 {avail / (double)Gib:0.0} GB，扣掉保留給系統的 1 GB 後不足 64 MB。"
                       + "請先關閉部分程式，或改選較小的測試量。";
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsRunning = true;
        Phase = "測試中";
        ProgressFraction = 0;
        Rows.Clear();
        Verdict = Severity.Neutral;
        TestedText = $"{size / (double)Mib:N0} MB";
        ErrorText = ElapsedText = SpeedText = "測試中…";
        StatusLine = "正在配置測試區塊…";

        var prog = new Progress<(double Frac, string Status)>(t => { ProgressFraction = t.Frac; StatusLine = t.Status; });
        var report = (IProgress<(double, string)>)prog;
        var sw = Stopwatch.StartNew();

        try
        {
            var rows = await Task.Run(() => Measure(size, ct, report), ct);
            sw.Stop();

            long bad = 0;
            double mb = 0;
            foreach (var r in rows)
            {
                Rows.Add(r);
                bad += r.Errors;
                mb += r.MbPerSec * r.Seconds;         // 還原成各輪實際讀寫的資料量（MB）
            }

            Verdict = bad == 0 ? Severity.Good : Severity.Critical;
            ErrorText = bad == 0 ? "0" : $"{bad:N0}";
            ElapsedText = $"{sw.Elapsed.TotalSeconds:0.0} s";
            SpeedText = sw.Elapsed.TotalSeconds > 0 ? $"{mb / sw.Elapsed.TotalSeconds:N0} MB/s" : "—";
            Phase = bad == 0 ? "完成" : "發現錯誤";
            ProgressFraction = 1;
            StatusLine = bad == 0
                ? $"全部 {Patterns.Length} 種圖樣通過，{size / (double)Mib:N0} MB 範圍內未量到任何不符。"
                  + "這只代表這一段記憶體在這些圖樣下正常，不等於整條記憶體都沒問題。"
                : $"量到 {bad:N0} 處不符——記憶體、時序或超頻設定其中之一有問題。"
                  + "建議取消超頻／XMP 後重測，並以 MemTest86 在開機前環境跑滿整條記憶體確認。";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            StatusLine = "測試已停止；未跑完的圖樣不列入結果。";
            ErrorText = ElapsedText = SpeedText = "—";
        }
        catch (OutOfMemoryException)
        {
            Phase = "配置失敗";
            StatusLine = "無法配置足夠的測試記憶體（期間其他程式可能佔用了更多）。請改選較小的測試量後重試。";
            ErrorText = ElapsedText = SpeedText = "—";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = "測試失敗：" + ex.Message;
            ErrorText = ElapsedText = SpeedText = "—";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(PlanText));

            // 剛才拿了好幾 GB，區塊已無人參考；主動收回並壓實，否則本程式會一直掛著這塊足跡
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
    // ── 量測（全部在背景執行緒上跑）──────────────────────────
    // 以下欄位都是「單次執行期間」的暫存狀態：同時只會有一輪測試（IsRunning 擋著）。

    private IProgress<(double, string)> _report = new Progress<(double, string)>();
    private double _lo, _span;
    private int _passes, _pass, _blockCount;
    private string _name = "";
    private long _errCount, _touched;
    private string? _first;

    private List<MemoryTestRow> Measure(long size, CancellationToken ct, IProgress<(double, string)> report)
    {
        _report = report;
        _blockCount = (int)(size / BlockBytes);

        var blocks = new List<ulong[]>(_blockCount);
        for (int i = 0; i < _blockCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            // 不預先歸零：緊接著就會整段寫過，先清一遍等於白跑一趟記憶體
            blocks.Add(GC.AllocateUninitializedArray<ulong>(BlockUlongs));
            report.Report(((i + 1) / (double)_blockCount * 0.05,
                           $"配置測試區塊 {(i + 1) * BlockBytes / Mib:N0} / {size / Mib:N0} MB"));
        }

        var rows = new List<MemoryTestRow>(Patterns.Length);
        for (int p = 0; p < Patterns.Length; p++)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(RunPattern(p, blocks, ct));
        }
        return rows;
    }

    private MemoryTestRow RunPattern(int index, List<ulong[]> blocks, CancellationToken ct)
    {
        var (name, detail) = Patterns[index];
        _name = name;
        _lo = 0.05 + index * 0.95 / Patterns.Length;   // 前 5% 留給配置
        _span = 0.95 / Patterns.Length;
        _passes = index switch { 0 or 1 => 4, 3 => 3, _ => 2 };
        _pass = 0;
        _errCount = 0;
        _touched = 0;
        _first = null;

        var sw = Stopwatch.StartNew();
        switch (index)
        {
            case 0:
                WriteConst(blocks, 0UL, ct); VerifyConst(blocks, 0UL, ct);
                WriteConst(blocks, ulong.MaxValue, ct); VerifyConst(blocks, ulong.MaxValue, ct);
                break;
            case 1:
                WriteConst(blocks, P55, ct); VerifyConst(blocks, P55, ct);
                WriteConst(blocks, PAA, ct); VerifyConst(blocks, PAA, ct);
                break;
            case 2:
                WriteAddress(blocks, ct); VerifyAddress(blocks, ct);
                break;
            case 3:
                WriteConst(blocks, P55, ct); InvertUp(blocks, ct); InvertDown(blocks, ct);
                break;
            default:
                WriteRandom(blocks, ct); VerifyRandom(blocks, ct);
                break;
        }
        sw.Stop();

        double seconds = sw.Elapsed.TotalSeconds, mb = _touched / (double)Mib;
        return new MemoryTestRow(name, detail, _errCount, seconds, seconds > 0 ? mb / seconds : 0, _first);
    }

    /// <summary>回報「本輪第 <paramref name="done"/>+1 個區塊已處理完」，換算成整體進度。</summary>
    private void Tick(int done, string what)
    {
        _touched += BlockBytes;
        double frac = (_pass * (double)_blockCount + done + 1) / (_passes * (double)_blockCount);
        _report.Report((_lo + _span * frac, $"{_name}：{what}　{done + 1} / {_blockCount} 區塊"));
    }

    private void Bad(ulong index, ulong expect, ulong actual)
    {
        _errCount++;
        // 只留第一處：真壞掉的記憶體可能出現上百萬處不符，全部記下來只會拖慢測試
        _first ??= $"位移 0x{index * 8:X}　預期 {Hex(expect)}，讀到 {Hex(actual)}";
    }

    private static string Hex(ulong v) => "0x" + v.ToString("X16");
    // ── 各種掃法 ──────────────────────────────────────────────
    // 一律「整段寫完 → 整段回讀」，中間隔了好幾百 MB，回讀時快取早就被沖掉，量到的才是記憶體。

    private void WriteConst(List<ulong[]> blocks, ulong v, CancellationToken ct)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Fill(blocks[b], v);
            Tick(b, "寫入 " + Hex(v));
        }
        _pass++;
    }

    private void VerifyConst(List<ulong[]> blocks, ulong v, CancellationToken ct)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var span = blocks[b].AsSpan();

            // IndexOfAnyExcept 是向量化的：沒有錯誤時幾乎就是純頻寬，只有真的找到不符才逐格記錄
            for (int from = 0; from < span.Length; )
            {
                int at = span[from..].IndexOfAnyExcept(v);
                if (at < 0) break;
                int i = from + at;
                Bad((ulong)b * BlockUlongs + (ulong)i, v, span[i]);
                from = i + 1;
            }
            Tick(b, "回讀比對 " + Hex(v));
        }
        _pass++;
    }

    private void WriteAddress(List<ulong[]> blocks, CancellationToken ct)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var arr = blocks[b];
            ulong bas = (ulong)b * BlockUlongs;
            for (int i = 0; i < arr.Length; i++) arr[i] = bas + (ulong)i;
            Tick(b, "寫入位址值");
        }
        _pass++;
    }

    private void VerifyAddress(List<ulong[]> blocks, CancellationToken ct)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var arr = blocks[b];
            ulong bas = (ulong)b * BlockUlongs;
            for (int i = 0; i < arr.Length; i++)
            {
                ulong want = bas + (ulong)i;
                if (arr[i] != want) Bad(want, want, arr[i]);
            }
            Tick(b, "回讀位址值");
        }
        _pass++;
    }

    /// <summary>升序：每格先驗 0x55 再改寫 0xAA。讀寫交錯進行，才抓得到「寫這格波及隔壁格」的干擾。</summary>
    private void InvertUp(List<ulong[]> blocks, CancellationToken ct)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var arr = blocks[b];
            ulong bas = (ulong)b * BlockUlongs;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != P55) Bad(bas + (ulong)i, P55, arr[i]);
                arr[i] = PAA;
            }
            Tick(b, "升序 0x55 → 0xAA");
        }
        _pass++;
    }

    /// <summary>降序改回 0x55：方向相反才照得到只在某個存取次序下才現形的干擾。</summary>
    private void InvertDown(List<ulong[]> blocks, CancellationToken ct)
    {
        for (int b = blocks.Count - 1; b >= 0; b--)
        {
            ct.ThrowIfCancellationRequested();
            var arr = blocks[b];
            ulong bas = (ulong)b * BlockUlongs;
            for (int i = arr.Length - 1; i >= 0; i--)
            {
                if (arr[i] != PAA) Bad(bas + (ulong)i, PAA, arr[i]);
                arr[i] = P55;
            }
            Tick(blocks.Count - 1 - b, "降序 0xAA → 0x55");
        }
        _pass++;
    }

    private void WriteRandom(List<ulong[]> blocks, CancellationToken ct)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var arr = blocks[b];
            ulong s = Seed(b);
            for (int i = 0; i < arr.Length; i++) arr[i] = Next(ref s);
            Tick(b, "寫入隨機圖樣");
        }
        _pass++;
    }

    private void VerifyRandom(List<ulong[]> blocks, CancellationToken ct)
    {
        for (int b = 0; b < blocks.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var arr = blocks[b];
            ulong s = Seed(b), bas = (ulong)b * BlockUlongs;
            for (int i = 0; i < arr.Length; i++)
            {
                ulong want = Next(ref s);
                if (arr[i] != want) Bad(bas + (ulong)i, want, arr[i]);
            }
            Tick(b, "回讀隨機圖樣");
        }
        _pass++;
    }

    /// <summary>每塊固定種子：不必把幾 GB 的隨機資料留著，回讀時原地重算同一串就能比對。</summary>
    private static ulong Seed(int block)
    {
        ulong s = 0x9E3779B97F4A7C15UL ^ ((ulong)block + 1) * 0xBF58476D1CE4E5B9UL;
        return s == 0 ? 1UL : s;                       // xorshift 的零狀態會卡死，不可能出現也要擋
    }

    private static ulong Next(ref ulong s)
    {
        s ^= s << 13;
        s ^= s >> 7;
        s ^= s << 17;
        return s;
    }
}
