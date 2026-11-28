namespace XinSpect;

/// <summary>
/// 核心後端宿主：進程級單例，把 <see cref="KernelBackends"/> 四檔接進呼叫鏈的最小樞紐。
/// <list type="number">
/// <item><b>懶初始化</b>：第一次 <see cref="Route"/>／<see cref="Snapshot"/> 才建後端鏈——
/// 載不載 WinRing0 驅動由「有沒有人真的要讀」決定，不是程式一開機就載。</item>
/// <item><b>模式先於載入</b>：只有 <see cref="KernelBackendMode.UniversalAllowed"/> 才呼叫
/// <see cref="WinRing0KernelBackend.Create"/>。Auto 與 SandboxOnly 都不載入 WinRing0，
/// 避免把預設路由當成對通用核心驅動的同意。</item>
/// <item><b>缺席也申報</b>：PawnIO 未安裝或試讀失敗、WinRing0 建立失敗、
/// 被模式排除——三種「不在鏈上」都以 <see cref="Snapshot"/> 一列如實帶原因，
/// 驅動就緒卡與 backend.* 事實直接渲染，介面能回答「為什麼這格走這條路」。</item>
/// </list>
/// <para>
/// <b>界線（誠實申報）：</b>本宿主只是局部路由，不是全倉閘門；仍有多個生產呼叫端直接建立
/// <see cref="WinRing0Bridge"/>，不經過此處，也不受本模式約束。
/// Auto／SandboxOnly 只保證本宿主不建立 WinRing0，不代表整個程式不會由其他路徑載入它。
/// </para>
/// </summary>
public static class KernelBackendHost
{
    private static readonly object Gate = new();

    private static KernelBackendSelector? _selector;
    private static readonly List<(KernelBackendDescriptor Desc, KernelCapability Caps, string? Reason)> _declared = [];
    private static KernelBackendMode _mode = KernelBackendMode.Auto;
    private static bool _initialized;
    private static Func<(IKernelBackend? Backend, string Error)> _winRing0Factory = CreateWinRing0Backend;
    private static Func<PawnIoBackend> _pawnIoFactory = PawnIoBackend.Create;

    /// <summary>由 <see cref="SettingsService"/> 帶入（設定服務是實例制，宿主不反向持有它）。
    /// N2：模式切換<b>退役</b>舊選擇器而非就地 Dispose——鎖外可能還有 in-flight 的 guard
    /// 正拿著舊後端鏈讀值；退役延後到最後一個在途者離場（見 KernelBackendSelector.Retire）。</summary>
    public static void ApplyMode(KernelBackendMode mode)
    {
        KernelBackendSelector? retired = null;
        lock (Gate)
        {
            if (_initialized && _mode == mode) return;
            _mode = mode;
            retired = _selector;      // N2：先退役、不立即 Dispose
            _selector = null;
            _declared.Clear();
            _initialized = false;     // 下一次 Route／RouteUse／Snapshot 依新模式重建
        }
        retired?.Retire();            // 無在途者即刻釋放；有在途者由最後一個 guard 觸發
    }

    /// <summary>為某項能力找後端。第一次呼叫觸發懶初始化（此時才可能載驅動）。</summary>
    public static KernelBackendSelector.KernelRoute Route(KernelCapability need)
    {
        lock (Gate)
        {
            Ensure();
            return _selector!.Route(need);
        }
    }

    /// <summary>
    /// N2：取一條路徑並登記在途。鎖外要<b>使用</b>後端（ReadMsr 等）的呼叫端一律走本入口：
    /// <c>using var use = KernelBackendHost.RouteUse(cap);</c>——guard 未釋放前，模式切換
    /// 不會釋放後端鏈，驅動句柄不會在讀取中被關。<see cref="Route"/> 保留給「只看路由、
    /// 不在鎖外使用後端」的呼叫端。
    /// </summary>
    public static KernelBackendSelector.KernelRouteUse RouteUse(KernelCapability need)
    {
        lock (Gate)
        {
            Ensure();
            return _selector!.RouteUse(need);   // 在途計數在鎖內登記，歸還（guard.Dispose）在鎖外——兩者皆 Interlocked 安全
        }
    }

    /// <summary>
    /// Borrow only an already-initialized MSR route. This never calls Ensure, any
    /// backend factory, a driver loader, or a probe. The lease pins the existing
    /// selector's lifetime and refuses further reads after policy/mode revocation.
    /// </summary>
    internal static ICpuPackagePowerDiagnosticReadLease? TryAcquireExistingPowerReadLease(out string reason)
    {
        lock (Gate)
        {
            if (!HardwareOperationGate.CanRunAdvancedDiagnostics)
            {
                reason = "第一級進階診斷未開啟；未讀取、未初始化後端。";
                return null;
            }
            if (!_initialized || _selector is null)
            {
                reason = "共用 MSR 後端尚未開啟；本診斷不載入驅動或初始化通道。";
                return null;
            }
            var owner = _selector;
            var use = owner.RouteUse(KernelCapability.MsrRead);
            if (use.Route.Backend is not { } backend)
            {
                reason = use.Route.Explanation;
                use.Dispose();
                return null;
            }
            reason = "借用既有 MSR 通道；不建立其他後端。";
            try { return new ExistingPowerReadLease(owner, use, backend, _mode); }
            catch { use.Dispose(); throw; }
        }
    }

    private sealed class ExistingPowerReadLease : ICpuPackagePowerDiagnosticReadLease
    {
        private readonly KernelBackendSelector _owner;
        private readonly IKernelBackend _backend;
        private KernelBackendSelector.KernelRouteUse? _use;
        private int _activeReads;
        private bool _disposeRequested;
        public CpuPackagePowerDiagnosticSource Source { get; }

