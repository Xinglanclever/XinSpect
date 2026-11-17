using System.Management;
using System.Runtime.CompilerServices;

namespace XinSpect;

/// <summary>
/// 單顆碟的可靠性計數器原值。<b>來源未提供的欄位一律 null，不以 0 代替</b>——
/// 「0」是合法值（磨損 0%、延遲 0 ms），與「提供者沒給」是兩件不同的事。
/// </summary>
public sealed record StorageReliabilityRaw(
    int DeviceId,
    long? Wear,
    long? Temperature,
    long? TemperatureMax,
    long? PowerOnHours,
    long? StartStopCycleCount,
    long? StartStopCycleCountMax,
    long? LoadUnloadCycleCount,
    long? LoadUnloadCycleCountMax,
    long? ReadErrorsTotal,
    long? ReadErrorsCorrected,
    long? ReadErrorsUncorrected,
    long? WriteErrorsTotal,
    long? WriteErrorsCorrected,
    long? WriteErrorsUncorrected,
    long? ReadLatencyMax,
    long? WriteLatencyMax,
    long? FlushLatencyMax,
    string? ManufactureDate);

/// <summary>
/// 可靠性計數器來源的可注入接縫。形狀照 <see cref="IAcpiTableSource"/>：
/// 可用性＋不可用原因＋資料；真實以 WMI 取，測試注入固定資料、不碰 WMI。
/// </summary>
/// <remarks>
/// <b><see cref="Available"/> 是「最近一次 <see cref="Read"/> 的結果」，不是事前承諾。</b>
/// 真實來源在第一次查詢之前無從得知 WMI 會不會失敗，所以
/// <see cref="StorageReliabilityFactsService.Collect"/> <b>先讀再判</b>——
/// 反過來（先看 <c>Available</c> 再讀）會讓「查詢失敗並帶著原因」掉進
/// 「可用但沒有資料」那一支，把一次失敗講成「不是錯誤」。
/// </remarks>
public interface IStorageReliabilitySource
{
    bool Available { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<StorageReliabilityRaw> Read();
}

/// <summary>
/// WMI 儲存可靠性查詢的呼叫端逾時包裝：把 <see cref="StorageReliabilityWmiSource.Read"/> 放到
/// 專屬執行緒上跑，超過預算便停止等待——底層讀取未被取消，收集鏈以失敗狀態繼續。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要它（2026-11-06 實測）</b>：守門測試與 CLI 採集並發時，WMI 的
/// <c>Get()</c>／<c>GetRelated()</c> 會卡死不回——整條收集鏈陪葬，進程停在核心
/// 不可中斷等待，連 taskkill /F 都殺不掉，只能重開機。逾時的執行緒一樣會留在核心
/// （與 <see cref="StorageProbeBreaker"/> 類別說明裡 DiskIo 的實測記錄同一代價），
/// 但犧牲一條執行緒換整條收集鏈活著，是划算的。
/// </para>
/// <para>
/// 同一個 inner 物件共用執行中閘門；換包裝器也不能重開尚未結束的讀取。
/// 逾時後不處置 inner 或同步資源，也不把晚到結果改寫成該次呼叫成功。
/// 通用包裝器的閘門按來源物件身份隔離；正式 StorageReliabilityWmiSource 另在 Read
/// 邊界共用本機 StorageWMI 閘門，跨來源實例仍不重發。其他直接 StorageWMI 查詢
/// （DiskInfo 等）尚未接入，也沒有跨程序的提供者斷路器。
/// </para>
/// </remarks>
public sealed class StorageReliabilityTimeoutSource : IStorageReliabilitySource
{
    private readonly IStorageReliabilitySource _inner;
    private readonly int _budgetMs;
    private readonly ReadState _state;
    private static readonly ConditionalWeakTable<IStorageReliabilitySource, ReadState> States = new();

    private sealed class ReadState
    {
        public Task<ReadResult>? InFlight;
    }

    private sealed record ReadResult(IReadOnlyList<StorageReliabilityRaw> Rows, bool Available, string? Reason);

