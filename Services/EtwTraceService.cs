using System.Globalization;
using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace XinSpect;

/// <summary>
/// 可落地的軌跡類別。檔名只認這三種，字串用 <see cref="EtwTraceService.Token"/> 對應。
/// </summary>
public enum TraceKind
{
    /// <summary>核心 DPC／ISR 延遲（對應核心 DeferedProcedureCalls＋Interrupt 關鍵字）。</summary>
    Dpc,
    /// <summary>DXGI／DWM 幀時間（對應 Microsoft-Windows-Dxgi 與 Dwm-Core）。</summary>
    FrameTime,
    /// <summary>核心執行緒遷移（對應核心 ContextSwitch 關鍵字）。</summary>
    Migration,
}

/// <summary>
/// 一個已落地的軌跡檔。<see cref="Time"/> 取自檔名（落地當下的本機時間），不是檔案系統時間。
/// </summary>
public sealed record TraceInfo(
    string Path, string FileName, TraceKind Kind, DateTime Time, long SizeBytes, string SizeText)
{
    /// <summary>畫面上的顯示名（類別 ＋ 時間）。</summary>
    public string Label => $"{EtwTraceService.DisplayName(Kind)} ・ {Time:yyyy-MM-dd HH:mm:ss}";
}

/// <summary>
/// ETW 軌跡的落地（把事件寫成<b>標準 .etl</b>檔）。
/// </summary>
/// <remarks>
/// <para>
/// 產出的是 Windows 事件追蹤的原生格式 .etl——這是<b>開放格式</b>（Windows Performance Analyzer、
/// xperf、wpr 都能直接開），不是本程式自創的私有格式。使用者拿到的是一個可以帶去別台機器、
/// 用微軟工具深入分析、或附在問題回報裡的檔案。
/// </para>
/// <para>
/// <b>誠實界線一：寫檔不是免費的。</b>ETW 一旦多一個檔案的消費端，取樣路徑上就多一份複製與寫入，
/// 有極小的效能成本（通常量不到，但在滿載的機器上並非零）。因此本服務<b>預設不由程式自動啟用</b>：
/// 使用者明確開啟「保存軌跡」時才建立 session，且寫檔本身會製造 DMA 與磁碟活動——這正是被觀察的對象。
/// </para>
/// <para>
/// <b>誠實界線二：檔案模式的工作階段拿不到即時事件流。</b>本服務以
/// <c>new TraceEventSession(sessionName, fileName)</c> 建立<b>檔案模式</b>工作階段；實測
/// TraceEvent 3.1.16 會讓存取 <c>session.Source</c> 直接丟
/// <c>InvalidOperationException</c>（"Only non-file based, non-circular ('real time') sessions
/// have can have a source associated with them."）。換言之，<b>「邊寫檔邊即時訂閱同一個 session」
/// 在這套函式庫上不可行</b>。即時觀察仍走既有的即時工作階段（DPC／幀時間／遷移各頁自己的），
/// 這份 .etl 是<b>平行落地的產物</b>：同一個提供者、同一組關鍵字，事後可完整重播。
/// </para>
/// <para>
/// <b>誠實界線三：刪不掉就說刪不掉。</b>WPA 開著某個 .etl 時，Windows 不允許刪除它。
/// <see cref="DeleteTrace"/> 遇此情況回 <c>false</c>，<see cref="Prune"/> 則整段停手
/// （繼續刪下去只會刪掉更新的檔，與「留下最新」的目的相反），絕不丟例外。
/// </para>
/// </remarks>
public sealed class EtwTraceService
{
    /// <summary>軌跡容量上限（位元組）；預設 512 MB，超過就從最舊的開始刪。</summary>
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    private readonly string _folder;
    private readonly long _maxBytes;

