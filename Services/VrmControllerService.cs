using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// VRM 供電控制器的 PMBus 自動偵測與 LLC 讀寫。
/// </summary>
/// <remarks>
/// <para>
/// <b>安全邊界：只碰 0x40–0x4F，而且寫之前一定先讀、寫之後一定讀回驗證。</b>
/// 0x40–0x4F 是 PMBus VRM 控制器的標準位址範圍，與 SPD EEPROM（0x50–0x57）不重疊。
/// </para>
/// <para>
/// <b>所有操作都走 <see cref="SmbusBusLock"/> 軟體互斥鎖＋硬體 INUSE_STS 旗號，兩層都到齊。</b>
/// 軟體層擋同機的 CPU-Z／AIDA64／HWiNFO；硬體層擋 BIOS／SMM。
/// </para>
/// <para>
/// ⚠ LLC 寫入是<b>真實寫入 VRM 控制器的暫存器</b>。設錯可能導致 CPU 供電不穩、當機、
/// 甚至損壞硬體。呼叫端必須有明確的風險確認。
/// </para>
/// </remarks>
public sealed class VrmControllerService : ObservableObject, IDisposable
{
    // ── i801 SMBus 暫存器位移 ──────────────────────────────────────────────
    private const uint HstSts = 0, HstCnt = 2, HstCmd = 3, XmitSlva = 4, HstD0 = 5, HstD1 = 6;
    private const byte StsHostBusy = 0x01, StsIntr = 0x02, StsDevErr = 0x04,
                       StsBusErr = 0x08, StsFailed = 0x10, StsInUse = 0x40;
    private const byte StsErrorMask = StsDevErr | StsBusErr | StsFailed;
    private const byte StsClearMask = 0xBE;
    private const byte CntStart = 0x40, CntKill = 0x02;
    private const byte ProtoByteData = 0x02 << 2;
    private const byte ProtoWordData = 0x03 << 2;

    // ── PMBus 命令碼 ──────────────────────────────────────────────────────
    internal const byte PmbusPage = 0x00;
    internal const byte PmbusIcDeviceId = 0xFD;
    internal const byte PmbusDeviceId = 0xAD;

    // ── VRM 位址範圍 ──────────────────────────────────────────────────────
    internal const byte VrmAddressMin = 0x40, VrmAddressMax = 0x4F;

    /// <summary>VRM 位址白名單：只允許 0x40–0x4F。</summary>
    internal static bool IsVrmAddress(byte slave7) => slave7 >= VrmAddressMin && slave7 <= VrmAddressMax;

    // ── 已知晶片資料庫 ────────────────────────────────────────────────────
    // IC_DEVICE_ID（Word Data 讀取的 16 位值）→ 晶片資訊模板
    internal static readonly IReadOnlyDictionary<ushort, (string Name, byte LlcReg, int MaxLlc, VrmVendorFamily Family)> KnownChipsByDeviceId
        = new Dictionary<ushort, (string, byte, int, VrmVendorFamily)>
        {
            [0x6938] = ("ISL69138",    0xD2, 7, VrmVendorFamily.Renesas),
            [0x6927] = ("ISL69269",    0xD2, 7, VrmVendorFamily.Renesas),
            [0x2856] = ("MP2856",      0x30, 7, VrmVendorFamily.Mps),
            [0x2857] = ("MP2857",      0x30, 7, VrmVendorFamily.Mps),
            [0x132D] = ("XDPE132G5C", 0xD1, 7, VrmVendorFamily.Infineon),
        };

    // 暫存器指紋：用 LLC 暫存器位址辨識家族（IC_DEVICE_ID 讀不到時的後備）
    internal static readonly IReadOnlyList<(byte LlcReg, string FamilyName, int MaxLlc, VrmVendorFamily Family)> FingerprintRules =
    [
        (0xD2, "Renesas ISL69xxx", 7, VrmVendorFamily.Renesas),
        (0xD1, "Infineon XDPExxxx", 7, VrmVendorFamily.Infineon),
        (0x30, "MPS MP285x", 7, VrmVendorFamily.Mps),
    ];