    public StorageReliabilityTimeoutSource(IStorageReliabilitySource inner, int budgetMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetMs);
        _inner = inner;
        _budgetMs = budgetMs;
        _state = States.GetValue(inner, _ => new ReadState());
    }

    public bool Available { get; private set; } = true;

    public string? UnavailableReason { get; private set; }

    /// <summary>注入生命週期回歸等待底層讀取及結果狀態存取結束；不會取消或釋放來源。</summary>
    internal Task ReadCompletion
    {
        get { lock (_state) return _state.InFlight ?? Task.CompletedTask; }
    }

    public IReadOnlyList<StorageReliabilityRaw> Read()
    {
        TaskCompletionSource<ReadResult> completion;
        lock (_state)
        {
            if (_state.InFlight is { IsCompleted: false })
                return Failure("同一儲存可靠性來源的先前讀取仍在執行——未取消底層查詢，不再啟動另一個讀取");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _state.InFlight = completion.Task;
        }

        try
        {
            var worker = new Thread(() =>
            {
                ReadResult result;
                try
                {
                    // Read 與其結果狀態在同一 worker 內完成；狀態 getter 也可能失敗或卡住。
                    var rows = _inner.Read();
                    result = rows is null
                        ? new([], false, "儲存可靠性來源違反契約：Read 回傳 null")
                        : new(rows, _inner.Available, _inner.UnavailableReason);
                }
                catch (Exception ex)
                {
                    result = new([], false, $"儲存可靠性來源讀取或結果狀態失敗：{ex.GetType().Name}：{ex.Message}");
                }
                // 只有 inner 讀取與狀態存取均已結束，才開放後續讀取；晚到結果不碰包裝器狀態。
                completion.SetResult(result);
            })
            {
                IsBackground = true,
                Name = "StorageReliability-Wmi-Read",
            };
            worker.Start();
        }
        catch (Exception ex)
        {
            completion.SetResult(new([], false, $"無法啟動儲存可靠性讀取：{ex.GetType().Name}：{ex.Message}"));
        }
        if (!completion.Task.Wait(_budgetMs))
            return Failure($"WMI 儲存可靠性查詢逾時（{_budgetMs} 毫秒沒有回應）——底層查詢未取消，收集鏈繼續");

        var outcome = completion.Task.Result;
        Available = outcome.Available;
        UnavailableReason = outcome.Reason;
        return outcome.Rows;
    }

    private IReadOnlyList<StorageReliabilityRaw> Failure(string reason)
    {
        Available = false;
        UnavailableReason = reason;
        return [];
    }
}

/// <summary>可靠性查詢專用的程序內 provider 閘門；其他直接 WMI 路徑尚未接入。</summary>
internal static class StorageReliabilityWmiReadGate
{
    // 本機官方 storagewmi.mof:3-10、508/550/1104 的註冊身份；不是執行中的 provider PID。
    internal const string LocalStorageProvider =
        @"local|root\Microsoft\Windows\Storage|StorageWMI|{B4A6821D-4D2E-40DD-9EE0-89C2CD07BF69}";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, object> Active = new(StringComparer.OrdinalIgnoreCase);

    internal static IDisposable? TryEnter(string identity)
    {
        lock (Sync)
        {
            if (Active.ContainsKey(identity)) return null;
            var owner = new object();
            var lease = new Lease(identity, owner);
            Active.Add(identity, owner);
            return lease;
        }
    }

    private sealed class Lease(string identity, object owner) : IDisposable
    {
        private object? _owner = owner;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _owner, null);
            if (held is null) return;
            lock (Sync)
            {
                if (Active.TryGetValue(identity, out var current) && ReferenceEquals(held, current))
                    Active.Remove(identity);
            }
        }
    }
}

/// <summary>正式 COM 查詢共用的收尾：保留原讀取失敗，清理失敗有界彙總並停止後續查詢。</summary>
internal static class StorageReliabilityWmiCleanup
{
    internal const int MaximumCleanupDiagnostics = 7;
    internal const int MaximumDiagnosticCharacters = 4096;

