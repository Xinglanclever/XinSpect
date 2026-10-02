using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Super I/O 探測的契約：ID 解碼（全 F／全 0 不給值）、兩種進入序列都嘗試、
/// <b>任何路徑都以 finally 退出設定模式</b>（留在設定模式是系統風險）、無裝置如實三態。
/// 以假 0x2E/0x2F 埠協定驗證，不碰真 I/O。
/// </summary>
public class SuperIoProbeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x87u, 0x86u, 0x8786u)]
    [InlineData(0x97u, 0x71u, 0x9771u)]
    public void 晶片ID解碼_原始組回(uint hi, uint lo, uint expected)
    {
        var id = SuperIo.DecodeChipId((byte)hi, (byte)lo);
        Assert.NotNull(id);
        Assert.Equal((ushort)expected, id.Value);
    }

    [Theory]
    [InlineData(0xFFu, 0xFFu)]
    [InlineData(0x00u, 0x00u)]
    public void 晶片ID解碼_全F與全0不給值(uint hi, uint lo)
    {
        Assert.Null(SuperIo.DecodeChipId((byte)hi, (byte)lo));
    }

    [Fact]
    public void 探測_有晶片給原始ID且必定退出設定模式()
    {
        var io = new FakeSuperIo { ChipId = 0x8786, VendorId = 0x9086 };
        var facts = SuperIoProbeService.Collect(io, At);

        var sio = Assert.Single(facts, f => f.Key == "sio.0x2e");
        Assert.Equal(FactAvailability.Present, sio.Availability);
        Assert.StartsWith("晶片 ID 0x8786", sio.Value);
        Assert.Contains("未對照名稱表", sio.Value);
        Assert.Equal(0x8786, sio.NumericValue);

        var absent = Assert.Single(facts, f => f.Key == "sio.0x4e");
        Assert.Equal(FactAvailability.NotSupported, absent.Availability);
        Assert.Contains("無裝置回應", absent.UnavailableReason);

        Assert.False(io.ConfigMode, "探測結束後不得留在設定模式"); // 安全性質：兩個埠都試完必須退出
    }

    [Fact]
    public void 探測_回應變動路徑仍保證退出()
    {
        // 探測中途 I/O 消失：不能把晶片留在設定模式。
        var io = new FakeSuperIo { ChipId = 0x8786, VendorId = null, FailAfterEnter = true };
        var facts = SuperIoProbeService.Collect(io, At);

        var sio = Assert.Single(facts, f => f.Key == "sio.0x2e");
        Assert.Equal(FactAvailability.ReadError, sio.Availability);
        Assert.False(io.ConfigMode, "即使中途失敗也必須退出設定模式");
    }

    [Fact]
    public void 探測_埠不可用整組三態()
    {
        var facts = SuperIoProbeService.Collect(new UnavailableIoPortAccess("缺 I/O 埠存取"), At);
        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));
    }

    /// <summary>實作 Super I/O 設定模式協定的假埠：0x87,0x87 進入、索引/資料讀 ID、0xAA 退出。0x2E 掛晶片、0x4E 無裝置。</summary>
    private sealed class FakeSuperIo : IIoPortAccess
    {
        public ushort ChipId = 0x8786;
        public ushort? VendorId = 0x9086;
        public bool FailAfterEnter;
        public bool ConfigMode { get; private set; }

        private uint _indexPort;
        private byte _index;
        private bool _sawFirstMagic;

        public bool Available => true;
        public string? UnavailableReason => null;

        public byte? InByte(uint port)
        {
            if (FailAfterEnter && port == _indexPort + 1) return null; // 探測途中 I/O 消失
            if (!ConfigMode || port != _indexPort + 1) return 0xFF;
            if (_indexPort != 0x2E) return 0xFF;                       // 只有 0x2E 掛晶片
            return _index switch
            {
                SuperIo.RegChipIdHigh => (byte)(ChipId >> 8),
                SuperIo.RegChipIdLow => (byte)ChipId,
                SuperIo.RegVendorIdHigh => VendorId is null ? (byte)0xFF : (byte)(VendorId.Value >> 8),
                SuperIo.RegVendorIdLow => VendorId is null ? (byte)0xFF : (byte)VendorId.Value,
                _ => (byte)0xFF,
            };
        }

        public bool OutByte(uint port, byte value)
        {
            if (port is not (0x2E or 0x4E)) return true; // 資料埠寫入（本服務不會做）
            if (value == SuperIo.ExitCommand) { ConfigMode = false; return true; }
            if (value == 0x87)
            {
                if (_sawFirstMagic) { ConfigMode = true; _indexPort = port; } // 0x87,0x87 → 進入
                _sawFirstMagic = !_sawFirstMagic;
                return true;
            }
            if (value == 0x55) { ConfigMode = true; _indexPort = port; return true; } // SMSC 序列
            if (ConfigMode && _indexPort == port) _index = value;
            return true;
        }
    }
}