    private readonly ISmbusIo _io;
    private readonly uint _ioBase;
    private readonly int _timeoutMs;
    private readonly WinRing0Bridge? _ownedBridge;

    // ── 可觀察屬性 ────────────────────────────────────────────────────────
    private VrmDetectionResult? _detection;
    public VrmDetectionResult? DetectionResult
    {
        get => _detection;
        private set => SetProperty(ref _detection, value);
    }

    private VrmLlcReading? _currentLlc;
    public VrmLlcReading? CurrentLlc
    {
        get => _currentLlc;
        private set => SetProperty(ref _currentLlc, value);
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ── 建構 ──────────────────────────────────────────────────────────────

    /// <summary>供測試用：注入假的 I/O。</summary>
    internal VrmControllerService(ISmbusIo io, uint ioBase, int timeoutMs = 10)
    {
        _io = io;
        _ioBase = ioBase;
        _timeoutMs = timeoutMs;
    }

    /// <summary>擁有 WinRing0Bridge 的內部建構。</summary>
    private VrmControllerService(WinRing0Bridge bridge, ISmbusIo io, uint ioBase)
        : this(io, ioBase)
    {
        _ownedBridge = bridge;
    }

    /// <summary>從真實硬體建立。找不到 Intel PCH SMBus 時回 <c>null</c>。</summary>
    public static VrmControllerService? TryCreate(out string diagnostic)
    {
        var ring0 = WinRing0Bridge.Create();
        if (!ring0.IoPortAvailable)
        {
            diagnostic = "WinRing0 I/O 埠存取不可用，無法掃描 VRM 控制器。";
            ring0.Dispose();
            return null;
        }
        if (!ring0.PciAvailable)
        {
            diagnostic = "WinRing0 PCI 設定空間存取不可用，無法定位 SMBus 控制器。";
            ring0.Dispose();
            return null;
        }
        var loc = SmbusDiscovery.Find((b, d, f, r) => ring0.ReadPciConfig(b, d, f, r), out diagnostic);
        if (loc is null)
        {
            ring0.Dispose();
            return null;
        }
        var io = new WinRing0SmbusIo(ring0);
        diagnostic = $"SMBus 控制器就緒（SMB_BASE＝0x{loc.IoBase:X4}），可掃描 VRM。";
        return new VrmControllerService(ring0, io, loc.IoBase);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 純函式（可在測試中直接呼叫，不碰硬體）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>依 IC_DEVICE_ID 的 16 位值辨識晶片。未知 ID 回 <c>null</c>。</summary>
    internal static VrmChipInfo? TryIdentifyByDeviceId(byte address, ushort icDeviceId)
    {
        if (!IsVrmAddress(address)) return null;
        if (!KnownChipsByDeviceId.TryGetValue(icDeviceId, out var info)) return null;
        return new VrmChipInfo(info.Name, address, info.LlcReg, info.MaxLlc, info.Family);
    }

    /// <summary>
    /// 以暫存器指紋辨識晶片家族（IC_DEVICE_ID 讀不到時的後備）。
    /// 依序嘗試每個家族的 LLC 暫存器，第一個有回應的就是答案。
    /// </summary>
    /// <param name="address">7-bit 裝置位址。</param>
    /// <param name="probeResults">暫存器位址 → 讀到的值（<c>null</c> 代表未回應）。</param>
    internal static VrmChipInfo? TryIdentifyByFingerprint(byte address, IReadOnlyDictionary<byte, byte?> probeResults)
    {
        if (!IsVrmAddress(address)) return null;
        foreach (var (reg, familyName, maxLlc, family) in FingerprintRules)
        {
            if (probeResults.TryGetValue(reg, out byte? val) && val is not null)
                return new VrmChipInfo(familyName, address, reg, maxLlc, family);
        }
        return null;
    }

    /// <summary>驗證 LLC 等級是否在晶片支援範圍內。</summary>
    internal static bool IsValidLlcLevel(VrmChipInfo chip, int level)
        => level >= 0 && level <= chip.MaxLlcLevel;

    /// <summary>從 MaxLlcLevel 推導位元遮罩（例如 MaxLlcLevel=7 → 0x07）。</summary>
    internal static byte LlcMask(int maxLevel)
    {
        int mask = 1;
        while (mask <= maxLevel) mask <<= 1;
        return (byte)(mask - 1);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 公開操作（每個都走 SmbusBusLock＋硬體 INUSE_STS）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>掃描 SMBus 上的 VRM 控制器並辨識晶片。</summary>
    public async Task<VrmDetectionResult> DetectAsync()
    {
        Status = "正在掃描 VRM 控制器…";
        var result = await Task.Run(ScanBus);
        DetectionResult = result;

        if (result.Found && result.Chips.Count > 0)
        {
            var first = result.Chips[0];
            var reading = ReadLlc(first);
            CurrentLlc = reading;
            Status = $"偵測到 {first.ChipName}（0x{first.Address:X2}）"
                   + (reading is not null ? $"，LLC = {reading.Level}" : "");
        }
        else
        {
            Status = "未偵測到已知的 VRM PMBus 控制器。";
        }
        return result;
    }

    /// <summary>讀取指定晶片的當前 LLC 等級。</summary>
    public VrmLlcReading? ReadLlc(VrmChipInfo chip)
    {
        using var busLock = SmbusBusLock.TryAcquire(500, out _);
        if (busLock is null) return null;
        if (!TryAcquireHwBus()) return null;
        try
        {
            return ReadLlcCore(chip);
        }
        finally { ReleaseHwBus(); }
    }

    /// <summary>
    /// 寫入指定晶片的 LLC 等級。
    /// <b>⚠ 真實寫入 VRM 控制器的 LLC 暫存器。設錯可能導致 CPU 供電不穩、當機、甚至損壞硬體。</b>
    /// </summary>
    /// <returns>寫入且讀回驗證成功回 <c>true</c>。</returns>
    public bool WriteLlc(VrmChipInfo chip, int level)
    {
        // ── 拒絕條件 ──
        if (chip.VendorFamily == VrmVendorFamily.Unknown) return false;
        if (!IsValidLlcLevel(chip, level)) return false;

        using var busLock = SmbusBusLock.TryAcquire(500, out _);
        if (busLock is null) return false;
        if (!TryAcquireHwBus()) return false;
        try
        {
            // 寫入前先讀取當前值
            byte? before = ByteDataRead(chip.Address, chip.LlcRegister);
            if (before is null) return false;

            // 只改 LLC 位元，保留暫存器其餘位元
            byte mask = LlcMask(chip.MaxLlcLevel);
            byte newVal = (byte)((before.Value & ~mask) | (level & mask));

            // 寫入
            if (!ByteDataWrite(chip.Address, chip.LlcRegister, newVal)) return false;

            // 讀回驗證
            byte? after = ByteDataRead(chip.Address, chip.LlcRegister);
            if (after is null) return false;
            if ((after.Value & mask) != (level & mask)) return false;

            CurrentLlc = new VrmLlcReading(after.Value & mask, after.Value, chip.ChipName);
            return true;
        }
        finally { ReleaseHwBus(); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 匯流排掃描（在 SmbusBusLock 保護下執行）
    // ═══════════════════════════════════════════════════════════════════════

    private VrmDetectionResult ScanBus()
    {
        var log = new StringBuilder();
        var chips = new List<VrmChipInfo>();

        using var busLock = SmbusBusLock.TryAcquire(1000, out var lockNote);
        if (busLock is null)
        {
            log.AppendLine("無法取得 SMBus 軟體互斥鎖：" + lockNote);
            return new VrmDetectionResult(false, chips, log.ToString());
        }
        if (!string.IsNullOrEmpty(lockNote))
            log.AppendLine("鎖定備註：" + lockNote);

        if (!TryAcquireHwBus())
        {
            log.AppendLine("無法取得 SMBus 硬體旗號。");
            return new VrmDetectionResult(false, chips, log.ToString());
        }
        try
        {
            for (byte addr = VrmAddressMin; addr <= VrmAddressMax; addr++)
            {
                // 先用 Byte Data Read 探測此地址是否有裝置
                byte? probe = ByteDataRead(addr, PmbusPage);
                if (probe is null)
                {
                    log.AppendLine($"0x{addr:X2}：無回應");
                    continue;
                }
                log.AppendLine($"0x{addr:X2}：有裝置（PAGE=0x{probe:X2}）");

                // 嘗試 Word Data 讀 IC_DEVICE_ID
                ushort? devId = WordDataRead(addr, PmbusIcDeviceId);
                if (devId is not null)
                {
                    log.AppendLine($"  IC_DEVICE_ID = 0x{devId:X4}");
                    var chip = TryIdentifyByDeviceId(addr, devId.Value);
                    if (chip is not null)
                    {
                        log.AppendLine($"  → 辨識為 {chip.ChipName}（{chip.VendorFamily}）");
                        chips.Add(chip);
                        continue;
                    }
                    log.AppendLine($"  → 未知 IC_DEVICE_ID，嘗試指紋辨識");
                }
                else
                {
                    log.AppendLine($"  IC_DEVICE_ID 讀取失敗，嘗試指紋辨識");
                }

                // 後備：暫存器指紋辨識
                var probeResults = new Dictionary<byte, byte?>();
                foreach (var (reg, _, _, _) in FingerprintRules)
                {
                    byte? val = ByteDataRead(addr, reg);
                    probeResults[reg] = val;
                    if (val is not null)
                        log.AppendLine($"  reg 0x{reg:X2} = 0x{val:X2}");
                }
                var fpChip = TryIdentifyByFingerprint(addr, probeResults);
                if (fpChip is not null)
                {
                    log.AppendLine($"  → 指紋辨識為 {fpChip.ChipName}（{fpChip.VendorFamily}）");
                    chips.Add(fpChip);
                }
                else
                {
                    log.AppendLine($"  → 無法辨識");
                }
            }
        }
        finally { ReleaseHwBus(); }

        return new VrmDetectionResult(chips.Count > 0, chips, log.ToString());
    }

    /// <summary>讀 LLC（不取鎖，供內部在已持鎖的上下文呼叫）。</summary>
    private VrmLlcReading? ReadLlcCore(VrmChipInfo chip)
    {
        byte? raw = ByteDataRead(chip.Address, chip.LlcRegister);
        if (raw is null) return null;
        byte mask = LlcMask(chip.MaxLlcLevel);
        return new VrmLlcReading(raw.Value & mask, raw.Value, chip.ChipName);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // i801 SMBus 交易（VRM 位址限定）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>SMBus Byte Data 讀取。<b>只允許 VRM 位址範圍。</b></summary>
    private byte? ByteDataRead(byte slave7, byte command)
    {
        if (!IsVrmAddress(slave7))
            throw new ArgumentOutOfRangeException(nameof(slave7), slave7,
                "VRM 控制器服務只允許 0x40–0x4F 範圍的位址。");
        return RunTransaction((byte)((slave7 << 1) | 1), command, ProtoByteData, readMode: ReadMode.Byte);
    }

    /// <summary>SMBus Word Data 讀取（回 16 位：D0＝低、D1＝高）。<b>只允許 VRM 位址範圍。</b></summary>
    private ushort? WordDataRead(byte slave7, byte command)
    {
        if (!IsVrmAddress(slave7))
            throw new ArgumentOutOfRangeException(nameof(slave7), slave7,
                "VRM 控制器服務只允許 0x40–0x4F 範圍的位址。");
        byte? result = RunTransaction((byte)((slave7 << 1) | 1), command, ProtoWordData, readMode: ReadMode.Word);
        return result is not null ? _lastWord : null;
    }

    /// <summary>SMBus Byte Data 寫入。<b>只允許 VRM 位址範圍。</b></summary>
    private bool ByteDataWrite(byte slave7, byte command, byte data)
    {
        if (!IsVrmAddress(slave7))
            throw new ArgumentOutOfRangeException(nameof(slave7), slave7,
                "VRM 控制器服務只允許 0x40–0x4F 範圍的位址。");
        // 寫入方向：slave address bit 0 = 0
        _io.Out(_ioBase + HstD0, data);
        return RunTransaction((byte)(slave7 << 1), command, ProtoByteData, readMode: ReadMode.None) is not null;
    }

    private enum ReadMode { None, Byte, Word }
    private ushort _lastWord;

    private byte? RunTransaction(byte slva, byte cmdByte, byte protocol, ReadMode readMode)
    {
        var sw = Stopwatch.StartNew();
        if (!WaitNotBusy(sw)) return null;

        _io.Out(_ioBase + HstSts, StsClearMask);
        if (!_io.Out(_ioBase + XmitSlva, slva)
            || !_io.Out(_ioBase + HstCmd, cmdByte)
            || !_io.Out(_ioBase + HstCnt, (byte)(protocol | CntStart)))
            return null;

        while (true)
        {
            byte? sts = _io.In(_ioBase + HstSts);
            if (sts is null) return null;

            if ((sts.Value & StsErrorMask) != 0)
            {
                _io.Out(_ioBase + HstSts, StsClearMask);
                return null;
            }
            if ((sts.Value & StsIntr) != 0) break;

            if (sw.ElapsedMilliseconds > _timeoutMs)
            {
                _io.Out(_ioBase + HstCnt, CntKill);
                Thread.SpinWait(256);
                _io.Out(_ioBase + HstSts, StsClearMask);
                return null;
            }
            Thread.SpinWait(64);
        }

        byte result = 0;
        switch (readMode)
        {
            case ReadMode.Byte:
                byte? d0 = _io.In(_ioBase + HstD0);
                if (d0 is null) return null;
                result = d0.Value;
                break;
            case ReadMode.Word:
                byte? w0 = _io.In(_ioBase + HstD0);
                byte? w1 = _io.In(_ioBase + HstD1);
                if (w0 is null || w1 is null) return null;
                _lastWord = (ushort)(w0.Value | (w1.Value << 8));
                result = w0.Value;
                break;
        }

        _io.Out(_ioBase + HstSts, StsClearMask);
        return result;
    }

    private bool WaitNotBusy(Stopwatch sw)
    {
        while (true)
        {
            byte? sts = _io.In(_ioBase + HstSts);
            if (sts is null) return false;
            if ((sts.Value & StsHostBusy) == 0) return true;
            if (sw.ElapsedMilliseconds > _timeoutMs) return false;
            Thread.SpinWait(64);
        }
    }

    // ── 硬體 INUSE_STS 取得／歸還 ────────────────────────────────────────
    private bool _hwAcquired;

    private bool TryAcquireHwBus()
    {
        byte? sts = _io.In(_ioBase + HstSts);
        if (sts is null) return false;
        if ((sts.Value & StsInUse) != 0) return false;   // 別人在用，不搶
        _hwAcquired = true;
        return true;
    }

    private void ReleaseHwBus()
    {
        if (!_hwAcquired) return;
        _hwAcquired = false;
        _io.Out(_ioBase + HstSts, StsInUse);
    }

    // ═══════════════════════════════════════════════════════════════════════

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ownedBridge?.Dispose();
    }
}
