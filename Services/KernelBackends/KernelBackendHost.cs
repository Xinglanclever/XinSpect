namespace XinSpect;

/// <summary>
/// 核心後端宿主：進程級單例，把 <see cref="KernelBackends"/> 四檔接進呼叫鏈的最小樞紐。
/// <list type="number">
/// <item><b>懶初始化</b>：第一次 <see cref="Route"/>／<see cref="Snapshot"/> 才建後端鏈——
/// 載不載 WinRing0 驅動由「有沒有人真的要讀」決定，不是程式一開機就載。</item>
/// <item><b>模式先於載入</b>：<see cref="KernelBackendMode.SandboxOnly"/> 下連
/// <see cref="WinRing0KernelBackend.Create"/> 都不做——「不開萬能鑰匙」的第一步是
/// 「不載驅動」，而不是「載了不給用」。</item>
/// <item><b>缺席也申報</b>：PawnIO 本版整體缺席（ModulePins 未成文）、WinRing0 建立失敗、
/// 被模式排除——三種「不在鏈上」都以 <see cref="Snapshot"/> 一列如實帶原因，
/// 驅動就緒卡與 backend.* 事實直接渲染，介面能回答「為什麼這格走這條路」。</item>
/// </list>
/// <para>
/// <b>界線（誠實申報）：</b>本宿主只是接線樞紐，不是全倉閘門——在補丁批次遷移完成前，
/// 倉庫裡還有 20+ 個呼叫端直接 <see cref="WinRing0Bridge.Create"/>，它們不經過這裡、
/// 也不受模式約束（見補丁文件「3. 行為會變什麼」第 3 條）。在那一刻之前，
/// <see cref="KernelBackendMode.SandboxOnly"/> 是<b>路由聲明</b>，不是硬保證。
/// </para>
/// </summary>
public static class KernelBackendHost
{
    private static readonly object Gate = new();

    private static KernelBackendSelector? _selector;
    private static readonly List<(KernelBackendDescriptor Desc, KernelCapability Caps, string? Reason)> _declared = [];
    private static KernelBackendMode _mode = KernelBackendMode.Auto;
    private static bool _initialized;

    /// <summary>由 <see cref="SettingsService"/> 帶入（設定服務是實例制，宿主不反向持有它）。</summary>
    public static void ApplyMode(KernelBackendMode mode)
    {
        lock (Gate)
        {
            if (_initialized && _mode == mode) return;
            _mode = mode;
            _selector?.Dispose();     // 引用計數歸零；驅動服務本身留到重開機（WinRing0Bridge 既有語義）
            _selector = null;
            _declared.Clear();
            _initialized = false;     // 下一次 Route／Snapshot 依新模式重建
        }
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

        // ① PawnIO（沙箱）：本版缺席申報——ModulePins 未成文、Native 未綁定，恆回 None。
        //    仍放進 candidates（選擇器自行過濾能力 None 者）；缺席原因由 _declared 帶出。
        var pawn = new PawnIoBackend();
        _declared.Add((new KernelBackendDescriptor(pawn.Id, pawn.DisplayName, pawn.Tier),
                       pawn.Capabilities, pawn.UnavailableReason));
        candidates.Add(pawn);

        // ② WinRing0（萬能鑰匙）：SandboxOnly 模式下不建立——驅動根本不載。
        if (_mode == KernelBackendMode.SandboxOnly)
        {
            _declared.Add((new KernelBackendDescriptor("winring0", "WinRing0（萬能鑰匙）", KernelTrustTier.Universal),
                           KernelCapability.None, "被「僅沙箱」模式排除——本行程未載入驅動。"));
        }
        else
        {
            var w0 = WinRing0KernelBackend.Create(out var createError);
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
}
