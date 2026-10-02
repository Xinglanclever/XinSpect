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

        var facts = CmosService.Collect(new FakeCmosIo(ValidRegs()), At);
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
        var vrt = Assert.Single(CmosService.Collect(new FakeCmosIo(regs), At), f => f.Key == "cmos.rtc_valid");
        Assert.StartsWith("RTC 掉電", vrt.Value);
        Assert.True(EvidenceFactRow.From(vrt).IsWarning);

        var uip = ValidRegs();
        uip[0x0A] = 0xA6; // 更新進行中
        var time = Assert.Single(CmosService.Collect(new FakeCmosIo(uip), At), f => f.Key == "cmos.rtc_time");
        Assert.Equal(FactAvailability.ReadError, time.Availability);
        Assert.Contains("UIP=1", time.UnavailableReason);

        var noVrt = ValidRegs();
        noVrt[0x0D] = 0xFF; // 平台未實作
        var missing = Assert.Single(CmosService.Collect(new FakeCmosIo(noVrt), At), f => f.Key == "cmos.rtc_valid");
        Assert.Equal(FactAvailability.NotSupported, missing.Availability);
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
    private sealed class FakeCmosIo(byte[] regs) : IIoPortAccess
    {
        private int _index;
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte? InByte(uint port) => port == 0x71 ? regs[_index] : null;
        public bool OutByte(uint port, byte value) { if (port == 0x70) _index = value & 0x7F; return port == 0x70; }
    }
}