    internal static T Run<T>(Func<Scope, T> read)
    {
        var scope = new Scope();
        T result = default!;
        Exception? primary = null;
        try { result = read(scope); }
        catch (CleanupInterruptedException) { /* 清理已記錄；不捏造另一個讀取失敗。 */ }
        catch (Exception ex) { primary = ex; }
        var reason = scope.FailureReason(primary);
        if (reason is not null) throw new InvalidOperationException(reason, primary);
        return result;
    }

    private sealed class CleanupInterruptedException : Exception { }

    internal sealed class Scope
    {
        private readonly List<string> _cleanupFailures = [];
        private int _cleanupFailureCount;

        internal T Use<TResource, T>(TResource resource, string stage, Func<TResource, T> read)
            where TResource : IDisposable
        {
            bool readFailed = false;
            try { return read(resource); }
            catch { readFailed = true; throw; }
            finally
            {
                try { resource.Dispose(); }
                catch (Exception ex)
                {
                    if (_cleanupFailureCount < int.MaxValue) _cleanupFailureCount++;
                    if (_cleanupFailures.Count < MaximumCleanupDiagnostics)
                        _cleanupFailures.Add(Describe("清理失敗 " + stage, ex));
                    // 原失敗仍在傳遞時絕不替換它；成功讀取後的清理失敗則中止新交易。
                    if (!readFailed) throw new CleanupInterruptedException();
                }
            }
        }

        internal string? FailureReason(Exception? primary)
        {
            if (primary is null && _cleanupFailureCount == 0) return null;
            var details = new List<string>();
            if (primary is not null) details.Add(Describe("原始讀取失敗", primary));
            details.AddRange(_cleanupFailures);
            if (_cleanupFailureCount > _cleanupFailures.Count)
                details.Add($"另有 {_cleanupFailureCount - _cleanupFailures.Count} 項清理失敗未列；已嘗試處置所有取得的資源");
            string reason = string.Join("；", details);
            return Limit(reason, MaximumDiagnosticCharacters);
        }

        private static string Describe(string stage, Exception error)
        {
            string message;
            try { message = error.Message; }
            catch { message = "例外訊息不可讀"; }
            return $"{Limit(stage, 64)}：{Limit(error.GetType().Name, 128)}：{Limit(message, 256)}";
        }

        private static string Limit(string value, int length)
            => value.Length <= length ? value : value[..(length - 1)] + "…";
    }
}

/// <summary>
/// 生產來源：<c>root\Microsoft\Windows\Storage</c> 的 <c>MSFT_StorageReliabilityCounter</c>。
/// </summary>
/// <remarks>
/// <b>刻意用 <c>GetRelated</c> 而不是自己組 <c>ASSOCIATORS OF</c> 的 WQL。</b>
/// 這個類別的實例靠關聯取得，而 <c>ObjectId</c> 內含反斜線與引號；
/// 自行組字串會踩到 WQL 跳脫（實測回 0x80041031 屬性無效），
/// 由 WMI 自己做關聯才不會因為一顆碟的 ObjectId 長相不同就整批失敗。
/// </remarks>
public sealed class StorageReliabilityWmiSource : IStorageReliabilitySource
{
    private const string Namespace = @"root\Microsoft\Windows\Storage";
    private readonly Func<IQuery> _openQuery;
    private readonly string _providerIdentity;

    // 此 seam 保留正式 Read／timeout wrapper／gate；測試只替換 COM 查詢與資源生命週期。
    internal interface IQuery : IDisposable
    {
        IReadOnlyList<StorageReliabilityRaw> Read();
    }

    public StorageReliabilityWmiSource()
        : this(static () => new WmiQuery(), StorageReliabilityWmiReadGate.LocalStorageProvider) { }

    internal StorageReliabilityWmiSource(Func<IQuery> openQuery,
        string providerIdentity = StorageReliabilityWmiReadGate.LocalStorageProvider)
    {
        ArgumentNullException.ThrowIfNull(openQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdentity);
        _openQuery = openQuery;
        _providerIdentity = providerIdentity;
    }

    public bool Available { get; private set; } = true;
    public string? UnavailableReason { get; private set; }

