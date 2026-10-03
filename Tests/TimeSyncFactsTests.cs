using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP25 時間同步互校的契約：HPET 存在（ACPI HPET 表）、ACPI PM timer 區塊（FADT PM_TMR_BLK@76）
/// 都是「硬體有沒有這個計時器」的事實；QPC 與系統時鐘的漂移是**行為量測**（量的是行為不是
/// 硬體映射——Windows 用哪個計時器驅動 QPC 是 OS 決策，本工具不猜）。純函式釘值、ACPI 與
/// 漂移量測皆可注入。
/// </summary>
public class TimeSyncFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>FADT 假表：只用到表頭＋offset 76 的 PM_TMR_BLK（u32 LE）。校驗和要照規格補。</summary>
    private static byte[] Fadt(uint pmTimerBlock)
    {
        var fadt = new byte[128];
        byte[] sig = [(byte)'F', (byte)'A', (byte)'D', (byte)'T'];
        sig.CopyTo(fadt, 0);
        fadt[4] = 128; // Length（僅標示）
        fadt[76] = (byte)pmTimerBlock;
        fadt[77] = (byte)(pmTimerBlock >> 8);
        fadt[78] = (byte)(pmTimerBlock >> 16);
        fadt[79] = (byte)(pmTimerBlock >> 24);
        FixChecksum(fadt);
        return fadt;
    }

    /// <summary>補表頭校驗和（全表位元組和 mod 256 = 0）。</summary>
    private static void FixChecksum(byte[] table)
    {
        table[9] = 0;
        table[9] = (byte)((256 - table.Aggregate(0, (acc, b) => acc + b) % 256) % 256);
    }

    private sealed class FakeAcpi : IAcpiTableSource
    {
        private readonly byte[][] _tables;
        public FakeAcpi(params byte[][] tables) { _tables = tables; }
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => _tables;
    }

    [Fact]
    public void 時間互校_HPET表與PMTimer區塊與漂移逐項釘值()
    {
        byte[] hpet = new byte[40];
        byte[] sig = [(byte)'H', (byte)'P', (byte)'E', (byte)'T'];
        sig.CopyTo(hpet, 0);
        hpet[4] = 40;
        FixChecksum(hpet);
        var facts = XinSpect.TimeSyncFactsService.Collect(
            At, new FakeAcpi(hpet, Fadt(0x408)),
            driftProbe: () => (QpcElapsedUs: 200_000.0, WallElapsedUs: 200_010.0));

        var hpetFact = Assert.Single(facts, f => f.Key == "time.hpet");
        Assert.Equal(FactAvailability.Present, hpetFact.Availability);
        Assert.Contains("存在", hpetFact.Value);

        var pm = Assert.Single(facts, f => f.Key == "time.pm_timer");
        Assert.Contains("0x408", pm.Value);
        Assert.Contains("1032", pm.Value);   // 十進位並列可稽核

        var drift = Assert.Single(facts, f => f.Key == "time.drift.ppm");
        Assert.Equal(FactAvailability.Present, drift.Availability);
        // (200010 - 200000) / 200000 × 1e6 = 50 ppm
        Assert.Equal(50.0, drift.NumericValue);
        Assert.Contains("ppm", drift.Unit);
    }

    [Fact]
    public void 時間互校_無HPET無PMTimer如實標_漂移量測不可用三態()
    {
        var facts = XinSpect.TimeSyncFactsService.Collect(
            At, new FakeAcpi(), driftProbe: () => null);

        var hpetFact = Assert.Single(facts, f => f.Key == "time.hpet");
        Assert.Equal(FactAvailability.NotSupported, hpetFact.Availability);
        Assert.Contains("沒有", hpetFact.UnavailableReason);

        var pm = Assert.Single(facts, f => f.Key == "time.pm_timer");
        Assert.Equal(FactAvailability.ReadError, pm.Availability);
        Assert.Contains("FADT", pm.UnavailableReason);

        var drift = Assert.Single(facts, f => f.Key == "time.drift.ppm");
        Assert.Equal(FactAvailability.ReadError, drift.Availability);
    }

    [Fact]
    public void PMTimer_短FADT如實拒解()
        => Assert.Null(XinSpect.TimeSyncFactsService.DecodePmTimerBlock(new byte[60]));
}
