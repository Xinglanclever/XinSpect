using Xunit;
namespace XinSpect.Tests;

// Synthetic snapshots exercise routing logic; they are not real hardware fixtures.
public class PcieParentResolverTests
{
    private static PcieTopologyFunction Endpoint(byte bus = 3, byte device = 0, byte function = 0)
        => new(new(bus, device, function), 0u, null, 0, 4, 16, 4, true);
    private static PcieTopologyFunction Bridge(byte bus, byte device, byte secondary, byte subordinate, int port = 6, int speed = 3, int width = 8)
        => new(new(bus, device, 0), 1u << 16, (uint)(bus | secondary << 8 | subordinate << 16), port, speed, width, 4, true);
    [Fact]
    public void MissingParentAndOtherFunctionsDoNotBecomeUpstream()
    {
        var child = Endpoint(device: 5);
        var sibling = Endpoint(device: 5, function: 1) with { PortType = 4 };
        var smallerDevice = Endpoint(device: 2) with { PortType = 6 };
        var result = PcieParentResolver.Resolve(child, [child, sibling, smallerDevice]);
        Assert.Equal((0, 0), (result.Speed, result.Width));
        Assert.Contains("未知", result.Evidence);
    }
    [Fact]
    public void ExactSecondaryBusFindsDirectParentNotAncestorRange()
    {
        var child = Endpoint();
        var root = Bridge(0, 1, 1, 20, 4, 5, 16);
        var direct = Bridge(2, 6, 3, 8);
        var result = PcieParentResolver.Resolve(child, [child, root, direct]);
        Assert.Equal((3, 8), (result.Speed, result.Width));
        Assert.Contains("02:06.0", result.Evidence);
        Assert.Contains("segment 未知", result.Evidence);
    }
    [Fact]
    public void MoreThanOneDirectBridgeIsAmbiguous()
    {
        var child = Endpoint();
        var result = PcieParentResolver.Resolve(child, [child, Bridge(0, 1, 3, 8), Bridge(1, 2, 3, 8)]);
        Assert.Equal(0, result.Speed);
    }
    [Fact]
    public void HeaderOrBusRegisterReadErrorsReturnUnknown()
    {
        var child = Endpoint();
        var bridge = Bridge(2, 1, 3, 8);
        foreach (var bad in new[] { bridge with { Header = null }, bridge with { Header = uint.MaxValue },
            bridge with { BusNumbers = null }, bridge with { BusNumbers = uint.MaxValue },
            bridge with { BusNumbers = 0 }, bridge with { MaximumSpeed = 0 } })
            Assert.Equal(0, PcieParentResolver.Resolve(child, [child, bad]).Speed);
    }
    [Fact]
    public void InvalidRangesAndWrongPrimaryAreRejected()
    {
        var child = Endpoint();
        var bridge = Bridge(2, 1, 3, 8);
        foreach (uint buses in new uint[] { 1 | 3 << 8 | 8 << 16, 2 | 3 << 8 | 2 << 16, 2 | 1 << 8 | 8 << 16 })
            Assert.Equal(0, PcieParentResolver.Resolve(child, [child, bridge with { BusNumbers = buses }]).Speed);
    }
    [Fact]
    public void DuplicateBdfAndUnsupportedPortRolesStayUnknown()
    {
        var child = Endpoint();
        var bridge = Bridge(2, 1, 3, 8);
        Assert.Equal(0, PcieParentResolver.Resolve(child, [child, bridge, bridge]).Speed);
        Assert.Equal(0, PcieParentResolver.Resolve(child, [child, bridge with { PortType = 5 }]).Speed);
        var downstream = child with { PortType = 6 };
        Assert.Equal(0, PcieParentResolver.Resolve(downstream, [downstream, bridge]).Speed);
    }
    [Fact]
    public void ProductionGapUsesOnlyResolvedParentEvidence()
    {
        var child = Endpoint();
        var row = new PcieLinkRow("endpoint", child.Address.Label, "endpoint", 3, 16, 4, 16, "", 1);
        var unknown = Assert.Single(PcieLinkService.ApplyTopology([row], [child]));
        Assert.NotEqual("上游上限", unknown.GapKindText);
        Assert.Contains("上游未知", unknown.GapEvidence);
        var verified = Assert.Single(PcieLinkService.ApplyTopology([row], [child, Bridge(2, 1, 3, 8, width: 16)]));
        Assert.Equal("上游上限", verified.GapKindText);
        Assert.Contains("02:01.0", verified.GapEvidence);
    }
    [Theory]
    [InlineData(3)]
    [InlineData(63)]
    [InlineData(0)]
    public void ReservedParentWidthCannotEstablishLimit(int width)
    {
        var child = Endpoint();
        Assert.Equal(0, PcieParentResolver.Resolve(child, [child, Bridge(2, 1, 3, 8, width: width)]).Speed);
    }
    [Theory]
    [InlineData(4, 8)]
    [InlineData(3, 16)]
    public void ChildCurrentAboveParentCapabilityRejectsAtProductionGap(int speed, int width)
    {
        var child = Endpoint();
        var row = new PcieLinkRow("endpoint", child.Address.Label, "endpoint", speed, width, 5, 16, "", 1);
        var result = Assert.Single(PcieLinkService.ApplyTopology([row], [child, Bridge(2, 1, 3, 8)]));
        Assert.Equal("未判定", result.GapKindText);
        Assert.Contains("拒絕歸因", result.GapEvidence);
    }
    [Theory]
    [InlineData(7, 16)]
    [InlineData(4, 3)]
    [InlineData(4, 63)]
    public void ReservedChildFieldsRejectAtProductionGap(int speed, int width)
    {
        var child = Endpoint();
        var row = new PcieLinkRow("endpoint", child.Address.Label, "endpoint", speed, width, 5, 16, "", 1);
        var result = Assert.Single(PcieLinkService.ApplyTopology([row], [child, Bridge(2, 1, 3, 8, speed: 5, width: 16)]));
        Assert.Equal("未判定", result.GapKindText);
        Assert.Contains("保留或互相矛盾", result.GapEvidence);
    }
    [Theory]
    [InlineData(0x00010010u, false)]
    [InlineData(0x00020010u, true)]
    [InlineData(0x00620010u, true)]
    public void RealCapabilityDwordUsesHighWordRevision(uint dword, bool expected)
        => Assert.Equal(expected, PcieParentResolver.HasLinkControl2FromCapabilityDword(dword));
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void EndpointAndUpstreamTargetSpeedCannotImplyOperationalLimit(int portType)
    {
        var child = Endpoint() with { PortType = portType, TargetSpeed = 1, HasControl2 = true };
        var row = new PcieLinkRow("endpoint", child.Address.Label, "endpoint", 2, 16, 4, 16, "", 1);
        var result = Assert.Single(PcieLinkService.ApplyTopology([row], [child]));
        Assert.DoesNotContain("協商上限", result.GapEvidence);
        Assert.DoesNotContain("BIOS", result.GapEvidence);
        Assert.Equal(1, child.TargetSpeed); // Raw compliance-test field remains in snapshot.
    }
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void DownstreamPortOwnTargetSpeedRetainsOperationalEvidence(int portType)
    {
        var child = Endpoint() with { PortType = portType, TargetSpeed = 1, HasControl2 = true };
        var row = new PcieLinkRow("downstream", child.Address.Label, "port", 2, 16, 4, 16, "", 1);
        var result = Assert.Single(PcieLinkService.ApplyTopology([row], [child]));
        Assert.Contains("協商上限", result.GapEvidence);
    }
}