    public IReadOnlyList<StorageReliabilityRaw> Read()
    {
        // 所有正式 source 實例共用此註冊身份；不按物件、WQL 或類別分鎖。
        // lease 由執行 Read 的 worker 持有，呼叫端 timeout 不會釋放它。
        using var lease = StorageReliabilityWmiReadGate.TryEnter(_providerIdentity);
        if (lease is null)
        {
            Available = false;
            UnavailableReason = "本機 StorageWMI 的先前可靠性查詢或清理仍在執行——未取消底層操作，不再發出另一個查詢";
            return [];
        }

        try
        {
            IReadOnlyList<StorageReliabilityRaw>? rows = null;
            var failures = new List<string>();
            var query = _openQuery() ?? throw new InvalidOperationException("可靠性查詢違反契約：factory 回傳 null");
            try
            {
                rows = query.Read() ?? throw new InvalidOperationException("可靠性查詢違反契約：Read 回傳 null");
            }
            catch (Exception ex)
            {
                failures.Add($"讀取失敗：{ex.GetType().Name}：{ex.Message}");
            }
            finally
            {
                // 清理必須完成才離開 lease；另外捕獲，不讓 Dispose 覆蓋原始讀取失敗。
                try { query.Dispose(); }
                catch (Exception ex) { failures.Add($"清理失敗：{ex.GetType().Name}：{ex.Message}"); }
            }
            if (failures.Count != 0)
            {
                Available = false;
                UnavailableReason = string.Join("；", failures);
                return [];
            }
            Available = true;
            UnavailableReason = null;
            return rows!;
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason = $"{ex.GetType().Name}：{ex.Message}";
            return [];
        }
    }

    internal sealed class WmiQuery : IQuery
    {
        private readonly Func<StorageReliabilityWmiCleanup.Scope, IReadOnlyList<StorageReliabilityRaw>> _read;

        // 注入只替換 COM 操作；正式 Read 的有界收尾與外層 source/gate 保持相同。
        internal WmiQuery(Func<StorageReliabilityWmiCleanup.Scope, IReadOnlyList<StorageReliabilityRaw>>? read = null)
            => _read = read ?? ReadCom;

        public IReadOnlyList<StorageReliabilityRaw> Read() => StorageReliabilityWmiCleanup.Run(_read);

        private static IReadOnlyList<StorageReliabilityRaw> ReadCom(StorageReliabilityWmiCleanup.Scope cleanup)
            => cleanup.Use(new ManagementObjectSearcher(Namespace, "SELECT * FROM MSFT_PhysicalDisk"), "searcher",
                searcher => cleanup.Use(searcher.Get(), "disk collection",
                    disks => cleanup.Use(disks.GetEnumerator(), "disk enumerator", diskEnumerator =>
                    {
                        var rows = new List<StorageReliabilityRaw>();
                        while (diskEnumerator.MoveNext())
                        {
                            cleanup.Use((ManagementObject)diskEnumerator.Current, "disk",
                                disk => cleanup.Use(disk.GetRelated("MSFT_StorageReliabilityCounter"), "related collection",
                                    related => cleanup.Use(related.GetEnumerator(), "related enumerator", relEnumerator =>
                                    {
                                        while (relEnumerator.MoveNext())
                                            cleanup.Use((ManagementObject)relEnumerator.Current, "counter", rel =>
                                            {
                                                rows.Add(ReadOne(rel));
                                                return true;
                                            });
                                        return true;
                                    })));
                        }
                        return rows;
                    })));

        // ReadCom 已透過同一 cleanup scope 嘗試處置每個取得的 COM 資源與列舉器。
        public void Dispose() { }
    }

