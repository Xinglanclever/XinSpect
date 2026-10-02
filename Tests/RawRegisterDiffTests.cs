using Xunit;

namespace XinSpect.Tests;

public sealed class RawRegisterDiffTests
{
    private static RawRegisterRegion Region(string source, byte[] bytes, byte[]? mask = null)
        => new() { Source = source, Bytes = bytes, VolatilityMask = mask };

    private static RawRegisterRegion Unavailable(string source, FactAvailability a, string reason)
        => new() { Source = source, Bytes = null, Availability = a, UnavailableReason = reason };

    [Fact]
    public void 相同位元組_判Unchanged()
    {
        var d = RawRegisterSnapshotService.Diff(
            [Region("msr:0x198", [1, 2, 3, 4])],
            [Region("msr:0x198", [1, 2, 3, 4])]);
        Assert.Equal(RawRegionChangeKind.Unchanged, Assert.Single(d.Regions).Kind);
    }

    [Fact]
    public void 非遮罩位元組不同_判Changed並列出偏移()
    {
        var d = RawRegisterSnapshotService.Diff(
            [Region("pcicfg:00:1f.0", [1, 2, 3, 4])],
            [Region("pcicfg:00:1f.0", [1, 9, 3, 4])]);
        var c = Assert.Single(d.Regions);
        Assert.Equal(RawRegionChangeKind.Changed, c.Kind);
        Assert.Equal(new[] { 1 }, c.ChangedOffsets);
    }

    [Fact]
    public void 只有揮發位元組不同_遮罩抑制為Unchanged()
    {
        var d = RawRegisterSnapshotService.Diff(
            [Region("msr:0x10", [10, 2, 3, 4], mask: [1, 0, 0, 0])],
            [Region("msr:0x10", [99, 2, 3, 4], mask: [1, 0, 0, 0])]);
        Assert.Equal(RawRegionChangeKind.Unchanged, Assert.Single(d.Regions).Kind);
    }

    [Fact]
    public void 揮發與非揮發都變_只列非揮發偏移且判Changed()
    {
        var d = RawRegisterSnapshotService.Diff(
            [Region("msr:0x10", [10, 2, 3, 4], mask: [1, 0, 0, 0])],
            [Region("msr:0x10", [99, 2, 7, 4], mask: [1, 0, 0, 0])]);
        var c = Assert.Single(d.Regions);
        Assert.Equal(RawRegionChangeKind.Changed, c.Kind);
        Assert.Equal(new[] { 2 }, c.ChangedOffsets);
    }

    [Fact]
    public void 區段從可用變不可用_判AvailabilityChanged()
    {
        var d = RawRegisterSnapshotService.Diff(
            [Region("mchbar:tcl", [36])],
            [Unavailable("mchbar:tcl", FactAvailability.InsufficientPrivilege, "缺 ring0")]);
        var c = Assert.Single(d.Regions);
        Assert.Equal(RawRegionChangeKind.AvailabilityChanged, c.Kind);
        Assert.Equal(FactAvailability.Present, c.PreviousAvailability);
        Assert.Equal(FactAvailability.InsufficientPrivilege, c.CurrentAvailability);
    }

    [Fact]
    public void 新增與移除區段()
    {
        var d = RawRegisterSnapshotService.Diff([Region("a", [1])], [Region("b", [2])]);
        Assert.Equal(RawRegionChangeKind.Removed, d.Regions.Single(r => r.Source == "a").Kind);
        Assert.Equal(RawRegionChangeKind.Added, d.Regions.Single(r => r.Source == "b").Kind);
    }
}
