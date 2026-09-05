using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// SMBus 主機控制器的探測與交易狀態機。
/// </summary>
/// <remarks>
/// <para>
/// 這組測試<b>完全不碰真實匯流排</b>：PCI 設定空間與 I/O 埠都是假的。SMBus 是共享匯流排
/// ——BIOS／SMM、CPU-Z、AIDA64、主機板燈光軟體都可能同時在上面。搶匯流排最壞會讓機器停頓，
/// 極端情況需要重開機，所以驗證狀態機的過程本身絕不可以碰它。
/// </para>
/// <para>
/// 最重要的兩條是白名單那兩個：<see cref="SmbusController.SendByte"/> 只准對 0x36／0x37
/// （DDR4 切頁）發，任何其他位址都必須拋例外。SPD 的寫入保護指令（SWP0–2、CWP，
/// 位址 0x31／0x33／0x34／0x35）就在那條線的另一邊——寫壞 SPD，主機板會認不出那條記憶體、
/// 開機不過，而且沒有軟體層的復原方式。
/// </para>
/// </remarks>
public class SmbusControllerTests
{
    private const uint Base = 0xF040;

    /// <summary>假的 I/O 埠：照 Intel PCH 的 HST_STS／HST_CNT 語意行為，包含 INUSE 的讀取即占用。</summary>
    private sealed class FakeIo : ISmbusIo
    {
        private const uint Sts = Base + 0, Cnt = Base + 2, Cmd = Base + 3, Slva = Base + 4, D0 = Base + 5;

        public bool BusyForever;
        public bool InUseHeldByOther;
        public bool NeverCompletes;
        public byte ErrorBits;
        public bool BridgeGone;
        public Func<byte, byte, byte?>? Respond;

        public readonly List<(uint Port, byte Value)> Writes = [];

        private byte _sts;
        private bool _inUse;
        private byte _slva, _cmd, _d0;

        public byte? In(uint port)
        {
            if (BridgeGone) return null;
            if (port == Sts)
            {
                byte v = _sts;
                if (BusyForever) v |= 0x01;
                if (_inUse || InUseHeldByOther) v |= 0x40;
                _inUse = true;                          // read-to-acquire：讀完就占住
                return v;
            }
            if (port == D0) return _d0;
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
                _sts &= (byte)~(value & 0xBE);          // 狀態位寫 1 清除；HOST_BUSY 唯讀
                if ((value & 0x40) != 0) { _inUse = false; InUseHeldByOther = false; }
                return true;
            }
            if (port == Slva) { _slva = value; return true; }
            if (port == Cmd) { _cmd = value; return true; }
            if (port != Cnt) return true;