    private static StorageReliabilityRaw ReadOne(ManagementBaseObject o) => new(
        DeviceId: (int)(Num(o, "DeviceId") ?? -1),
        Wear: Num(o, "Wear"),
        Temperature: Num(o, "Temperature"),
        TemperatureMax: Num(o, "TemperatureMax"),
        PowerOnHours: Num(o, "PowerOnHours"),
        StartStopCycleCount: Num(o, "StartStopCycleCount"),
        StartStopCycleCountMax: Num(o, "StartStopCycleCountMax"),
        LoadUnloadCycleCount: Num(o, "LoadUnloadCycleCount"),
        LoadUnloadCycleCountMax: Num(o, "LoadUnloadCycleCountMax"),
        ReadErrorsTotal: Num(o, "ReadErrorsTotal"),
        ReadErrorsCorrected: Num(o, "ReadErrorsCorrected"),
        ReadErrorsUncorrected: Num(o, "ReadErrorsUncorrected"),
        WriteErrorsTotal: Num(o, "WriteErrorsTotal"),
        WriteErrorsCorrected: Num(o, "WriteErrorsCorrected"),
        WriteErrorsUncorrected: Num(o, "WriteErrorsUncorrected"),
        ReadLatencyMax: Num(o, "ReadLatencyMax"),
        WriteLatencyMax: Num(o, "WriteLatencyMax"),
        FlushLatencyMax: Num(o, "FlushLatencyMax"),
        ManufactureDate: o["ManufactureDate"] is string d && d.Length > 0 ? d : null);

    /// <summary>讀一個數值欄位；<b>空值回 null，不回 0</b>——空與 0 是兩件不同的事。</summary>
    private static long? Num(ManagementBaseObject o, string prop)
    {
        try
        {
            object? v = o[prop];
            if (v is null) return null;
            if (v is string s) return s.Length == 0 ? null : (long.TryParse(s, out long p) ? p : null);
            return Convert.ToInt64(v);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;   // 讀得到但解不開＝沒有值，不是 0
        }
    }
}

/// <summary>
/// 儲存裝置可靠性計數器事實（WMI <c>MSFT_StorageReliabilityCounter</c>）。
/// </summary>
/// <remarks>
/// 這是本專案原本沒讀的結構化來源：Windows 自己就有，不必呼叫 smartctl 一類的外部工具。
/// <para>
/// <b>每個欄位各自三態</b>：提供者有給就成一筆事實；沒給就整欄不出現，只在該碟的
/// 「收錄情形」那一筆裡被數出來並列出名稱——「沒提供」與「值是 0」絕不混為一談。
/// </para>
/// </remarks>
public static class StorageReliabilityFactsService
{
    private const string Category = "儲存裝置";

