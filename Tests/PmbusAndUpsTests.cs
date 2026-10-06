using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// R7 的契約：PMBus LINEAR11／LINEAR16 金標向量（PMBus Rev 1.3 §8.4/8.5 編碼規則手算）、
/// 掃描的唯讀安全邊界（MFR_ID 偵測、NAK 跳過）、無裝置時整組 NotApplicable；
/// UPS 在 WMI 沒有實例時 NotApplicable、有實例時三事實。
/// </summary>
public class PmbusAndUpsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    // ── PMBus 純解碼 ──

    [Fact]
    public void LINEAR11金標_指數與尾數的補數展開()
    {
        // exp＝-3（0b11100）、mantissa＝1000 → 1000 × 2^-3 ＝ 125.0
        Assert.Equal(125.0, PmbusDecoder.DecodeLinear11((ushort)((0b11101 << 11) | 1000))!.Value, 5);  // exp -3＝0b11101
        // exp＝0、mantissa＝-1（0b11111111111）→ -1.0
        Assert.Equal(-1.0, PmbusDecoder.DecodeLinear11((ushort)(0b00000 << 11 | 0x7FF))!.Value, 5);
        // exp＝-9、mantissa＝128 → 128 × 2^-9 ＝ 0.25
        Assert.Equal(0.25, PmbusDecoder.DecodeLinear11((ushort)((0b10111 << 11) | 128))!.Value, 5);
    }

    [Fact]
    public void LINEAR16與VOUT_MODE金標()
    {
        // VOUT_MODE：bits[7:5]＝模式（000＝LINEAR16）、bits[4:0]＝指數。-9 → 0x17；尾數 2000 × 2^-9 ＝ 3.90625 V
        Assert.Equal(-9, PmbusDecoder.DecodeVoutModeExponent(0x17));
        Assert.Equal(0, PmbusDecoder.DecodeVoutModeExponent(0x00));
        Assert.Null(PmbusDecoder.DecodeVoutModeExponent(0x37));      // 模式 001＝Direct——不解
        Assert.Equal(3.90625, PmbusDecoder.DecodeLinear16(2000, -9)!.Value, 5);
    }

    // ── 掃描層（假傳輸）──

    private sealed class FakeTransport(byte? mfrAddr, IReadOnlyDictionary<byte, ushort>? words = null) : PsuPmbusFactsService.ITransport
    {
        public Dictionary<byte, ushort> Words { get; } = words is Dictionary<byte, ushort> d ? d : new();
        public byte? ReadByte(byte slave7, byte command) =>
            command == PmbusDecoder.CmdMfrId && slave7 == mfrAddr ? mfrAddr is { } ? (byte)0x43 : null : null;
        public ushort? ReadWord(byte slave7, byte command) =>
            slave7 == mfrAddr && Words.TryGetValue(command, out ushort v) ? v : null;
    }

    [Fact]
    public void 沒有PMBus裝置時整組NotApplicable()
    {
        var facts = PsuPmbusFactsService.Collect(At, new FakeTransport(null));
        Assert.Equal(4, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    [Fact]
    public void 有裝置時LINE11電軌解出來()
    {
        // READ_VIN＝exp -3、mantissa 928 → 116.0 V（11-bit 尾數上限 1023）；READ_POUT＝exp 0、mantissa 300 → 300 W
        var transport = new FakeTransport(0x40, new Dictionary<byte, ushort>
        {
            [PmbusDecoder.CmdReadVin] = (ushort)((0b11101 << 11) | 928),
            [PmbusDecoder.CmdReadPout] = (ushort)((0b00000 << 11) | 300),
        });
        var facts = PsuPmbusFactsService.Collect(At, transport);

        var vin = Assert.Single(facts, f => f.Key == "psu.pmbus.vin");
        Assert.Equal(FactAvailability.Present, vin.Availability);
        Assert.Contains("116", vin.Value);
        Assert.Contains("未在本機驗證", vin.Source);

        var pout = Assert.Single(facts, f => f.Key == "psu.pmbus.pout");
        Assert.Contains("300", pout.Value);

        // 未回應的命令如實 ReadError
        var temp = Assert.Single(facts, f => f.Key == "psu.pmbus.temp1");
        Assert.Equal(FactAvailability.ReadError, temp.Availability);
    }

    // ── UPS ──

    [Fact]
    public void 沒有電池實例時UPS整組NotApplicable()
    {
        var facts = UpsFactsService.Collect(At, () => []);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        // 防禦：probe 回 null（WMI 不可用）與空清單同樣整組 NotApplicable
        Assert.All(UpsFactsService.Collect(At, () => null!), f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    [Fact]
    public void 有UPS實例時三事實()
    {
        var facts = UpsFactsService.Collect(At,
            () => [new UpsFactsService.UpsBatterySnapshot(87, 3, 25)]);
        Assert.Equal(87, Assert.Single(facts, f => f.Key == "ups.battery.percent").NumericValue);
        Assert.Contains("充飽", Assert.Single(facts, f => f.Key == "ups.status").Value);
        Assert.Contains("25 分鐘", Assert.Single(facts, f => f.Key == "ups.runtime_minutes").Value);
    }

    [Fact]
    public void BatteryStatus代碼解碼與未收錄()
    {
        Assert.Equal("放電中（未接市電）", UpsFactsService.DescribeBatteryStatus(1));
        Assert.Equal("BatteryStatus 0x2A（未收錄）", UpsFactsService.DescribeBatteryStatus(0x2A));
    }
}
