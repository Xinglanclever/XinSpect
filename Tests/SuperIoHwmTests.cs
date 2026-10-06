using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP17 SuperIO HWM 的契約：ITE／Fintek／NCT67xx 三家族的純解碼（風扇 RPM、溫度、電壓）＋
/// 埠層的設定模式狀態機（進入→讀晶片 ID 定家族→選 HWM LDN→取基址→退出→HWM 讀取）＋
/// 0x2E／0x4E 雙埠遍歷。假 I/O 依驅動行為模擬：
/// ITE 風扇 count 0/0xFFFF＝無效、Fintek 1,500,000/count（無除數）、NCT banked（0x4E 切 bank）；
/// 溫度皆 8-bit 二補數；電壓 **未經主機板分壓校準**要在事實上明說。
/// </summary>
public class SuperIoHwmTests
{
    [Fact]
    public void HWM解碼_ITE風扇RPM公式與無效值釘死()
    {
        // count=450、divisor 2 → 1,350,000 / 900 = 1500 RPM
        Assert.Equal(1500u, XinSpect.SuperIoHwmDecoder.DecodeFanRpm(450));
        Assert.Equal(1500u, XinSpect.SuperIoHwmDecoder.DecodeFanRpm(450, 2));
        Assert.Equal(3000u, XinSpect.SuperIoHwmDecoder.DecodeFanRpm(225));   // divisor 2 預設
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeFanRpm(0));       // 停轉/未接——不回 0 RPM
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeFanRpm(0xFFFF));  // ITE 的 16-bit 無效碼
    }

    [Fact]
    public void HWM解碼_溫度二補數與ITE電壓LSB()
    {
        Assert.Equal(42, XinSpect.SuperIoHwmDecoder.DecodeTemperature(42));
        Assert.Equal(-12, XinSpect.SuperIoHwmDecoder.DecodeTemperature(0xF4)); // 二補數
        Assert.Equal(3200u, XinSpect.SuperIoHwmDecoder.DecodeVoltageMv(200));  // 200×16mV
        Assert.Equal(0u, XinSpect.SuperIoHwmDecoder.DecodeVoltageMv(0));
    }

    [Fact]
    public void HWM解碼_Fintek公式_無除數風扇與8mV電壓()
    {
        // 1,500,000 / count，無除數：count=1500 → 1000 RPM
        Assert.Equal(1000u, XinSpect.SuperIoHwmDecoder.DecodeFintekFanRpm(1500));
        Assert.Equal(1500u, XinSpect.SuperIoHwmDecoder.DecodeFintekFanRpm(1000));
        // FAN_MIN_DETECT 366 RPM 下限：count > 4098（< 366 RPM）視為無訊號
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeFintekFanRpm(0));
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeFintekFanRpm(4099));
        Assert.Equal(366u, XinSpect.SuperIoHwmDecoder.DecodeFintekFanRpm(4098)); // 邊界內：1500000/4098
        // 溫度 8-bit 二補數
        Assert.Equal(45, XinSpect.SuperIoHwmDecoder.DecodeFintekTemperature(45));
        Assert.Equal(-5, XinSpect.SuperIoHwmDecoder.DecodeFintekTemperature(0xFB));
        // 電壓 LSB 8 mV（lm-sensors show_in = in×8；交接寫 12 mV 與驅動不一致，從驅動）
        Assert.Equal(1600u, XinSpect.SuperIoHwmDecoder.DecodeFintekVoltageMv(200));
        Assert.Equal(0u, XinSpect.SuperIoHwmDecoder.DecodeFintekVoltageMv(0));
    }

    [Fact]
    public void HWM解碼_NCT公式_banked風扇與分通道電壓scale()
    {
        // 1,350,000 / (count << divreg)，divreg 預設 0
        Assert.Equal(1500u, XinSpect.SuperIoHwmDecoder.DecodeNctFanRpm(900));   // 1350000/900
        Assert.Equal(1500u, XinSpect.SuperIoHwmDecoder.DecodeNctFanRpm(450, 1)); // 1350000/(450<<1)
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeNctFanRpm(0));
        Assert.Null(XinSpect.SuperIoHwmDecoder.DecodeNctFanRpm(0xFFFF));
        // 溫度 8-bit 二補數（byte 暫存器 0x27 讀法）
        Assert.Equal(38, XinSpect.SuperIoHwmDecoder.DecodeNctTemperature(38));
        Assert.Equal(-9, XinSpect.SuperIoHwmDecoder.DecodeNctTemperature(0xF7));
        // 電壓 scale 0.01 mV/LSB：800→8 mV/LSB（in0）、1600→16 mV/LSB（in2）
        Assert.Equal(1600u, XinSpect.SuperIoHwmDecoder.DecodeNctVoltageMv(200, 800));
        Assert.Equal(3200u, XinSpect.SuperIoHwmDecoder.DecodeNctVoltageMv(200, 1600));
        Assert.Equal(0u, XinSpect.SuperIoHwmDecoder.DecodeNctVoltageMv(0, 800));
    }

    [Fact]
    public void 晶片知識_Fintek與NCT家族分類()
    {
        Assert.Equal(SuperIoFamily.Fintek, SuperIoKnowledge.Family(0x4105));
        Assert.Equal("F71882FG / F71883FG", SuperIoKnowledge.ChipName(0x4105));
        Assert.Equal("F71889", SuperIoKnowledge.ChipName(0x2307));
        Assert.Equal(SuperIoFamily.NuvotonNct, SuperIoKnowledge.Family(0xC562));
        Assert.Equal("NCT6779D", SuperIoKnowledge.ChipName(0xC562));
        // 名稱有收錄但 HWM 佈局不同的 Nuvoton 分支
        Assert.Equal(SuperIoFamily.NuvotonOther, SuperIoKnowledge.Family(0xD592));
        Assert.Equal("NCT6687D-W", SuperIoKnowledge.ChipName(0xD592));
        Assert.Equal(SuperIoFamily.ITE, SuperIoKnowledge.Family(0x8728));
        Assert.Equal(SuperIoFamily.Unlisted, SuperIoKnowledge.Family(0x1234));
    }

    [Fact]
    public void 埠層_ITE_雙埠遍歷與LDN4基址與感測器讀值()
    {
        var io = new FakeSuperIoIo();
        var facts = XinSpect.SuperIoHwmFactsService.Collect(io, At);

        var baseFact = Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.base");
        Assert.Equal(FactAvailability.Present, baseFact.Availability);
        Assert.Contains("0x290", baseFact.Value);
        Assert.Contains("IT8728F", baseFact.Name);
        Assert.True(io.AllPortsExitedConfigMode, "所有路徑都必須退出設定模式——留在設定模式是系統風險");

        var fan = Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.fan0");
        Assert.Equal(1500u, fan.NumericValue);
        var temp = Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.temp0");
        Assert.Contains("42", temp.Value);
        var vin = Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.vin0");
        Assert.Contains("未經主機板校準", vin.Value);

        // 0x4E 沒有裝置：四個事實如實 NotSupported
        Assert.All(facts.Where(f => f.Key.StartsWith("sio.hwm.0x4e.")),
            f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
    }

    [Fact]
    public void 埠層_Fintek_4E通路_公式與LDN4()
    {
        var io = new FakeSuperIoIo();
        io.Ports[0x4E] = FakeSuperIoIo.FintekPort();
        var facts = XinSpect.SuperIoHwmFactsService.Collect(io, At);

        var baseFact = Assert.Single(facts, f => f.Key == "sio.hwm.0x4e.base");
        Assert.Contains("0x2A0", baseFact.Value);
        Assert.Contains("F71882FG", baseFact.Name);

        var fan = Assert.Single(facts, f => f.Key == "sio.hwm.0x4e.fan0");
        Assert.Equal(1000u, fan.NumericValue); // count＝0x05DC＝1500（MSB first）→ 1500000/1500＝1000 RPM
        var temp = Assert.Single(facts, f => f.Key == "sio.hwm.0x4e.temp0");
        Assert.Contains("55", temp.Value);
        var vin = Assert.Single(facts, f => f.Key == "sio.hwm.0x4e.vin0");
        Assert.Contains("1600", vin.Value); // 200×8mV
        Assert.True(io.AllPortsExitedConfigMode);
    }

    [Fact]
    public void 埠層_NCT_banked佈局_LDNB_切bank讀風扇()
    {
        var io = new FakeSuperIoIo();
        io.Ports[0x2E] = FakeSuperIoIo.NctPort();
        var facts = XinSpect.SuperIoHwmFactsService.Collect(io, At);

        var baseFact = Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.base");
        Assert.Contains("NCT6779D", baseFact.Name);
        Assert.Equal(1500u, Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.fan0").NumericValue); // count 900 → 1350000/900
        var temp = Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.temp0");
        Assert.Contains("38", temp.Value);
        var vin = Assert.Single(facts, f => f.Key == "sio.hwm.0x2e.vin0");
        Assert.Contains("1600", vin.Value); // 200×8mV
        Assert.True(io.AllPortsExitedConfigMode);
    }

    [Fact]
    public void 埠層_未收錄家族_名稱可解但感測器不解()
    {
        var io = new FakeSuperIoIo();
        io.Ports[0x2E] = FakeSuperIoIo.NctPort(0xD592); // NCT6687D-W：名稱有、佈局無
        var facts = XinSpect.SuperIoHwmFactsService.Collect(io, At);

        Assert.All(facts.Where(f => f.Key.StartsWith("sio.hwm.0x2e.")),
            f => Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.True(io.AllPortsExitedConfigMode);
    }

    [Fact]
    public void 埠層_Io不可用三態_退出保證在失敗路徑也成立()
    {
        var unavailable = new FakeSuperIoIo { Available = false };
        var facts = XinSpect.SuperIoHwmFactsService.Collect(unavailable, At);
        Assert.Equal(8, facts.Count); // 兩埠 × 4 事實
        Assert.All(facts, f => Assert.Equal(FactAvailability.InsufficientPrivilege, f.Availability));

        var failing = new FakeSuperIoIo { FailWrites = true };
        var failFacts = XinSpect.SuperIoHwmFactsService.Collect(failing, At);
        Assert.All(failFacts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        Assert.True(failing.AllPortsExitedConfigMode);
    }

    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>依 ITE／Fintek／NCT 行為模擬的 I/O：0x2E/0x4E 進入序列、晶片 ID、LDN 選擇、HWM（可 banked）。</summary>
    private sealed class FakeSuperIoIo : IIoPortAccess
    {
        public class PortState
        {
            public ushort ChipId;
            public byte HwmLdn = 0x04;
            public uint HwmBase = 0x290;
            public byte Bank;
            public bool InConfig;
            public bool Exited = true;
            public byte LastIndex;
            public Dictionary<byte, byte> HwmRegisters = new();
            public Dictionary<byte, Dictionary<byte, byte>> BankedRegisters = new();
        }

        public static PortState ItePort() => new()
        {
            ChipId = 0x8728,
            HwmRegisters = new Dictionary<byte, byte>
            {
                [0x29] = 42,                       // TMPIN1＝42°C
                [0x0D] = 0xC2, [0x0E] = 0x01,      // FAN1 count＝0x01C2＝450 → 1500 RPM
                [0x20] = 200,                      // VIN0＝200×16mV＝3200mV
            },
        };

        public static PortState FintekPort() => new()
        {
            ChipId = 0x4105,
            HwmBase = 0x2A0,
            HwmRegisters = new Dictionary<byte, byte>
            {
                [0x70] = 55,                       // TEMP1＝55°C
                [0xA0] = 0x05, [0xA1] = 0xDC,      // FAN1 count＝0x05DC＝1500（MSB first）→ 1000 RPM
                [0x20] = 200,                      // VIN0＝200×8mV＝1600mV
            },
        };

        public static PortState NctPort(ushort chipId = 0xC562) => new()
        {
            ChipId = chipId,
            HwmLdn = 0x0B,
            BankedRegisters = new Dictionary<byte, Dictionary<byte, byte>>
            {
                [0] = new Dictionary<byte, byte> { [0x27] = 38, [0x20] = 200 },  // TEMP1＝38°C；VIN0＝200×8mV
                [6] = new Dictionary<byte, byte> { [0x30] = 0x84, [0x31] = 0x03 }, // FAN1 count＝0x0384＝900（LSB first）→ 1500 RPM
            },
        };

        public bool Available { get; init; } = true;
        public string? UnavailableReason => Available ? null : "假件：I/O 不可用";
        public bool FailWrites { get; init; }

        public Dictionary<uint, PortState> Ports { get; } = new()
        {
            [0x2E] = ItePort(),
        };

        public bool AllPortsExitedConfigMode => Ports.Values.All(p => p.Exited);

        public bool OutByte(uint port, byte value)
        {
            if (FailWrites || !Available) return false;

            // 設定埠（index）與其資料埠
            foreach (var (base_, state) in Ports)
            {
                uint dataPort = base_ + 1;
                if (port == base_)
                {
                    if (!state.InConfig && value is 0x87 or 0x55) { state.InConfig = value == 0x87; state.Exited = false; state.LastIndex = value; return true; }
                    if (state.InConfig && value == SuperIo.ExitCommand) { state.InConfig = false; state.Exited = true; return true; }
                    if (state.InConfig) state.LastIndex = value;
                    return true;
                }
                if (port == dataPort && state.InConfig)
                {
                    if (state.LastIndex == 0x07) { state.Bank = value; } // LDN 選擇（借用 Bank 暫存 LDN）
                    return true;
                }

                // HWM 通路：base+5（index）／base+6（data）
                if (port == state.HwmBase + 5) { state.LastIndex = value; return true; }
                if (port == state.HwmBase + 6)
                {
                    if (state.LastIndex == 0x4E) state.Bank = value; // NCT bank 選擇
                    return true;
                }
            }
            return true;
        }

        public byte? InByte(uint port)
        {
            if (!Available) return null;
            foreach (var (base_, state) in Ports)
            {
                uint dataPort = base_ + 1;
                if (port == dataPort && state.InConfig)
                {
                    byte ldn = state.Bank; // LDN 選擇暫存在 Bank
                    return state.LastIndex switch
                    {
                        0x20 => (byte)(state.ChipId >> 8),
                        0x21 => (byte)(state.ChipId & 0xFF),
                        0x60 => ldn == state.HwmLdn ? (byte)(state.HwmBase >> 8) : (byte)0xFF,
                        0x61 => ldn == state.HwmLdn ? (byte)(state.HwmBase & 0xF8) : (byte)0xFF,
                        _ => (byte)0xFF,
                    };
                }
                if (port == state.HwmBase + 6)
                {
                    if (state.BankedRegisters.TryGetValue(state.Bank, out var bank) &&
                        bank.TryGetValue(state.LastIndex, out byte v)) return v;
                    if (state.HwmRegisters.TryGetValue(state.LastIndex, out byte v2)) return v2;
                    return 0xFF;
                }
            }
            return 0xFF;
        }

        public bool OutWord(uint port, ushort value) => false;
        public byte? InWord(uint port) => null;
    }
}
