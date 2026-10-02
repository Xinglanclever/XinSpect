using Xunit;

namespace XinSpect.Tests;

public sealed class EvidenceRowTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 讀不到的事實列_顯示原因而非空白或誤導值()
    {
        var fact = new HardwareSnapshotFact
        {
            Key = "chipset.bios_cntl",
            Category = "韌體安全",
            Name = "BIOS 寫入保護",
            Value = "",
            Source = "PCI 0:31.0+0xDC",
            Trust = FactTrustLevel.Unknown,
            MeasuredAtUtc = At,
            Availability = FactAvailability.InsufficientPrivilege,
            UnavailableReason = "缺 ring0：自家驅動未載入",
        };

        var row = EvidenceFactRow.From(fact);

        Assert.Contains("讀不到", row.ValueText);
        Assert.Contains("缺 ring0", row.ValueText);
    }

    [Fact]
    public void 不支援的事實列_顯示不支援標籤()
    {
        var fact = new HardwareSnapshotFact
        {
            Key = "gpu.row_remapper",
            Category = "顯示卡",
            Name = "顯存列重映射",
            Value = "",
            Source = "NVML",
            Trust = FactTrustLevel.Unknown,
            MeasuredAtUtc = At,
            Availability = FactAvailability.NotSupported,
            UnavailableReason = "此卡為 Pascal，無 Ampere+ 列重映射",
        };

        Assert.Contains("不支援", EvidenceFactRow.From(fact).ValueText);
    }

    [Fact]
    public void 變更列_某側讀不到時顯示原因而非破折號()
    {
        var before = new HardwareFact("mchbar.tcl", "記憶體", "tCL", "36", "clk", "MCHBAR",
            FactTrustLevel.Measured, false, At, 36);
        var after = new HardwareFact("mchbar.tcl", "記憶體", "tCL", "", "", "MCHBAR",
            FactTrustLevel.Unknown, false, At, null,
            FactAvailability.InsufficientPrivilege, "缺 ring0");
        var change = new HardwareFactChange
        {
            Key = "mchbar.tcl",
            Kind = SnapshotChangeKind.Changed,
            Previous = before,
            Current = after,
        };

        var row = EvidenceChangeRow.From(change);

        Assert.Equal("36", row.Before);
        Assert.Contains("缺 ring0", row.After);
    }

    [Fact]
    public void 韌體安全列_反映已載入事實且讀不到時顯示原因()
    {
        var svc = new EvidenceLabService();
        svc.LoadChipsetSecurity(new FakePci());

        var rows = svc.FirmwareSecurityRows;

        Assert.Contains(rows, r => r.Name == "BIOS 寫入保護" && r.ValueText.Contains("讀不到"));
    }

    private sealed class FakePci : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "WinRing0 未載入";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }
}
