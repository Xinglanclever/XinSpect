using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP17 SuperIO HWM 的契約：ITE 家族環境控制器的純解碼（風扇 RPM、溫度、電壓）＋
/// 埠層的設定模式狀態機（進入→選 LDN4→取基址→退出→HWM 讀取）。
/// 假 I/O 依 ITE 行為模擬：fan count 0/255＝無效（停轉或未接，不是 0 RPM）、
/// 溫度 8-bit 二補數、電壓 LSB 16mV（**未經主機板分壓校準**要在事實上明說）。
/// </summary>
public class SuperIoHwmTests
{
    [Fact]
    public void HWM解碼_風扇RPM公式與無效值釘死()
    {
        // count=450、divisor 2 → 1,350,000 / 900 = 1500 RPM
        Assert.Equal(1500u, XinSpect.SuperIoHwmDecoder.DecodeFanRpm(450));
        Assert.Equal(1500u, XinSpect.SuperIoHwmDecoder.DecodeFanRpm(450, 2));
        Assert.Equal(3000u, XinSpect.SuperIoHwmDecoder.DecodeFanRpm(225));   // divisor 2 預設
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeFanRpm(0));       // 停轉/未接——不回 0 RPM
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeFanRpm(0xFFFF));  // ITE 的 16-bit 無效碼
    }

    [Fact]
    public void HWM解碼_溫度二補數與電壓LSB()
    {
        Assert.Equal(42, XinSpect.SuperIoHwmDecoder.DecodeTemperature(42));
        Assert.Equal(-12, XinSpect.SuperIoHwmDecoder.DecodeTemperature(0xF4)); // 二補數
        Assert.Equal(3200u, XinSpect.SuperIoHwmDecoder.DecodeVoltageMv(200));  // 200×16mV
        Assert.Equal(0u, XinSpect.SuperIoHwmDecoder.DecodeVoltageMv(0));
    }

    [Fact]
    public void 埠層_設定模式狀態機與LDN4基址與感測器讀值()
    {
        var io = new FakeSuperIoIo();
        var facts = XinSpect.SuperIoHwmFactsService.Collect(io, At);

        var baseFact = Assert.Single(facts, f => f.Key == "sio.hwm.base");
        Assert.Equal(FactAvailability.Present, baseFact.Availability);
        Assert.Contains("0x290", baseFact.Value);
        Assert.True(io.ExitedConfigMode, "所有路徑都必須退出設定模式——留在設定模式是系統風險");

        var fan = Assert.Single(facts, f => f.Key == "sio.hwm.fan0");
        Assert.Equal(1500u, fan.NumericValue);
        var temp = Assert.Single(facts, f => f.Key == "sio.hwm.temp0");
        Assert.Contains("42", temp.Value);
        var vin = Assert.Single(facts, f => f.Key == "sio.hwm.vin0");
        Assert.Contains("未經主機板校準", vin.Value);
    }

    [Fact]
    public void 埠層_Io不可用三態_退出保證在失敗路徑也成立()
    {
        var unavailable = new FakeSuperIoIo { Available = false };
        var facts = XinSpect.SuperIoHwmFactsService.Collect(unavailable, At);
        Assert.All(facts, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));

        var failing = new FakeSuperIoIo { FailWrites = true };
        var failFacts = XinSpect.SuperIoHwmFactsService.Collect(failing, At);
        Assert.All(failFacts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.True(failing.ExitedConfigMode);
    }

    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>依 ITE 行為模擬的 I/O：0x2E 進入序列 0x87,0x87、暫存器選擇後 InByte 回資料。</summary>
    private sealed class FakeSuperIoIo : IIoPortAccess
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason => Available ? null : "假件：I/O 不可用";
        public bool FailWrites { get; init; }

        public bool ExitedConfigMode { get; private set; } = true;
        private bool _inConfig;
        private byte _lastIndex;
        private byte _ldn;
        private readonly Dictionary<byte, byte> _hwmRegisters = new()
        {
            [0x29] = 42,          // TMPIN1＝42°C
            [0x0D] = 0xC2, [0x0E] = 0x01, // FAN1 count＝0x01C2＝450 → 1500 RPM
            [0x20] = 200,         // VIN0＝200×16mV＝3200mV
        };

        public bool OutByte(uint port, byte value)
        {
            if (FailWrites || !Available) return false;
            if (port == 0x2E)
            {
                if (!_inConfig && value is 0x87 or 0x55) { _inConfig = value == 0x87; ExitedConfigMode = false; return true; }
                if (_inConfig && value == SuperIo.ExitCommand) { _inConfig = false; ExitedConfigMode = true; return true; }
                if (_inConfig) _lastIndex = value;
                return true;
            }
            if (port == 0x2F && _inConfig && _lastIndex == 0x07) { _ldn = value; return true; } // LDN 選擇
            if (port == 0x295) { _lastIndex = value; return true; } // HWM index 埠（base+5）
            return true; // 其他 HWM 資料埠寫入
        }

        public byte? InByte(uint port)
        {
            if (!Available) return null;
            if (port == 0x2F && _inConfig)
            {
                // 設定模式讀取：LDN4 的基址暫存器 0x60/0x61＝0x02/0x90（base 0x290）
                if (_lastIndex == 0x60) return _ldn == 0x04 ? (byte)0x02 : (byte)0xFF;
                if (_lastIndex == 0x61) return _ldn == 0x04 ? (byte)0x90 : (byte)0xFF;
                return 0xFF;
            }
            if (port == 0x296 && _hwmRegisters.TryGetValue(_lastIndex, out byte v)) return v;
            return 0xFF;
        }

        public bool OutWord(uint port, ushort value) => false;
        public byte? InWord(uint port) => null;
    }
}
