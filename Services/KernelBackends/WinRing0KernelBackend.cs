namespace XinSpect;

/// <summary>
/// WinRing0 後端：既有 <see cref="WinRing0Bridge"/> 的 <see cref="IKernelBackend"/> 適配器。
/// 橋接本身（隔離 ALC、引用計數會話、選用方法組）不動——適配器只負責把它的形狀
/// 翻譯成後端介面，並把信任等級如實標為 <see cref="KernelTrustTier.Universal"/>。
/// </summary>
/// <remarks>
/// <b>風險申報（沿用橋接頭註並抬高到後端層）：</b>WinRing0 裝置無呼叫者校驗——驅動載入後
/// 到重開機前，同機其他程序理論上也能透過它存取 MSR。介面在選擇此後端時必須向使用者
/// 展示這一行；<see cref="KernelBackendSelector"/> 的模式開關是使用者說了算的地方。
/// </remarks>
/// <remarks>
/// <b>暴露窗口的設計回歸（明文化）：</b>現行呼叫點是「按次 Create→使用→Dispose」，驅動
/// 暴露窗口以秒計；本後端為了路由快取與會話穩定，**讓橋接會話存活到鏈被 Dispose 為止**——
/// 暴露窗口從「秒」變成「程式啟用鏈之後的全部時間」。選 <see cref="KernelBackendMode.UniversalAllowed"/>
/// 等於同意這個窗口延長；<see cref="KernelBackendMode.SandboxOnly"/> 完全不受影響。
/// 未來可加閒置自動卸載（秒級無呼叫後釋放會話、下次呼叫再 Create）——代價是熱路徑多一次
/// 驅動載入延遲，等實測數字出來再決定。
/// </remarks>
public sealed class WinRing0KernelBackend : IKernelBackend
{
    private readonly WinRing0Bridge _bridge;

    public string Id => "winring0";
    public string DisplayName => "WinRing0（萬能鑰匙）";
    public KernelTrustTier Tier => KernelTrustTier.Universal;
    public string? UnavailableReason => _bridge.Available ? null : _bridge.Error;

    /// <summary>能力按橋接實際載到的選用方法組申報——沒有就不報，與橋接既有行為一致。</summary>
    public KernelCapability Capabilities
    {
        get
        {
            if (!_bridge.Available) return KernelCapability.None;
            var c = KernelCapability.MsrRead | KernelCapability.MsrWrite;
            if (_bridge.PciAvailable) c |= KernelCapability.PciRead;
            if (_bridge.PciWriteAvailable) c |= KernelCapability.PciWrite;
            if (_bridge.IoPortAvailable) c |= KernelCapability.IoPortRead | KernelCapability.IoPortWrite;
            if (_bridge.MemoryReadAvailable) c |= KernelCapability.MmioRead;
            return c;
        }
    }

    private WinRing0KernelBackend(WinRing0Bridge bridge) => _bridge = bridge;

    /// <summary>取得橋接會話（引用計數；第一位使用者才真正載入驅動）。失敗回 null 帶原因。</summary>
    public static WinRing0KernelBackend? Create() => Create(out _);

    /// <summary>同上，但把橋接的失敗原因帶出（宿主缺席申報用）。成功時 error 為空字串或殘留訊息，以回傳值為準。</summary>
    public static WinRing0KernelBackend? Create(out string error)
    {
        var bridge = WinRing0Bridge.Create();
        error = bridge.Error;
        return bridge.Available ? new WinRing0KernelBackend(bridge) : null;
    }

    public ulong? ReadMsr(uint index) => _bridge.ReadMsrPair64(index);

    public KernelWriteOutcome WriteMsr(uint index, ulong value, string caller)
    {
        uint eax = (uint)(value & 0xFFFFFFFF), edx = (uint)(value >> 32);
        bool ok = _bridge.WriteMsrPair(index, eax, edx);
        WriteGate.Record($"MSR 0x{index:X}", caller, $"值 0x{value:X}", ok);
        return new KernelWriteOutcome(ok, ok ? null : "WinRing0 WRMSR 回失敗（HVCI／平台拒絕或權限不足）。");
    }

    public uint? ReadPciConfig(byte bus, byte device, byte function, uint register) =>
        _bridge.ReadPciConfig(bus, device, function, register);

    public KernelWriteOutcome WritePciConfig(byte bus, byte device, byte function, uint register, uint value, string caller)
    {
        bool ok = _bridge.WritePciConfig(bus, device, function, register, value);
        WriteGate.Record($"PCI {bus}:{device}.{function}+0x{register:X}", caller, $"值 0x{value:X}", ok);
        return new KernelWriteOutcome(ok, ok ? null : "WinRing0 PCI 寫入回失敗。");
    }

    public byte? ReadIoPortByte(uint port) => _bridge.ReadIoPortByte(port);

    public KernelWriteOutcome WriteIoPortByte(uint port, byte value, string caller)
    {
        bool ok = _bridge.WriteIoPortByte(port, value);
        WriteGate.Record($"IO 0x{port:X}", caller, $"值 0x{value:X2}", ok);
        return new KernelWriteOutcome(ok, ok ? null : "WinRing0 I/O 埠寫入回失敗。");
    }

    public byte[]? ReadMemoryBlock(ulong address, int length) => _bridge.ReadMemoryBlock(address, length);

    public void Dispose() => _bridge.Dispose();
}
