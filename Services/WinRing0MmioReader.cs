namespace XinSpect;

/// <summary>
/// WinRing0 實體記憶體後端的 <see cref="IMmioReader"/>（V7 §2 驅動裁決：WinRing0 為主力）。
/// 底層是 LHM 0.9.4 內嵌 WinRing0 驅動的 ReadMemory（IOCTL_OLS_READ_MEMORY：位址＋UnitSize＋Count），
/// 能讀任意實體位址——與 XsRegProbe 的白名單路徑相比能力最寬、防護最薄。
/// 讀不到回 null 由上層標三態，絕不以 0／0xFF 頂替。
/// </summary>
/// <remarks>
/// ⚠ 防線在呼叫方：各服務的位址一律來自 PCI BAR／MCFG／MSR 對帳的真實來源，不自造位址、
/// 不做任意掃描。位址正確但該範圍未映射時驅動會回失敗——那也是誠實的讀不到，照實上拋。
/// </remarks>
public sealed class WinRing0MmioReader : IMmioReader, IDisposable
{
    private readonly WinRing0Bridge _bridge;
    private string? _lastFailReason;

    public WinRing0MmioReader() : this(WinRing0Bridge.Create()) { }

    /// <summary>注入既有橋接（測試／共用會話用）；取得所有權，Dispose 時交還。</summary>
    public WinRing0MmioReader(WinRing0Bridge bridge) => _bridge = bridge;

    public bool Available => _bridge.MemoryReadAvailable;

    public string? UnavailableReason =>
        !_bridge.Available ? _bridge.Error
        : !_bridge.MemoryReadAvailable ? "WinRing0 驅動未提供實體記憶體讀取（Ring0.ReadMemory 缺少）"
        : null;

    public string? LastFailReason => _lastFailReason;

    public byte[]? ReadBlock(ulong physicalAddress, int length)
    {
        _lastFailReason = null;
        if (!Available)
        {
            _lastFailReason = UnavailableReason;
            return null;
        }
        if (length <= 0)
        {
            _lastFailReason = $"長度 {length} 不合法（須為正）";
            return null;
        }
        var data = _bridge.ReadMemoryBlock(physicalAddress, length);
        if (data is null)
        {
            _lastFailReason = $"讀取實體記憶體 0x{physicalAddress:X}+{length} 失敗（驅動回報映射或複製失敗——位址未映射或不允許）";
            return null;
        }
        return data;
    }

    public void Dispose() => _bridge.Dispose();
}