    /// <summary>
    /// <paramref name="folder"/> 為 <c>null</c> 時用 <c>%LOCALAPPDATA%\XinSpect\traces\</c>；
    /// 測試傳入暫存夾，不碰使用者真正的 %LOCALAPPDATA%。
    /// </summary>
    /// <param name="maxBytes">容量上限；≤ 0 時退回 <see cref="DefaultMaxBytes"/>。</param>
    public EtwTraceService(string? folder = null, long maxBytes = DefaultMaxBytes)
    {
        _folder = folder ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XinSpect", "traces");
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    /// <summary>軌跡存放資料夾（不一定存在，需先 <see cref="EnsureFolder"/>）。</summary>
    public string Folder => _folder;

    /// <summary>容量上限（位元組）。</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>建立資料夾（已存在則不動作），回傳資料夾路徑。</summary>
    public string EnsureFolder()
    {
        Directory.CreateDirectory(_folder);
        return _folder;
    }

    /// <summary>目前所有已落地軌跡的位元組總和；資料夾不存在時為 0。</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var t in ListTraces()) total += t.SizeBytes;
            return total;
        }
    }

    // ── 檔名：{類別}_{yyyyMMdd_HHmmss}.etl ────────────────────────────────

    /// <summary>類別 → 檔名用的字串（<c>dpc</c>／<c>frametime</c>／<c>migration</c>）。</summary>
    public static string Token(TraceKind kind) => kind switch
    {
        TraceKind.Dpc => "dpc",
        TraceKind.FrameTime => "frametime",
        TraceKind.Migration => "migration",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>類別 → 畫面上的中文名。</summary>
    public static string DisplayName(TraceKind kind) => kind switch
    {
        TraceKind.Dpc => "DPC／ISR 延遲",
        TraceKind.FrameTime => "幀時間",
        TraceKind.Migration => "執行緒遷移",
        _ => "未知",
    };

    /// <summary>字串 → 類別；不認得的字串回 <c>false</c>（不猜）。</summary>
    public static bool TryParseToken(string? token, out TraceKind kind)
    {
        switch (token)
        {
            case "dpc": kind = TraceKind.Dpc; return true;
            case "frametime": kind = TraceKind.FrameTime; return true;
            case "migration": kind = TraceKind.Migration; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>組出檔名（不含資料夾）。</summary>
    public static string FileNameFor(TraceKind kind, DateTime time)
        => $"{Token(kind)}_{time.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.etl";

    /// <summary>在存放資料夾下組出完整路徑。</summary>
    public string PathFor(TraceKind kind, DateTime time)
        => System.IO.Path.Combine(_folder, FileNameFor(kind, time));

    /// <summary>
    /// 從檔名反推類別與時間。<paramref name="name"/> 可以是檔名或完整路徑。
    /// 不符 <c>{類別}_{yyyyMMdd_HHmmss}.etl</c> 者回 <c>false</c>。
    /// </summary>
    public static bool TryParseName(string? name, out TraceKind kind, out DateTime time)
    {
        kind = default;
        time = default;
        if (string.IsNullOrWhiteSpace(name)) return false;

        string file = System.IO.Path.GetFileName(name);
        if (!file.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)) return false;

        string stem = file[..^4];                       // 去掉 ".etl"
        int us = stem.IndexOf('_');                     // 只切第一個底線：時間戳自己含一個底線
        if (us <= 0 || us == stem.Length - 1) return false;
        if (!TryParseToken(stem[..us], out kind)) return false;

        string stamp = stem[(us + 1)..];                // "20260912_101010"
        return DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out time);
    }

    /// <summary>位元組 → 可讀大小文字（沿用本程式其他頁面的 GB／MB／KB／B 寫法）。</summary>
    public static string SizeText(long bytes)
    {
        if (bytes < 0) return "—";
        return bytes >= 1L << 30 ? $"{bytes / 1024.0 / 1024 / 1024:0.00} GB"
             : bytes >= 1L << 20 ? $"{bytes / 1024.0 / 1024:0.0} MB"
             : bytes >= 1L << 10 ? $"{bytes / 1024.0:0} KB"
             : $"{bytes} B";
    }

    // ── 列舉／容量 ────────────────────────────────────────────────────────

    /// <summary>
    /// 列出資料夾內所有<b>符合命名</b>的軌跡，新到舊。資料夾不存在或無法讀取時回空集合（不丟例外）。
    /// 不符命名的 .etl（別的程式放的）與其他檔案一律略過——本清單只認本服務落地的產物。
    /// </summary>
    public IReadOnlyList<TraceInfo> ListTraces()
    {
        try
        {
            if (!Directory.Exists(_folder)) return [];
            var list = new List<TraceInfo>();
            foreach (var path in Directory.EnumerateFiles(_folder, "*.etl"))
            {
                string file = System.IO.Path.GetFileName(path);
                if (!TryParseName(file, out var kind, out var time)) continue;
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { /* 讀不到大小記 0，不因此漏列 */ }
                list.Add(new TraceInfo(path, file, kind, time, size, SizeText(size)));
            }
            return list
                .OrderByDescending(t => t.Time)
                .ThenByDescending(t => t.FileName, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return [];   // 資料夾權限／磁碟問題一律視為「沒有軌跡」，不讓呼叫端因列舉失敗而崩
        }
    }

    /// <summary>
    /// 刪除單一軌跡檔。被其他程式開著（例如 WPA）時回 <c>false</c> 而不是丟例外；
    /// 檔案本就不存在時回 <c>true</c>（冪等）。
    /// </summary>
    public bool DeleteTrace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!File.Exists(path)) return true;   // 已經不在了：目的已達成
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }                       // 被佔用（共用違規）
        catch (UnauthorizedAccessException) { return false; }       // 唯讀／權限不足
        catch { return false; }
    }

    /// <summary>
    /// 容量控管：總量超過 <paramref name="maxBytes"/> 時，從<b>最舊</b>的開始刪，直到降到上限內。
    /// 一旦遇到刪不掉的（被開著），立即停手——繼續下去只會刪掉更新的檔，與「留下最新」的目的相反。
    /// </summary>
    /// <returns>實際刪除的檔案數。</returns>
    public int Prune(long maxBytes)
    {
        if (maxBytes <= 0) return 0;
        var traces = ListTraces();               // 新到舊
        long total = 0;
        foreach (var t in traces) total += t.SizeBytes;
        if (total <= maxBytes) return 0;

        int deleted = 0;
        for (int i = traces.Count - 1; i >= 0 && total > maxBytes; i--)
        {
            if (!DeleteTrace(traces[i].Path)) break;
            total -= traces[i].SizeBytes;
            deleted++;
        }
        return deleted;
    }

    // ── 建立可寫檔的工作階段 ──────────────────────────────────────────────

    /// <summary>
    /// 建立一個把 ETW 寫進 .etl 的工作階段，並依類別啟用對應的提供者／關鍵字。
    /// 呼叫端負責在結束時 <c>Dispose()</c>（這會沖刷並關閉檔案）。
    /// </summary>
    /// <remarks>
    /// 建立前先 <see cref="Prune"/>，讓既有軌跡先降到容量上限之下，避免新檔一落地就超量。
    /// 核心類別（DPC、遷移）需要系統管理員權限；沒有權限時 <c>EnableKernelProvider</c> 會丟例外，照實往外拋。
    /// 回傳的 session 是檔案模式，<b>無法</b>取用 <c>Source</c>（見類別註解的誠實界線二），
    /// 它是拿來「停掉並沖刷檔案」的把手，不是拿來即時讀事件的。
    /// </remarks>
    public (TraceEventSession Session, string Path) CreateWritableSession(TraceKind kind)
    {
        EnsureFolder();
        Prune(_maxBytes);

        string path = PathFor(kind, DateTime.Now);
        var session = new TraceEventSession(SessionName(kind), path);
        try
        {
            EnableProviders(session, kind);
        }
        catch
        {
            try { session.Dispose(); } catch { /* 收尾失敗不掩蓋原始例外 */ }
            throw;
        }
        return (session, path);
    }

    /// <summary>各類別對應的工作階段名稱（系統層唯一；同名工作階段同時只能有一個）。</summary>
    public static string SessionName(TraceKind kind) => "XinSpect-Trace-" + Token(kind);

    /// <summary>依類別啟用提供者。關鍵字與既有的即時量測頁保持一致，落地檔才能對得起同一批事件。</summary>
    private static void EnableProviders(TraceEventSession session, TraceKind kind)
    {
        switch (kind)
        {
            case TraceKind.Dpc:
                // 一般 DPC ＋ 計時器 DPC ＋ 執行緒化 DPC 都要，另加 ISR；只收 PerfInfoDPC 會漏掉計時器 DPC
                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt);
                break;

            case TraceKind.FrameTime:
                session.EnableProvider("Microsoft-Windows-Dxgi", TraceEventLevel.Verbose, ulong.MaxValue);
                session.EnableProvider("Microsoft-Windows-Dwm-Core", TraceEventLevel.Verbose, ulong.MaxValue);
                break;

            case TraceKind.Migration:
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.ContextSwitch);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    // ── 檔頭驗證 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 檢查一個檔案的檔頭是不是 ETW .etl。<b>不確定就回 <c>false</c>，不猜。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 成立的條件有兩條，任一成立即視為 .etl：
    /// </para>
    /// <list type="number">
    /// <item>
    /// 前 4 位元組為 <c>00 00 01 00</c>（小端 0x00010000）：工作階段式 ETW 寫出的檔頭，
    /// 第一個緩衝區大小為 64 KiB。本服務寫出的檔正是這個樣子（實機以 TraceEvent 3.1.16 驗證）。
    /// </item>
    /// <item>
    /// 結構自洽性：offset 4..7 與 offset 8..11 這兩個 32 位元欄位同值，且其低位第二個位元組（offset 5）
    /// 為 <c>0x02</c>（追蹤格式的次版本號）。<b>實測本機 200 個系統 .etl</b>（SIH／waasmedic／NetSetup／
    /// WMI 等不同來源）全數成立——它們的第一個緩衝區大小各不相同，offset 0 根本不是穩定的魔數，
    /// 單看 offset 0 反而會把這些貨真價實的 .etl 判成假的。
    /// </item>
    /// </list>
    /// <para>
    /// 只讀檔頭、以 <c>FileShare.ReadWrite</c> 開啟，所以 WPA 正開著的檔也能驗證。
    /// </para>
    /// </remarks>
    public static bool IsValidEtl(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[12];
            int got = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (got < 4) return false;

            // (1) 工作階段式 ETW 的檔頭魔數
            if (head[0] == 0x00 && head[1] == 0x00 && head[2] == 0x01 && head[3] == 0x00) return true;

            // (2) 跨來源通用的結構自洽性
            if (got >= 12
                && head[4] == head[8] && head[5] == head[9] && head[6] == head[10] && head[7] == head[11]
                && head[5] == 0x02 && head[6] == 0x00 && head[7] == 0x00)
                return true;

            return false;
        }
        catch
        {
            return false;   // 讀不到就當它不是（含被獨佔鎖住的情況），不丟例外
        }
    }
}