        internal ExistingPowerReadLease(KernelBackendSelector owner,
            KernelBackendSelector.KernelRouteUse use, IKernelBackend backend, KernelBackendMode mode)
        {
            _owner = owner; _use = use; _backend = backend;
            Source = new(backend.Id, backend.DisplayName, backend.Tier.ToString(), mode.ToString(),
                Guid.NewGuid().ToString("D"), typeof(KernelBackendHost).Assembly.GetName().Version?.ToString() ?? "unknown",
                backend.Id == "pawnio" ? typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version?.ToString()
                    : "Embedded LHM 0.9.4 bridge; active module hash unavailable",
                backend.Id == "pawnio" ? typeof(PawnIoBackend).GetField("ModuleSha256",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetRawConstantValue() as string : null);
        }

        public bool IsCurrent
        {
            get
            {
                lock (Gate)
                    return !_disposeRequested && _use is not null && _initialized && ReferenceEquals(_selector, _owner)
                        && HardwareOperationGate.CanRunAdvancedDiagnostics;
            }
        }

        public ulong? Read(uint index)
        {
            if (index is not (0x606 or 0x611))
                throw new ArgumentOutOfRangeException(nameof(index), "被動功耗診斷只允許 MSR 0x606／0x611。");
            lock (Gate)
            {
                if (!IsCurrent) throw new OperationCanceledException("既有通道或第一級診斷政策已變更；停止讀取。");
                if (_activeReads != 0) throw new InvalidOperationException("此診斷 lease 只允許一個在途讀取。");
                _activeReads++;
            }
            try
            {
                // Retain the lifetime reference, not the host lock, during I/O.
                // Mode retirement remains responsive while the old backend stays
                // alive until this operation really returns.
                ulong? value = _backend.ReadMsr(index);
                // The diagnostic brackets and rechecks this return, retaining a
                // revoked read only as raw failed evidence, never as a power endpoint.
                return value;
            }
            finally
            {
                KernelBackendSelector.KernelRouteUse? release = null;
                lock (Gate)
                {
                    _activeReads--;
                    if (_disposeRequested) { release = _use; _use = null; }
                }
                release?.Dispose();
            }
        }

        public void Dispose()
        {
            KernelBackendSelector.KernelRouteUse? use;
            lock (Gate)
            {
                _disposeRequested = true;
                use = _activeReads == 0 ? _use : null;
                if (_activeReads == 0) _use = null;
            }
            use?.Dispose();
        }
    }

    /// <summary>
    /// 診斷快照：每一枚後端的健康與能力（含缺席者與被模式排除者）。
    /// 驅動就緒卡與 <c>backend.*</c> 事實鍵的資料源。
    /// </summary>
    public static IReadOnlyList<(KernelBackendDescriptor Desc, KernelCapability Caps, string? Reason)> Snapshot()
    {
        lock (Gate)
        {
            Ensure();
            return _declared.ToArray();
        }
    }

    private static void Ensure()
    {
        if (_initialized) return;
        _declared.Clear();
        var candidates = new List<IKernelBackend>();

        // ① 既有 PawnIO：白名單 MSR 試讀通過後才納入路由。
        var pawn = _pawnIoFactory();
        _declared.Add((new KernelBackendDescriptor(pawn.Id, pawn.DisplayName, pawn.Tier),
                       pawn.Capabilities, pawn.UnavailableReason));
        candidates.Add(pawn);

        // ② WinRing0（萬能鑰匙）：只有明確選 UniversalAllowed 才建立——預設／僅沙箱都不載。
        if (!ShouldLoadWinRing0(_mode))
        {
            _declared.Add((new KernelBackendDescriptor("winring0", "WinRing0（萬能鑰匙）", KernelTrustTier.Universal),
                           KernelCapability.None, _mode == KernelBackendMode.Auto
                               ? "被「自動」模式的安全策略排除——未明確允許 WinRing0，本路由未載入驅動。"
                               : "被「僅沙箱」模式排除——本路由未載入驅動。"));
        }
        else
        {
            var (w0, createError) = _winRing0Factory();
            if (w0 is { } b)
            {
                _declared.Add((new KernelBackendDescriptor(b.Id, b.DisplayName, b.Tier),
                               b.Capabilities, b.UnavailableReason));
                candidates.Add(b);
            }
            else
            {
                _declared.Add((new KernelBackendDescriptor("winring0", "WinRing0（萬能鑰匙）", KernelTrustTier.Universal),
                               KernelCapability.None, "驅動未載入：" + createError));
            }
        }

        _selector = new KernelBackendSelector(_mode, candidates);
        _initialized = true;
    }

    /// <summary>WinRing0 具有較廣核心存取暴露面；只允許明確選 UniversalAllowed 後載入。</summary>
    internal static bool ShouldLoadWinRing0(KernelBackendMode mode) =>
        mode == KernelBackendMode.UniversalAllowed;

    /// <summary>測試注入點：避免矩陣測試載入真實核心驅動；傳 null 還原正式工廠。</summary>
    internal static void SetFactoriesForTests(Func<(IKernelBackend? Backend, string Error)>? winRing0,
        Func<PawnIoBackend>? pawnIo = null)
    {
        KernelBackendSelector? retired;
        lock (Gate)
        {
            _winRing0Factory = winRing0 ?? CreateWinRing0Backend;
            _pawnIoFactory = pawnIo ?? PawnIoBackend.Create;
            retired = _selector;
            _selector = null;
            _declared.Clear();
            _initialized = false;
        }
        retired?.Retire();
    }

    private static (IKernelBackend? Backend, string Error) CreateWinRing0Backend()
    {
        var backend = WinRing0KernelBackend.Create(out var error);
        return (backend, error);
    }
}
