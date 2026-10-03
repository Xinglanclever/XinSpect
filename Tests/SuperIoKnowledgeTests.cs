using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Super I/O 名稱知識（WP30／D2）的契約：coreboot superiotool 出處化的對照表查得准、
/// 未收錄回 null 不猜、探測服務把名字與原始 ID 並列。
/// </summary>
public class SuperIoKnowledgeTests
{
    [Theory]
    [InlineData(0x8728, "IT8728F")]
    [InlineData(0x8623, "IT8623E")]
    [InlineData(0x8689, "IT8689E")]
    [InlineData(0xC562, "NCT6779D")]
    [InlineData(0xD42A, "NCT6796D")]
    public void 常見晶片查得到名稱(ushort chipId, string expected)
    {
        Assert.Equal(expected, SuperIoKnowledge.ChipName(chipId));
    }

    [Fact]
    public void 未收錄回null不猜()
    {
        Assert.Null(SuperIoKnowledge.ChipName(0x1234));
        Assert.Null(SuperIoKnowledge.ChipName(0x0000));
    }

    [Fact]
    public void 探測服務_收錄晶片帶名稱_未收錄誠實標()
    {
        var at = DateTimeOffset.UtcNow;
        var known = SuperIoProbeService.Collect(new FakeSuperIoForName(0x8728), at);
        var k = Assert.Single(known, f => f.Key == "sio.0x2e");
        Assert.Contains("IT8728F", k.Value);
        Assert.DoesNotContain("未收錄", k.Value);

        var unknown = SuperIoProbeService.Collect(new FakeSuperIoForName(0x9999), at);
        var u = Assert.Single(unknown, f => f.Key == "sio.0x2e");
        Assert.Contains("0x9999", u.Value);
        Assert.Contains("名稱對照未收錄", u.Value);
    }

    private sealed class FakeSuperIoForName(ushort chipId) : IIoPortAccess
    {
        private int _index;
        private bool _entered, _sawFirstMagic;
        public bool Available => true;
        public string? UnavailableReason => null;

        public byte? InByte(uint port)
        {
            if (!(_entered && port == 0x2F)) return 0xFF;
            return _index switch
            {
                SuperIo.RegChipIdHigh => (byte)(chipId >> 8),
                SuperIo.RegChipIdLow => (byte)chipId,
                SuperIo.RegVendorIdHigh => (byte)0x90,
                SuperIo.RegVendorIdLow => (byte)0x86,
                _ => (byte)0xFF,
            };
        }

        public bool OutByte(uint port, byte value)
        {
            if (port != 0x2E) return true;
            if (value == SuperIo.ExitCommand) { _entered = false; return true; }
            if (value == 0x87)
            {
                if (_sawFirstMagic) _entered = true;
                _sawFirstMagic = !_sawFirstMagic;
                return true;
            }
            if (_entered) _index = value;
            return true;
        }
    }
}