            if ((value & 0x02) != 0) { _sts |= 0x10; return true; }     // KILL → FAILED
            if ((value & 0x40) == 0) return true;                        // 沒按 START
            if (NeverCompletes) return true;
            if (ErrorBits != 0) { _sts |= ErrorBits; return true; }
            if (((value >> 2) & 0x07) == 0x02)                           // Byte Data
            {
                var got = Respond?.Invoke((byte)(_slva >> 1), _cmd);
                if (got is null) { _sts |= 0x04; return true; }          // 無裝置 → DEV_ERR
                _d0 = got.Value;
            }
            _sts |= 0x02;                                                // INTR＝完成
            return true;
        }
    }

    private static SmbusController Make(FakeIo io) => new(io, Base, transactionTimeoutMs: 30);

    // ---- 交易狀態機 ----

    [Fact]
    public void 正常的ByteData讀取回傳HST_D0()
    {
        var io = new FakeIo { Respond = (slave, cmd) => slave == 0x50 ? (byte)(cmd ^ 0x5A) : null };
        var bus = Make(io);

        Assert.True(bus.TryAcquireBus(out var reason), reason);
        Assert.Equal((byte)(0x0C ^ 0x5A), bus.ReadByteData(0x50, 0x0C));
        bus.ReleaseBus();
    }

    [Fact]
    public void INUSE已被別人持有時取不到匯流排_而且不得把它清掉()
    {
        var io = new FakeIo { InUseHeldByOther = true };
        var bus = Make(io);

        Assert.False(bus.TryAcquireBus(out var reason));
        Assert.Contains("其他", reason);
        // 那個旗標不是我們的，動它等於把別人正在進行的交易掀掉
        Assert.DoesNotContain(io.Writes, w => w.Port == Base && (w.Value & 0x40) != 0);
    }

    [Fact]
    public void HOST_BUSY一直不放就逾時_並且送出KILL收尾()
    {
        var io = new FakeIo { BusyForever = true, Respond = (_, _) => 0x11 };
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Null(bus.ReadByteData(0x50, 0x00));
        Assert.Contains("逾時", bus.LastError);
        // 卡住的交易要收掉，否則 HOST_BUSY 一直亮著，下一個用匯流排的人（CPU-Z 也算）全被擋住
        Assert.Contains(io.Writes, w => w.Port == Base + 2 && (w.Value & 0x02) != 0);
    }

    [Fact]
    public void 完成訊號一直不來也算逾時()
    {
        var io = new FakeIo { NeverCompletes = true };
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Null(bus.ReadByteData(0x50, 0x00));
        Assert.Contains("逾時", bus.LastError);
    }

    [Theory]
    [InlineData((byte)0x04, "裝置")]
    [InlineData((byte)0x08, "匯流排")]
    [InlineData((byte)0x10, "失敗")]
    public void 控制器回報錯誤位時回null並說明是哪一種(byte errorBit, string expect)
    {
        var io = new FakeIo { ErrorBits = errorBit };
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Null(bus.ReadByteData(0x50, 0x00));
        Assert.Contains(expect, bus.LastError);
    }

    [Fact]
    public void 位址上沒有裝置時是讀不到_不是回0()
    {
        var io = new FakeIo { Respond = (slave, _) => slave == 0x50 ? (byte)0x77 : null };
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Equal((byte)0x77, bus.ReadByteData(0x50, 0x00));
        Assert.Null(bus.ReadByteData(0x53, 0x00));       // 空插槽
    }

    [Fact]
    public void 橋接不可用時連匯流排都取不到()
    {
        var bus = Make(new FakeIo { BridgeGone = true });

        Assert.False(bus.TryAcquireBus(out var reason));
        Assert.Contains("讀不到", reason);
    }

    /// <summary>
    /// 共用的橋接可能被別的元件 Dispose 掉（<see cref="WinRing0Bridge"/> 的引用計數就是為此而存在）。
    /// 這時候必須說「讀不到」——說成「逾時」會把人引去查匯流排壅塞，而真正的原因是驅動沒了。
    /// </summary>
    [Fact]
    public void 交易途中橋接消失要說是讀不到而不是逾時()
    {
        var io = new FakeIo { Respond = (_, _) => 0x01 };
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));

        io.BridgeGone = true;
        Assert.Null(bus.ReadByteData(0x50, 0x00));
        Assert.Contains("讀不到", bus.LastError);
        Assert.DoesNotContain("逾時", bus.LastError);
    }

    [Fact]
    public void 釋放匯流排時把INUSE寫回去()
    {
        var io = new FakeIo();
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));
        bus.ReleaseBus();

        Assert.Contains(io.Writes, w => w.Port == Base && (w.Value & 0x40) != 0);
    }

    [Fact]
    public void 沒取得匯流排就讀取是程式錯誤()
    {
        var bus = Make(new FakeIo());
        Assert.Throws<InvalidOperationException>(() => bus.ReadByteData(0x50, 0x00));
    }

    // ---- 裝置位址白名單：本檔最要緊的兩組 ----

    [Theory]
    [InlineData((byte)0x50)]
    [InlineData((byte)0x53)]
    [InlineData((byte)0x57)]
    public void SPD的八個EEPROM位址可以讀(byte slave)
    {
        var io = new FakeIo { Respond = (_, _) => 0x01 };
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));
        Assert.Equal((byte)0x01, bus.ReadByteData(slave, 0x00));
    }

    [Theory]
    [InlineData((byte)0x36)]   // 切頁位址不是資料來源
    [InlineData((byte)0x4F)]
    [InlineData((byte)0x58)]
    [InlineData((byte)0x30)]
    public void 其餘位址一律不准讀(byte slave)
    {
        var bus = Make(new FakeIo());
        Assert.True(bus.TryAcquireBus(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => bus.ReadByteData(slave, 0x00));
    }

    [Theory]
    [InlineData((byte)0x36)]
    [InlineData((byte)0x37)]
    public void 只有DDR4的兩個切頁位址可以寫(byte slave)
    {
        var bus = Make(new FakeIo());
        Assert.True(bus.TryAcquireBus(out _));
        Assert.True(bus.SendByte(slave, 0x00));
    }

    /// <summary>
    /// 這一條是整個功能的安全底線。0x31／0x34／0x35 是 SWP0–2（把 SPD 永久寫入保護），
    /// 0x33 是 CWP（清除保護），0x50–0x57 是 EEPROM 資料區本身。
    /// 送錯一次就可能讓那條記憶體再也開不了機，而且沒有軟體層的復原方式。
    /// </summary>
    [Theory]
    [InlineData((byte)0x31)]
    [InlineData((byte)0x33)]
    [InlineData((byte)0x34)]
    [InlineData((byte)0x35)]
    [InlineData((byte)0x50)]
    [InlineData((byte)0x57)]
    public void 寫入保護指令與EEPROM資料區永遠不准寫(byte slave)
    {
        var io = new FakeIo();
        var bus = Make(io);
        Assert.True(bus.TryAcquireBus(out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => bus.SendByte(slave, 0x00));
        // 不只是回錯——連 START 都不許送出去
        Assert.DoesNotContain(io.Writes, w => w.Port == Base + 2 && (w.Value & 0x40) != 0);
    }

    // ---- 控制器探測 ----

    private static PciDwordReader Pci(Dictionary<(byte Dev, byte Fn, uint Reg), uint> space)
        => (bus, dev, fn, reg) => bus != 0 ? 0xFFFFFFFF
            : space.TryGetValue((dev, fn, reg), out var v) ? v : 0xFFFFFFFF;

    private static Dictionary<(byte, byte, uint), uint> IntelSmbus(uint hostc = 0x01, uint bar = 0xF041) => new()
    {
        [(31, 4, 0x00)] = 0xA2A38086,       // Intel 200 系列 PCH 的 SMBus 控制器
        [(31, 4, 0x08)] = 0x0C050000,       // 基類 0x0C（序列匯流排）／子類 0x05（SMBus）
        [(31, 4, 0x20)] = bar,              // SMB_BASE：I/O 空間，基底 0xF040
        [(31, 4, 0x40)] = hostc,            // HOSTC
    };

    [Fact]
    public void 從PCI設定空間找出SMBus控制器與IO基底()
    {
        var found = SmbusDiscovery.Find(Pci(IntelSmbus()), out var note);

        Assert.NotNull(found);
        Assert.Equal(0xF040u, found!.IoBase);
        Assert.Equal((byte)31, found.Device);
        Assert.Equal((byte)4, found.Function);
        Assert.Equal(0x8086, found.VendorId);
        Assert.False(found.SpdWriteDisabled);
        Assert.Contains("0.31.4", note);
    }

    [Fact]
    public void 沒有SMBus裝置時回null並說明()
    {
        Assert.Null(SmbusDiscovery.Find(Pci([]), out var note));
        Assert.Contains("找不到", note);
    }

    [Fact]
    public void 類別碼不是SMBus的裝置不得被誤認()
    {
        var space = IntelSmbus();
        space[(31, 4, 0x08)] = 0x0C030000;      // 序列匯流排底下的 USB，不是 SMBus
        Assert.Null(SmbusDiscovery.Find(Pci(space), out _));
    }

    [Fact]
    public void I2C模式下拒絕使用()
    {
        var found = SmbusDiscovery.Find(Pci(IntelSmbus(hostc: 0x04)), out var note);

        Assert.Null(found);
        Assert.Contains("I2C", note);
    }

    [Fact]
    public void SPD寫入停用旗標要照實回報_但不阻擋讀取()
    {
        var found = SmbusDiscovery.Find(Pci(IntelSmbus(hostc: 0x11)), out _);

        Assert.NotNull(found);
        Assert.True(found!.SpdWriteDisabled);
    }

    [Theory]
    [InlineData(0x00000000u)]      // BAR 沒配置
    [InlineData(0xFFFFFFFFu)]      // 讀不到
    [InlineData(0x0000F040u)]      // bit 0 為 0＝記憶體空間，不是 I/O
    public void IO基底不合理時判為讀不到(uint bar)
    {
        Assert.Null(SmbusDiscovery.Find(Pci(IntelSmbus(bar: bar)), out var note));
        Assert.Contains("I/O", note);
    }

    [Fact]
    public void AMD的FCH要明說不支援_而不是拿PCI的BAR亂算()
    {
        var space = IntelSmbus();
        space[(31, 4, 0x00)] = 0x790B1022;      // AMD FCH SMBus
        var found = SmbusDiscovery.Find(Pci(space), out var note);

        Assert.Null(found);
        Assert.Contains("AMD", note);
    }
}