    /// <summary>WMI 類別路徑，逐筆事實的來源欄位用。</summary>
    public const string Source = @"WMI root\Microsoft\Windows\Storage:MSFT_StorageReliabilityCounter";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IStorageReliabilitySource? source = null)
    {
        source ??= new StorageReliabilityWmiSource();
        var facts = new List<HardwareFact>();

        // 先讀再判可用性：真實來源在查詢之前不知道 WMI 會不會失敗，它的 Available 是
        // 「剛剛那次讀取的結果」。若先檢查 Available（預設 true）就會在查詢失敗時
        // 走到下面那一支，把「失敗並帶著原因」講成「讀到了、只是沒有計數器——不是錯誤」。
        IReadOnlyList<StorageReliabilityRaw> rows;
        try
        {
            rows = source.Read();
        }
        catch (Exception ex)
        {
            // 來源自己把例外丟出來也一樣：那是一次讀取失敗，不是「沒有這回事」。
            // 真實來源會自行吞噬並回報，但接縫不該假設每個實作者都那麼做——
            // 這裡吞掉的話，畫面與 CLI 會看到一個空集合，而空集合讀起來像「沒問題」。
            facts.Add(new HardwareFact("storage.reliability.available", Category, "儲存可靠性計數器", "", "",
                Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, $"來源在讀取時擲出 {ex.GetType().Name}：{ex.Message}"));
            return facts;
        }

        if (!source.Available)
        {
            // 來源不可用時不是「沒有這回事」，而是一筆說得出原因的不可用事實。
            facts.Add(new HardwareFact("storage.reliability.available", Category, "儲存可靠性計數器", "", "",
                Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, source.UnavailableReason ?? "來源未提供原因"));
            return facts;
        }

        if (rows.Count == 0)
        {
            facts.Add(new HardwareFact("storage.reliability.available", Category, "儲存可靠性計數器", "", "",
                Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "讀到了來源，但它沒有回報任何計數器——不是錯誤，是本機沒有可用的提供者"));
            return facts;
        }

        facts.Add(new HardwareFact("storage.reliability.count", Category, "可靠性計數器涵蓋磁碟數",
            rows.Count.ToString(), "顆", Source, FactTrustLevel.Reported, false, at, rows.Count));

        foreach (var r in rows) facts.AddRange(ForDisk(at, r));
        return facts;
    }

    private static IEnumerable<HardwareFact> ForDisk(DateTimeOffset at, StorageReliabilityRaw r)
    {
        string disk = $"磁碟 {r.DeviceId}";
        var missing = new List<string>();

        foreach (var (key, name, value, unit) in Fields(r))
        {
            if (value is null) { missing.Add(name); continue; }
            yield return new HardwareFact($"storage.reliability.{r.DeviceId}.{key}", Category, $"{name}（{disk}）",
                value.Value.ToString(), unit, Source, FactTrustLevel.Reported, false, at, value.Value);
        }

        if (r.ManufactureDate is null) missing.Add("製造日期");
        else
            yield return new HardwareFact($"storage.reliability.{r.DeviceId}.manufacture_date", Category,
                $"製造日期（{disk}）", r.ManufactureDate, "", Source, FactTrustLevel.Reported, false, at, null);

        // 收錄情形：把「提供者沒給的欄位」數出來並具名列出。缺不是錯，但不能無聲無息。
        int supplied = Fields(r).Count(f => f.Value is not null) + (r.ManufactureDate is null ? 0 : 1);
        int total = Fields(r).Count() + 1;
        yield return new HardwareFact($"storage.reliability.{r.DeviceId}.coverage", Category,
            $"計數器收錄情形（{disk}）",
            missing.Count == 0 ? $"{supplied}/{total}（提供者全數提供）" : $"{supplied}/{total}；未提供：{string.Join("、", missing)}",
            "", Source, FactTrustLevel.Derived, false, at, supplied,
            missing.Count == 0 ? FactAvailability.Present : FactAvailability.NotApplicable,
            missing.Count == 0 ? null : "這些欄位不是讀取失敗，是提供者沒有提供——空值不代表 0");
    }

    /// <summary>逐欄位的（鍵後綴, 顯示名, 值, 單位）。單位以 WMI 類別的定義為準。</summary>
    private static IEnumerable<(string Key, string Name, long? Value, string Unit)> Fields(StorageReliabilityRaw r)
    {
        yield return ("wear", "磨損程度", r.Wear, "%");
        yield return ("temperature", "溫度", r.Temperature, "°C");
        yield return ("temperature_max", "溫度上限", r.TemperatureMax, "°C");
        yield return ("power_on_hours", "通電時數", r.PowerOnHours, "小時");
        yield return ("start_stop", "啟停次數", r.StartStopCycleCount, "次");
        yield return ("start_stop_max", "啟停次數上限", r.StartStopCycleCountMax, "次");
        yield return ("load_unload", "讀寫頭載卸次數", r.LoadUnloadCycleCount, "次");
        yield return ("load_unload_max", "讀寫頭載卸次數上限", r.LoadUnloadCycleCountMax, "次");
        yield return ("read_errors_total", "讀取錯誤總數", r.ReadErrorsTotal, "次");
        yield return ("read_errors_corrected", "讀取錯誤（已修正）", r.ReadErrorsCorrected, "次");
        yield return ("read_errors_uncorrected", "讀取錯誤（未修正）", r.ReadErrorsUncorrected, "次");
        yield return ("write_errors_total", "寫入錯誤總數", r.WriteErrorsTotal, "次");
        yield return ("write_errors_corrected", "寫入錯誤（已修正）", r.WriteErrorsCorrected, "次");
        yield return ("write_errors_uncorrected", "寫入錯誤（未修正）", r.WriteErrorsUncorrected, "次");
        yield return ("read_latency_max", "最大讀取延遲", r.ReadLatencyMax, "ms");
        yield return ("write_latency_max", "最大寫入延遲", r.WriteLatencyMax, "ms");
        yield return ("flush_latency_max", "最大清除延遲", r.FlushLatencyMax, "ms");
    }
}
