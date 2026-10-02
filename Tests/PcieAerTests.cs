using Xunit;

namespace XinSpect.Tests;

public sealed class PcieAerTests
{
    [Fact]
    public void 找得到AER_走擴充能力鏈結串列()
    {
        var cfg = new byte[4096];
        WriteCapHeader(cfg, 0x100, id: 0x000B, next: 0x140); // 非 AER，鏈到 0x140
        WriteCapHeader(cfg, 0x140, id: PcieAer.ExtCapIdAer, next: 0);
        Assert.Equal(0x140, PcieAer.FindAerCapOffset(cfg));
    }

    [Fact]
    public void 無AER能力_回null()
    {
        var cfg = new byte[4096];
        WriteCapHeader(cfg, 0x100, id: 0x000B, next: 0);
        Assert.Null(PcieAer.FindAerCapOffset(cfg));
    }

    [Fact]
    public void 解AER狀態_讀出可修正與不可修正錯誤位()
    {
        var cfg = new byte[4096];
        WriteCapHeader(cfg, 0x100, id: PcieAer.ExtCapIdAer, next: 0);
        BitConverter.GetBytes(0x40u).CopyTo(cfg, 0x100 + 0x04); // Uncorrectable Error Status
        BitConverter.GetBytes(0x1u).CopyTo(cfg, 0x100 + 0x10);  // Correctable Error Status

        var aer = PcieAer.DecodeAer(cfg, 0x100)!.Value;

        Assert.Equal(0x40u, aer.UncorrectableStatus);
        Assert.True(aer.HasUncorrectable);
        Assert.Equal(0x1u, aer.CorrectableStatus);
        Assert.True(aer.HasCorrectable);
    }

    [Fact]
    public void 解AER狀態_無錯誤時旗標為false()
    {
        var cfg = new byte[4096];
        WriteCapHeader(cfg, 0x100, id: PcieAer.ExtCapIdAer, next: 0);
        var aer = PcieAer.DecodeAer(cfg, 0x100)!.Value;
        Assert.False(aer.HasUncorrectable);
        Assert.False(aer.HasCorrectable);
    }

    // 擴充能力表頭：CapID[15:0] | CapVer[19:16] | NextOffset[31:20]
    private static void WriteCapHeader(byte[] cfg, int offset, ushort id, int next)
    {
        uint header = id | (1u << 16) | ((uint)next << 20);
        BitConverter.GetBytes(header).CopyTo(cfg, offset);
    }
}
