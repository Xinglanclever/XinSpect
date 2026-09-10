using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// VRM PMBus 自動偵測與 LLC 讀寫的純函式與狀態機測試。
/// </summary>
/// <remarks>
/// <para>
/// 這組測試<b>完全不碰真實 SMBus</b>：I/O 埠全是假的。理由同 <see cref="SmbusControllerTests"/>：
/// SMBus 是共享匯流排，搶了可能停機。
/// </para>
/// </remarks>
public class VrmControllerServiceTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // 純函式：TryIdentifyByDeviceId
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0x6938, "ISL69138", VrmVendorFamily.Renesas)]
    [InlineData(0x6927, "ISL69269", VrmVendorFamily.Renesas)]
    [InlineData(0x2856, "MP2856",   VrmVendorFamily.Mps)]
    [InlineData(0x2857, "MP2857",   VrmVendorFamily.Mps)]
    [InlineData(0x132D, "XDPE132G5C", VrmVendorFamily.Infineon)]
    public void TryIdentifyByDeviceId_已知晶片辨識正確(ushort id, string expectedName, VrmVendorFamily expectedFamily)
    {
        var chip = VrmControllerService.TryIdentifyByDeviceId(0x40, id);
        Assert.NotNull(chip);
        Assert.Equal(expectedName, chip.ChipName);
        Assert.Equal(expectedFamily, chip.VendorFamily);
        Assert.Equal((byte)0x40, chip.Address);
    }

    [Fact]
    public void TryIdentifyByDeviceId_未知ID回null()
    {
        Assert.Null(VrmControllerService.TryIdentifyByDeviceId(0x40, 0xBEEF));
    }

    [Fact]
    public void TryIdentifyByDeviceId_位址超出VRM範圍回null()
    {
        // 即使 ID 正確，位址不在 0x40–0x4F 也不認
        Assert.Null(VrmControllerService.TryIdentifyByDeviceId(0x50, 0x6938));
        Assert.Null(VrmControllerService.TryIdentifyByDeviceId(0x3F, 0x6938));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 純函式：TryIdentifyByFingerprint
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0xD2, VrmVendorFamily.Renesas)]
    [InlineData(0xD1, VrmVendorFamily.Infineon)]
    [InlineData(0x30, VrmVendorFamily.Mps)]
    public void TryIdentifyByFingerprint_LLC暫存器有回應就辨識家族(byte llcReg, VrmVendorFamily expectedFamily)
    {
        var probes = new Dictionary<byte, byte?> { [llcReg] = 0x03 };
        var chip = VrmControllerService.TryIdentifyByFingerprint(0x45, probes);
        Assert.NotNull(chip);
        Assert.Equal(expectedFamily, chip.VendorFamily);
        Assert.Equal(llcReg, chip.LlcRegister);
    }

    [Fact]
    public void TryIdentifyByFingerprint_全部無回應就回null()
    {
        var probes = new Dictionary<byte, byte?>
        {
            [0xD2] = null,
            [0xD1] = null,
            [0x30] = null,
        };
        Assert.Null(VrmControllerService.TryIdentifyByFingerprint(0x45, probes));
    }

    [Fact]
    public void TryIdentifyByFingerprint_位址超出VRM範圍回null()
    {
        var probes = new Dictionary<byte, byte?> { [0xD2] = 0x03 };
        Assert.Null(VrmControllerService.TryIdentifyByFingerprint(0x50, probes));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 純函式：IsValidLlcLevel
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsValidLlcLevel_範圍內合法()
    {
        var chip = new VrmChipInfo("ISL69269", 0x40, 0xD2, 7, VrmVendorFamily.Renesas);
        for (int i = 0; i <= 7; i++)
            Assert.True(VrmControllerService.IsValidLlcLevel(chip, i));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(255)]
    public void IsValidLlcLevel_超出範圍不合法(int level)
    {
        var chip = new VrmChipInfo("ISL69269", 0x40, 0xD2, 7, VrmVendorFamily.Renesas);
        Assert.False(VrmControllerService.IsValidLlcLevel(chip, level));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 純函式：LlcMask
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(7, 0x07)]
    [InlineData(3, 0x03)]
    [InlineData(1, 0x01)]
    [InlineData(15, 0x0F)]
    public void LlcMask_推導正確(int maxLevel, byte expectedMask)
    {
        Assert.Equal(expectedMask, VrmControllerService.LlcMask(maxLevel));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 狀態機：WriteLlc 未知晶片不嘗試寫入
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteLlc_Unknown晶片拒絕寫入()
    {
        var fakeIo = new FakeVrmIo();
        var svc = new VrmControllerService(fakeIo, FakeVrmIo.Base, timeoutMs: 30);

        var unknownChip = new VrmChipInfo("Unknown", 0x40, 0xD2, 7, VrmVendorFamily.Unknown);
        Assert.False(svc.WriteLlc(unknownChip, 3));
        // 確認沒有任何寫入發生
        Assert.Empty(fakeIo.Writes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 狀態機：WriteLlc 超出範圍拒絕
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteLlc_超出MaxLlcLevel拒絕()
    {
        var fakeIo = new FakeVrmIo();
        var svc = new VrmControllerService(fakeIo, FakeVrmIo.Base, timeoutMs: 30);

        var chip = new VrmChipInfo("ISL69269", 0x40, 0xD2, 7, VrmVendorFamily.Renesas);
        Assert.False(svc.WriteLlc(chip, 8));
        Assert.False(svc.WriteLlc(chip, -1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 狀態機：WriteLlc 寫入成功並讀回驗證
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteLlc_正常寫入並讀回驗證通過()
    {
        var fakeIo = new FakeVrmIo();
        fakeIo.SetRegister(0x40, 0xD2, 0x03);  // 原值 LLC=3
        var svc = new VrmControllerService(fakeIo, FakeVrmIo.Base, timeoutMs: 30);

        var chip = new VrmChipInfo("ISL69269", 0x40, 0xD2, 7, VrmVendorFamily.Renesas);
        Assert.True(svc.WriteLlc(chip, 5));
        Assert.NotNull(svc.CurrentLlc);
        Assert.Equal(5, svc.CurrentLlc!.Level);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 狀態機：WriteLlc 讀回驗證失敗時回 false
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteLlc_讀回驗證失敗回false()
    {
        var fakeIo = new FakeVrmIo();
        fakeIo.SetRegister(0x40, 0xD2, 0x03);
        fakeIo.VerifyWillFail = true;           // 寫入成功但讀回是錯的
        var svc = new VrmControllerService(fakeIo, FakeVrmIo.Base, timeoutMs: 30);

        var chip = new VrmChipInfo("ISL69269", 0x40, 0xD2, 7, VrmVendorFamily.Renesas);
        Assert.False(svc.WriteLlc(chip, 5));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 狀態機：ReadLlc
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ReadLlc_正常讀取()
    {
        var fakeIo = new FakeVrmIo();
        fakeIo.SetRegister(0x40, 0xD2, 0x05);  // LLC=5
        var svc = new VrmControllerService(fakeIo, FakeVrmIo.Base, timeoutMs: 30);

        var chip = new VrmChipInfo("ISL69269", 0x40, 0xD2, 7, VrmVendorFamily.Renesas);
        var reading = svc.ReadLlc(chip);
        Assert.NotNull(reading);
        Assert.Equal(5, reading!.Level);
        Assert.Equal(0x05, reading.RawByte);
        Assert.Equal("ISL69269", reading.ChipName);
    }

    [Fact]
    public void ReadLlc_只看低位元()
    {
        var fakeIo = new FakeVrmIo();
        fakeIo.SetRegister(0x40, 0xD2, 0xF5);  // 高位有其他旗標，LLC=5
        var svc = new VrmControllerService(fakeIo, FakeVrmIo.Base, timeoutMs: 30);

        var chip = new VrmChipInfo("ISL69269", 0x40, 0xD2, 7, VrmVendorFamily.Renesas);
        var reading = svc.ReadLlc(chip);
        Assert.NotNull(reading);
        Assert.Equal(5, reading!.Level);        // 0xF5 & 0x07 = 5
        Assert.Equal(0xF5, reading.RawByte);    // 原始值保留
    }
}

/// <summary>
/// 假的 SMBus I/O：支援 VRM PMBus 的 Byte Data Read／Write 與 Word Data Read 協定。
/// </summary>
/// <remarks>
/// 與 <see cref="FakeSmbusIo"/> 分開：VRM 的位址白名單（0x40–0x4F）、協定組合與 SPD 完全不同。
/// </remarks>
internal sealed class FakeVrmIo : ISmbusIo
{
    public const uint Base = 0xF040;
    private const uint Sts = Base + 0, Cnt = Base + 2, Cmd = Base + 3, Slva = Base + 4, D0 = Base + 5, D1 = Base + 6;

    /// <summary>(slave7, register) → byte 的暫存器映像。</summary>
    private readonly Dictionary<(byte Addr, byte Reg), byte> _registers = new();

    /// <summary>(slave7, register) → ushort 的 Word 暫存器映像（IC_DEVICE_ID 等）。</summary>
    private readonly Dictionary<(byte Addr, byte Reg), ushort> _wordRegisters = new();

    /// <summary>有裝置的位址集合（即使沒有暫存器也算有裝置）。</summary>
    private readonly HashSet<byte> _presentDevices = new();

    public readonly List<(uint Port, byte Value)> Writes = [];

    /// <summary>設為 true 後，WriteLlc 的第三次讀取（讀回驗證）會回覆錯的值。</summary>
    public bool VerifyWillFail;

    public bool BridgeGone;

    private byte _sts;
    private bool _inUse;
    private byte _slva, _cmd, _d0, _d1;
    private int _readCount;               // 用來讓 VerifyWillFail 只影響驗證那次
    private byte _lastWrittenValue;       // 記住最後寫入的值
    private bool _didWrite;               // 是否已發生過寫入

    public void SetRegister(byte addr, byte reg, byte value)
    {
        _registers[(addr, reg)] = value;
        _presentDevices.Add(addr);
    }

    public void SetWordRegister(byte addr, byte reg, ushort value)
    {
        _wordRegisters[(addr, reg)] = value;
        _presentDevices.Add(addr);
    }

    public void SetPresent(byte addr) => _presentDevices.Add(addr);

    public byte? In(uint port)
    {
        if (BridgeGone) return null;
        if (port == Sts)
        {
            byte v = _sts;
            if (_inUse) v |= 0x40;
            _inUse = true;
            return v;
        }
        if (port == D0) return _d0;
        if (port == D1) return _d1;
        if (port == Slva) return _slva;
        if (port == Cmd) return _cmd;
        return 0;
    }

    public bool Out(uint port, byte value)
    {
        if (BridgeGone) return false;
        Writes.Add((port, value));

        if (port == Sts)
        {
            _sts &= (byte)~(value & 0xBE);
            if ((value & 0x40) != 0) _inUse = false;
            return true;
        }
        if (port == Slva) { _slva = value; return true; }
        if (port == Cmd) { _cmd = value; return true; }
        if (port == D0) { _d0 = value; return true; }
        if (port == D1) { _d1 = value; return true; }
        if (port != Cnt) return true;

        if ((value & 0x02) != 0) { _sts |= 0x10; return true; }      // KILL → FAILED
        if ((value & 0x40) == 0) return true;                         // 沒按 START

        byte slave7 = (byte)(_slva >> 1);
        bool isRead = (_slva & 1) != 0;
        int protocol = (value >> 2) & 0x07;

        if (!_presentDevices.Contains(slave7)) { _sts |= 0x04; return true; }

        switch (protocol)
        {
            case 0x02:                                                // Byte Data
                if (isRead)
                {
                    // Byte Data 讀取
                    var key = (slave7, _cmd);
                    if (!_registers.ContainsKey(key)) { _sts |= 0x04; return true; }
                    _readCount++;
                    if (VerifyWillFail && _didWrite && _readCount >= 2)
                        _d0 = (byte)(_lastWrittenValue ^ 0xFF);      // 故意回錯
                    else
                        _d0 = _registers[key];
                }
                else
                {
                    // Byte Data 寫入：D0 已由 ByteDataWrite 在 START 前寫到 D0 埠
                    _registers[(slave7, _cmd)] = _d0;
                    _lastWrittenValue = _d0;
                    _didWrite = true;
                }
                break;

            case 0x03:                                                // Word Data
                if (isRead)
                {
                    var wkey = (slave7, _cmd);
                    if (!_wordRegisters.ContainsKey(wkey)) { _sts |= 0x04; return true; }
                    ushort w = _wordRegisters[wkey];
                    _d0 = (byte)(w & 0xFF);
                    _d1 = (byte)(w >> 8);
                }
                else
                {
                    _sts |= 0x04; return true;                       // 不支援 Word 寫入
                }
                break;

            default:
                _sts |= 0x04; return true;
        }

        _sts |= 0x02;                                                 // INTR＝完成
        return true;
    }
}
