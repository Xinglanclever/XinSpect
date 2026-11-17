using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// CMOS/RTC 唯讀事實的解碼契約：BCD／12 小時制轉換、UIP 不以半新半舊冒充、
/// 垃圾格式不解碼、PC-AT 校驗和比對。服務端以假 0x70/0x71 埠協定驗證，不碰真 I/O。
/// </summary>
public class CmosTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VRT位元解碼()
    {
        Assert.True(Cmos.VrtValid(0x80));
        Assert.False(Cmos.VrtValid(0x00));
    }

    [Fact]
    public void 狀態位解碼_UIP與格式位()
    {
        // 0x0A=0x26（UIP=0）、0x0B=0x02（BCD、24h）
        Assert.Equal((false, false, true), Cmos.DecodeStatus(0x26, 0x02));
        Assert.Equal((true, false, true), Cmos.DecodeStatus(0xA6, 0x02));
        Assert.Equal((false, true, false), Cmos.DecodeStatus(0x26, 0x04)); // 二進位、12h（bit1=0）
        Assert.Equal((false, true, true), Cmos.DecodeStatus(0x26, 0x06));  // 二進位、24h
    }

    [Fact]
    public void 時間解碼_BCD與二進位與12小時制_垃圾回null()
    {
        Assert.Equal((14, 35, 45), Cmos.DecodeTime(0x45, 0x35, 0x14, binary: false, hour24: true));
        Assert.Equal((20, 35, 45), Cmos.DecodeTime(0x2D, 0x23, 0x14, binary: true, hour24: true)); // 二進位 45/35/20

        // 12 小時制：PM 12＝正午、AM 12＝午夜、PM 5＝17
        Assert.Equal((0, 0, 0), Cmos.DecodeTime(0x00, 0x00, 0x12, binary: false, hour24: false));
        Assert.Equal((12, 0, 0), Cmos.DecodeTime(0x00, 0x00, 0x92, binary: false, hour24: false)); // 0x12|PM
        Assert.Equal((17, 0, 0), Cmos.DecodeTime(0x00, 0x00, 0x85, binary: false, hour24: false)); // 05|PM

        // 垃圾：BCD 位含 A–F、分鐘 99
        Assert.Null(Cmos.DecodeTime(0x45, 0x35, 0x1A, binary: false, hour24: true));
        Assert.Null(Cmos.DecodeTime(0x99, 0x35, 0x14, binary: false, hour24: true));
    }

    [Fact]
    public void PCAT校驗和_相符與不符()
    {
        var regs = new byte[0x40];
        new Random(7).NextBytes(regs);
        int sum = 0;
        for (int i = 0x10; i <= 0x2D; i++) sum += regs[i];
        sum &= 0xFFFF;
        regs[0x2E] = (byte)(sum >> 8);
        regs[0x2F] = (byte)sum;
        Assert.True(Cmos.ChecksumMatches(regs));

        regs[0x11] ^= 0xFF; // 動一格
        Assert.False(Cmos.ChecksumMatches(regs));
    }

    [Fact]
    public void 服務_不可用埠整組三態_正常埠三事實成列()
    {
        var denied = CmosService.Collect(new UnavailableIoPortAccess("缺 I/O 埠存取"), At);
        Assert.Equal(3, denied.Count);
        Assert.All(denied, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));

        var facts = CmosService.Collect(new FakeCmosIo(ValidRegs()), At, () => TimeSpan.Zero);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.Present, f.Availability));
        Assert.Contains("有效（VRT=1）", Assert.Single(facts, f => f.Key == "cmos.rtc_valid").Value);
        Assert.StartsWith("14:35:45", Assert.Single(facts, f => f.Key == "cmos.rtc_time").Value);
        Assert.Contains("相符", Assert.Single(facts, f => f.Key == "cmos.checksum").Value);
    }

    [Fact]
    public void 服務_VRT掉電走警示_UIP不冒充_全F如實標()
    {
        var regs = ValidRegs();
        regs[0x0D] = 0x00; // 電池掉電
        var vrt = Assert.Single(CmosService.Collect(new FakeCmosIo(regs), At, () => TimeSpan.Zero), f => f.Key == "cmos.rtc_valid");
        Assert.StartsWith("RTC 掉電", vrt.Value);
        Assert.True(EvidenceFactRow.From(vrt).IsWarning);

        var uip = ValidRegs();
        uip[0x0A] = 0xA6; // 更新進行中
        var time = Assert.Single(CmosService.Collect(new FakeCmosIo(uip), At, () => TimeSpan.Zero), f => f.Key == "cmos.rtc_time");
        Assert.Equal(FactAvailability.ReadError, time.Availability);
        Assert.Contains("UIP=1", time.UnavailableReason);

        var noVrt = ValidRegs();
        noVrt[0x0D] = 0xFF; // 平台未實作
        var missing = Assert.Single(CmosService.Collect(new FakeCmosIo(noVrt), At, () => TimeSpan.Zero), f => f.Key == "cmos.rtc_valid");
        Assert.Equal(FactAvailability.NotSupported, missing.Availability);
    }


    [Fact]
    public void 一般埠能力不等於RTC所有權_不可存取埠()
    {
        var io = new PlainAvailableIo();
        var facts = CmosService.Collect(io, At, () => TimeSpan.Zero);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.NotSupported, f.Availability);
            Assert.Contains("所有權", f.UnavailableReason);
            Assert.Contains("不代表沒有 RTC", f.UnavailableReason);
        });
        Assert.Equal(0, io.Calls);
        Assert.Empty(CmosService.Snapshot(io, () => TimeSpan.Zero).Registers);
        Assert.Equal(0, io.Calls);
    }

    [Fact]
    public void 不可用優先於ownership檢查_拒絕lease不進埠()
    {
        var io = new RecordingCmosIo { AvailableValue = false, AllowLease = false };
        Assert.All(CmosService.Collect(io, At, () => TimeSpan.Zero), f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
        Assert.Equal(0, io.LeaseAttempts);
        Assert.Empty(io.Selected);
        io.AvailableValue = true;
        Assert.All(CmosService.Collect(io, At, () => TimeSpan.Zero), f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.Equal(1, io.LeaseAttempts);
        Assert.Empty(io.Selected);
        Assert.Equal(0, io.Released);
    }

    [Fact]
    public void 稀疏快照只包含已讀白名單_不讀清旗標或警報地址()
    {
        var io = new RecordingCmosIo();
        var snapshot = CmosService.Snapshot(io, () => TimeSpan.Zero);
        Assert.Equal(FactAvailability.Present, snapshot.Availability);
        Assert.Null(snapshot.TimeUnavailableReason);
        Assert.Equal(38, snapshot.Registers.Count); // 32 checksum + VRT + 5 RTC fields
        Assert.All(io.Selected, index => Assert.True(index is 0 or 2 or 4 or 0xA or 0xB or 0xD || index is >= 0x10 and <= 0x2F));
        Assert.DoesNotContain((byte)0x0C, io.Selected);
        Assert.DoesNotContain((byte)0x0C, snapshot.Registers.Keys);
        Assert.DoesNotContain((byte)0x01, snapshot.Registers.Keys);
        Assert.DoesNotContain((byte)0x03, snapshot.Registers.Keys);
        Assert.DoesNotContain((byte)0x05, snapshot.Registers.Keys);
        Assert.DoesNotContain((byte)0x30, snapshot.Registers.Keys);
        Assert.Equal(1, io.Released);
        Assert.Equal(io.Selected.Count, io.DataReads);
    }

    [Fact]
    public void 秒進位後UIP已清仍不可輸出混合時間()
    {
        var io = new RecordingCmosIo();
        io.Regs[0x02] = 0x01;
        io.Regs[0x04] = 0x12;
        io.ReadOverride = (index, nth) => index == 0x00 ? (byte)(nth == 1 ? 0x59 : 0x00) : io.Regs[index];
        var time = Assert.Single(CmosService.Collect(io, At, () => TimeSpan.Zero), f => f.Key == "cmos.rtc_time");
        Assert.Equal(FactAvailability.Present, time.Availability);
        Assert.StartsWith("12:01:00", time.Value);
        Assert.DoesNotContain("12:01:59", time.Value);
        Assert.True(io.ReadCounts[0x00] >= 6);
        Assert.Equal(1, io.Released);
    }

    [Fact]
    public void 前後UIP皆清但兩組樣本不同_有界拒絕而非混用()
    {
        var io = new RecordingCmosIo();
        io.ReadOverride = (index, nth) => index == 0x00 ? (byte)((((nth - 1) / 2) % 2 == 0) ? 0x45 : 0x46) : io.Regs[index];
        var facts = CmosService.Collect(io, At, () => TimeSpan.Zero);
        var time = Assert.Single(facts, f => f.Key == "cmos.rtc_time");
        Assert.Equal(FactAvailability.ReadError, time.Availability);
        Assert.Equal("", time.Value);
        Assert.Contains("一致", time.UnavailableReason);
        Assert.Equal(FactAvailability.Present, Assert.Single(facts, f => f.Key == "cmos.rtc_valid").Availability);
        Assert.Equal(FactAvailability.Present, Assert.Single(facts, f => f.Key == "cmos.checksum").Availability);
        Assert.Equal(81, io.DataReads);
        Assert.Equal(1, io.Released);
    }

    [Fact]
    public void UIP短暫為一可在預算內重讀_持續為一不輸出時間()
    {
        var io = new RecordingCmosIo();
        io.ReadOverride = (index, nth) => index == 0x0A && nth == 1 ? (byte)0xA6 : io.Regs[index];
        Assert.Equal(FactAvailability.Present, Assert.Single(CmosService.Collect(io, At, () => TimeSpan.Zero), f => f.Key == "cmos.rtc_time").Availability);
        var busy = new RecordingCmosIo();
        busy.Regs[0x0A] = 0xA6;
        var snapshot = CmosService.Snapshot(busy, () => TimeSpan.Zero);
        Assert.Equal(FactAvailability.Present, snapshot.Availability);
        Assert.Contains("UIP=1", snapshot.TimeUnavailableReason);
        Assert.False(snapshot.Registers.ContainsKey(0x00));
        Assert.False(snapshot.Registers.ContainsKey(0x02));
        Assert.Equal(36, busy.DataReads); // 33 static + 3 bounded UIP polls
        Assert.Equal(1, busy.Released);
    }

    [Theory]
    [InlineData(0x0A)]
    [InlineData(0x0B)]
    public void 樣本中UIP或格式改變不得宣稱Present(int changedRegister)
    {
        var io = new RecordingCmosIo();
        io.ReadOverride = (index, nth) => index == changedRegister && nth % 2 == 0
            ? (byte)(index == 0x0A ? 0xA6 : 0x06) : io.Regs[index];
        var time = Assert.Single(CmosService.Collect(io, At, () => TimeSpan.Zero), f => f.Key == "cmos.rtc_time");
        Assert.Equal(FactAvailability.ReadError, time.Availability);
        Assert.Equal("", time.Value);
        Assert.InRange(io.DataReads, 1, 81);
        Assert.Equal(1, io.Released);
    }

    [Fact]
    public void SET抑制中的時間不發佈_不寫RTC資料或SET位()
    {
        var io = new RecordingCmosIo();
        io.Regs[0x0B] = 0x82;
        Assert.Equal(FactAvailability.ReadError, Assert.Single(CmosService.Collect(io, At, () => TimeSpan.Zero), f => f.Key == "cmos.rtc_time").Availability);
        Assert.InRange(io.DataReads, 1, 81);
        Assert.DoesNotContain((byte)0x0C, io.Selected);
        Assert.Equal(1, io.Released);
    }

    [Theory]
    [InlineData(0x0D)]
    [InlineData(0x10)]
    [InlineData(0x2F)]
    [InlineData(0x0A)]
    [InlineData(0x02)]
    public void 部分資料讀失敗即停止_不重送不碰清旗標且交還lease(int failedRegister)
    {
        var io = new RecordingCmosIo();
        io.ReadOverride = (index, _) => index == failedRegister ? null : io.Regs[index];
        var facts = CmosService.Collect(io, At, () => TimeSpan.Zero);
        Assert.All(facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Equal((byte)failedRegister, io.Selected[^1]);
        Assert.Equal(1, io.ReadCounts[(byte)failedRegister]);
        Assert.DoesNotContain((byte)0x0C, io.Selected);
        Assert.Equal(1, io.Released);
    }

    [Fact]
    public void 原選址gate拒絕時不讀資料也不改走替代通路()
    {
        var io = new RecordingCmosIo { DenyWriteNumber = 2 };
        Assert.All(CmosService.Collect(io, At, () => TimeSpan.Zero), f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.Equal(2, io.Selected.Count);
        Assert.Equal(1, io.DataReads);
        Assert.Equal(1, io.Released);
    }

    [Fact]
    public void 預算耗盡前不做第一個選址_選址後過期不做資料讀()
    {
        var before = new RecordingCmosIo();
        var snapshot = CmosService.Snapshot(before, () => TimeSpan.FromMilliseconds(100));
        Assert.Equal(FactAvailability.ReadError, snapshot.Availability);
        Assert.Empty(before.Selected);
        Assert.Equal(1, before.Released);
        var after = new RecordingCmosIo();
        var elapsed = TimeSpan.Zero;
        after.AfterWrite = () => elapsed = TimeSpan.FromMilliseconds(100);
        snapshot = CmosService.Snapshot(after, () => elapsed);
        Assert.Equal(FactAvailability.ReadError, snapshot.Availability);
        Assert.Single(after.Selected);
        Assert.Equal(0, after.DataReads);
        Assert.Equal(1, after.Released);
    }

    [Fact]
    public void 同步I_O確實返回後才交還lease_過期結果不發佈()
    {
        var io = new RecordingCmosIo();
        var elapsed = TimeSpan.Zero;
        io.AfterRead = () =>
        {
            Assert.Equal(0, io.Released);
            elapsed = TimeSpan.FromMilliseconds(101);
        };
        var snapshot = CmosService.Snapshot(io, () => elapsed);
        Assert.Equal(FactAvailability.ReadError, snapshot.Availability);
        Assert.Empty(snapshot.Registers);
        Assert.Equal(1, io.DataReads);
        Assert.Equal(1, io.Released);
    }

    [Fact]
    public void 後端例外也會在同步呼叫退出後交還所有權()
    {
        var io = new RecordingCmosIo();
        io.ReadOverride = (_, _) => throw new InvalidOperationException("fake backend failure");
        Assert.Throws<InvalidOperationException>(() => CmosService.Snapshot(io, () => TimeSpan.Zero));
        Assert.Equal(1, io.Released);
        Assert.Single(io.Selected);
        Assert.Equal(1, io.DataReads);
    }

    private sealed class PlainAvailableIo : IIoPortAccess
    {
        public int Calls;
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte? InByte(uint port) { Calls++; return 0; }
        public bool OutByte(uint port, byte value) { Calls++; return true; }
    }

    private sealed class TestLease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class RecordingCmosIo : ICmosReadOwnership
    {
        public readonly byte[] Regs = ValidRegs();
        public readonly List<byte> Selected = [];
        public readonly Dictionary<byte, int> ReadCounts = [];
        public Func<byte, int, byte?>? ReadOverride;
        public Action? AfterRead, AfterWrite;
        public bool AvailableValue = true, AllowLease = true;
        public int LeaseAttempts, Released, DataReads, DenyWriteNumber = -1;
        private byte _index;
        public bool Available => AvailableValue;
        public string? UnavailableReason => "fake unavailable";
        public string? CmosOwnershipUnavailableReason => "fake owner denied lease";
        public IDisposable? TryAcquireCmosReadLease()
        {
            LeaseAttempts++;
            return AllowLease ? new TestLease(() => Released++) : null;
        }
        public bool OutByte(uint port, byte value)
        {
            Assert.Equal(0x70u, port); // No RTC-data writes, no SET-bit writes, no alternate ports.
            Assert.Equal(0, Released);
            Assert.True(LeaseAttempts > 0);
            Assert.Equal(0, value & 0x80);
            Selected.Add(value);
            if (Selected.Count == DenyWriteNumber) return false;
            _index = value;
            AfterWrite?.Invoke();
            return true;
        }
        public byte? InByte(uint port)
        {
            Assert.Equal(0x71u, port);
            Assert.Equal(0, Released);
            Assert.NotEqual((byte)0x0C, _index);
            DataReads++;
            int nth = ReadCounts.GetValueOrDefault(_index) + 1;
            ReadCounts[_index] = nth;
            byte? value = ReadOverride is null ? Regs[_index] : ReadOverride(_index, nth);
            AfterRead?.Invoke();
            return value;
        }
    }

    private static byte[] ValidRegs()
    {
        var regs = new byte[0x40];
        regs[0x0A] = 0x26;             // UIP=0
        regs[0x0B] = 0x02;             // BCD、24h
        regs[0x00] = 0x45; regs[0x02] = 0x35; regs[0x04] = 0x14; // 14:35:45
        regs[0x0D] = 0x80;             // VRT=1
        new Random(7).NextBytes(regs.AsSpan(0x10, 0x1E)); // 0x10–0x2D 設定區
        int sum = 0;
        for (int i = 0x10; i <= 0x2D; i++) sum += regs[i];
        sum &= 0xFFFF;
        regs[0x2E] = (byte)(sum >> 8);
        regs[0x2F] = (byte)sum;
        return regs;
    }

    /// <summary>實作 0x70/0x71 讀取協定的假埠：寫 0x70 記索引、讀 0x71 回該暫存器。</summary>
    private sealed class FakeCmosIo(byte[] regs) : ICmosReadOwnership
    {
        private int _index;
        public IDisposable? TryAcquireCmosReadLease() => new TestLease(() => { });
        public string? CmosOwnershipUnavailableReason => null;
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte? InByte(uint port) => port == 0x71 ? regs[_index] : null;
        public bool OutByte(uint port, byte value) { if (port == 0x70) _index = value & 0x7F; return port == 0x70; }
    }
}
