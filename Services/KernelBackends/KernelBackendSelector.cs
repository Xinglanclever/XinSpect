namespace XinSpect;

/// <summary>後端選擇模式（存進 settings.json 的 <c>KernelBackendMode</c>；整數只追加不調換）。</summary>
public enum KernelBackendMode
{
    /// <summary>自動：沙箱優先，通不過自證才落到萬能鑰匙；每項事實照實記錄服務後端。</summary>
    Auto = 0,
    /// <summary>僅沙箱：只接受 <see cref="KernelTrustTier.Sandboxed"/>——MSR/MMIO 讀不到就三態，不開萬能鑰匙。</summary>
    SandboxOnly = 1,
    /// <summary>允許萬能鑰匙：明示同意 WinRing0 的暴露面（介面在選此項時展示風險行）。</summary>
    UniversalAllowed = 2,
}

/// <summary>
/// 後端鏈：把「三枚」（PawnIO 沙箱／WinRing0 萬能鑰匙／未來自研原生驅動）排成一條
/// 可切換的鏈，按<b>能力</b>逐項找路，而不是選一個後端包辦一切。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼按能力不按後端。</b>三枚的能力圈不重疊也不包含：PawnIO 只有白名單內的讀；
/// WinRing0 全能但高危；自研驅動介於其間。一個「讀 MSR 走 PawnIO、寫 RDT 計數器走
/// WinRing0」的組合既合法又比單選任何一枚都好——所以選擇器的單位是「這一次呼叫，
/// 哪個後端有這個能力且被允許」。
/// </para>
/// <para>
/// <b>切換的語義。</b>模式只約束<b>信任層級</b>，不指定具體後端：<see cref="KernelBackendMode.SandboxOnly"/>
/// 擋掉 Universal 與 Native；<see cref="KernelBackendMode.UniversalAllowed"/> 全開但仍優先沙箱
/// （優先序是政策不是使用者偏好——沙箱通過自證時，萬能鑰匙只兜沙箱沒有的能力）。
/// 每次解析都帶原因；被模式擋掉的後端寫進 <see cref="KernelRoute"/>，介面能回答
/// 「為什麼這一格走的是這條路」。
/// </para>
/// </remarks>
public sealed class KernelBackendSelector : IDisposable
{
    private readonly List<IKernelBackend> _chain;
    private readonly KernelBackendMode _mode;

    /// <summary>
    /// 路由快取：同一能力在後端能力集不變時，選路結果不會變——逐呼叫重建解釋字串
    /// 在「感測器輪詢 × 逐核 MSR 掃描」的熱路徑上是純分配垃圾。快取按能力鍵存；
    /// <b>快取的是「後端有能力」這個前提</b>，不是「呼叫會成功」——呼叫失敗仍由呼叫端三態處理。
    /// </summary>
    private readonly Dictionary<KernelCapability, KernelRoute> _routeCache = [];

    public KernelBackendSelector(KernelBackendMode mode, IEnumerable<IKernelBackend> candidates)
    {
        _mode = mode;
        _chain = candidates
            .Where(b => b.Capabilities != KernelCapability.None)
            .OrderBy(b => (int)b.Tier)          // Sandboxed → Universal → Native
            .ToList();
    }

    /// <summary>一次解析的結果：用哪個後端、為什麼別的不行——介面與事實鍵直接渲染。</summary>
    public sealed record KernelRoute(IKernelBackend? Backend, string Explanation, IReadOnlyList<string> Rejected);

    /// <summary>為某項能力找後端（帶快取）。找不到回 null，但 <see cref="KernelRoute"/> 說清每一枚被排除的原因。</summary>
    public KernelRoute Route(KernelCapability need)
    {
        if (_routeCache.TryGetValue(need, out var cached))
            return cached;

        var route = Resolve(need);
        _routeCache[need] = route;
        return route;
    }

    private KernelRoute Resolve(KernelCapability need)
    {
        var rejected = new List<string>();
        foreach (var b in _chain)
        {
            if (!b.Capabilities.HasFlag(need))
            {
                rejected.Add($"{b.DisplayName}：無此能力（{b.UnavailableReason ?? "未申報"}）");
                continue;
            }
            if (_mode == KernelBackendMode.SandboxOnly && b.Tier != KernelTrustTier.Sandboxed)
            {
                rejected.Add($"{b.DisplayName}：被「僅沙箱」模式排除");
                continue;
            }
            var others = rejected.Count == 0 ? "無前序排除" : string.Join("；", rejected);
            return new KernelRoute(b, $"{need} → {b.DisplayName}（{b.Tier}；{others}）", rejected);
        }
        return new KernelRoute(null,
            $"{need} 無可用後端（模式：{_mode}）。" + (rejected.Count > 0 ? " " + string.Join("；", rejected) : ""),
            rejected);
    }

    /// <summary>
    /// 後端能力集可能已變化（HVCI 開關、驅動載入成敗、PawnIO 自證歸隊）時清快取。
    /// 呼叫端在收到「有快取路徑但讀回 null」時也應呼叫——三態的 null 不分「無路」與「路斷了」，
    /// 寧可多選一次路，也不要讓過期的路徑假設頂替現況（「舊資料不得冒充現況」同一哲學）。
    /// </summary>
    public void InvalidateRoutes() => _routeCache.Clear();

    /// <summary>讀 MSR 的快捷（走快取路由）。null＝讀不到，呼叫端可先 <see cref="InvalidateRoutes"/> 再試一次。</summary>
    public ulong? ReadMsr(uint index)
    {
        var route = Route(KernelCapability.MsrRead);
        var v = route.Backend?.ReadMsr(index);
        if (v is null && route.Backend is not null) InvalidateRoutes();   // 路在但斷了：下回合重選
        return v;
    }

    /// <summary>寫 MSR 的快捷。同意閘門在服務層；此處只保證：經萬能鑰匙寫入一定有帳。</summary>
    public KernelWriteOutcome WriteMsr(uint index, ulong value, string caller)
    {
        var route = Route(KernelCapability.MsrWrite);
        if (route.Backend is not { } b)
            return new KernelWriteOutcome(false, route.Explanation);
        return b.WriteMsr(index, value, caller);
    }

    /// <summary>診斷快照：三枚各自的健康與能力（驅動就緒卡／backend.* 事實鍵的資料源）。</summary>
    public IReadOnlyList<(KernelBackendDescriptor Desc, KernelCapability Caps, string? Reason)> Snapshot() =>
        _chain.Select(b => (new KernelBackendDescriptor(b.Id, b.DisplayName, b.Tier), b.Capabilities, b.UnavailableReason))
              .ToList();

    public void Dispose()
    {
        foreach (var b in _chain) b.Dispose();
        _chain.Clear();
    }
}
