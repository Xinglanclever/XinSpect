using Xunit;
namespace XinSpect.Tests;
public class PcieAnalysisTests
{
    private static PciRawDevice Pnp(string instance) => new() { DeviceId = @"PCI\VEN_1234&DEV_5678\" + instance, Name = "same card" };
    private static PcieLinkRow Link(string location, int width) => new("model name", location, "endpoint", 4, width, 4, 16, "", 0, ven: 0x1234, dev: 0x5678);
    [Fact]
    public void IdenticalCardsKeepIndependentLinkReadingsWithoutModelJoin()
    {
        var rows = PcieAnalysisService.AnalyseSources([Pnp("A"), Pnp("B")], [Link("01:00.0", 8), Link("02:00.0", 16)]);
        Assert.Equal(4, rows.Count);
        var pnp = rows.Where(r => r.RawInstanceId.StartsWith("PCI\\")).ToArray();
        Assert.Equal(2, pnp.Length);
        Assert.All(pnp, r => Assert.Equal(0, r.NegotiatedWidth));
        var links = rows.Where(r => r.RawLocation.Length > 0).ToArray();
        Assert.Equal(new[] { 8, 16 }, links.Select(r => r.NegotiatedWidth));
        Assert.All(links, r => { Assert.Null(r.Address); Assert.Contains("segment", r.LocationLabel); Assert.NotEmpty(r.LinkSource); });
        Assert.False(links[0].IsSplitDetected);
        Assert.Equal("未驗證", links[0].SplitDescription);
    }
    [Fact]
    public void OpaqueOrBusLookingInstanceDoesNotBecomeBusZeroEvidence()
    {
        var row = Assert.Single(PcieAnalysisService.AnalyseSources([Pnp("BUS_01&DEV_00&FUNC_0")], []));
        Assert.Null(row.Address);
        Assert.Contains("未知", row.LocationLabel);
        Assert.Equal(0, row.NegotiatedGen);
        Assert.NotEmpty(row.RawInstanceId);
        Assert.Equal("未知（未驗證拓撲）", new PcieLaneAllocation { Source = LaneSource.Unknown }.SourceLabel);
    }
    [Fact]
    public void DuplicatedPnpIdentityDoesNotDuplicateDeviceRecord()
        => Assert.Single(PcieAnalysisService.AnalyseSources([Pnp("A"), Pnp("a")], []));
    [Fact]
    public void AmbiguousOrInvalidBackendLocationsAreRejected()
    {
        var rows = PcieAnalysisService.AnalyseSources([], [Link("01:00.0", 8), Link("01:00.0", 16), Link("00:20.0", 4), Link("00:00.8", 4)]);
        Assert.Empty(rows);
    }
    [Theory]
    [InlineData("")]
    [InlineData("Unknown CPU")]
    [InlineData("Intel Xeon")]
    [InlineData("AMD Ryzen 9950X")]
    public void CpuNamesCannotEstablishPlatformLaneBudget(string cpu)
    {
        Assert.Equal((0, 0), PcieAnalysisService.EstimatePlatformLanes(cpu));
        var service = new PcieAnalysisService();
        Assert.False(service.PlatformLaneCountsKnown);
        Assert.Equal("未知", service.CpuLaneCountLabel);
        Assert.Equal("未知", service.ChipsetLaneCountLabel);
    }
    [Theory]
    [InlineData(1, 250.0)]
    [InlineData(2, 500.0)]
    [InlineData(3, 984.6153846153846)]
    [InlineData(4, 1969.2307692307693)]
    [InlineData(5, 3938.4615384615386)]
    public void VerifiedGenerationsUseEncodingAdjustedSingleDirectionRate(int gen, double expected)
    {
        var reference = Assert.Single(PcieAnalysisService.GenReferences, row => row.Gen == gen);
        Assert.Equal(expected, PcieAnalysisService.CalculateBandwidthMBps(gen, 1), 8);
        Assert.Equal(expected * 16, PcieAnalysisService.CalculateBandwidthMBps(gen, 16), 7);
        Assert.Equal(expected, reference.TheoreticalSingleLaneMBps, 8);
        Assert.Equal($"{expected:N2}", reference.TheoreticalSingleLaneLabel);
    }
    [Fact]
    public void GenSixKeepsRawRateWithoutInventedPayloadBandwidth()
    {
        var reference = Assert.Single(PcieAnalysisService.GenReferences, row => row.Gen == 6);
        Assert.Equal(64.0, reference.TransferRateGTs);
        Assert.False(reference.BandwidthCalculationSupported);
        Assert.Equal(0, PcieAnalysisService.CalculateBandwidthMBps(6, 16));
        Assert.Equal("未校核", reference.TheoreticalSingleLaneLabel);
        var slot = PcieAnalysisService.AnalyseDevice(new PciRawDevice {
            NegotiatedSpeedCode = 6, NegotiatedWidth = 16, CapableSpeedCode = 6, CapableWidth = 16 });
        Assert.Equal("未知（未校核）", slot.NegotiatedBwLabel);
        Assert.Equal("未知（未校核）", slot.CapableBwLabel);
    }
    [Fact]
    public void ReferenceAndDeviceColumnsBindTheirDistinctBandwidthProperties()
    {
        var folder = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !System.IO.File.Exists(System.IO.Path.Combine(folder.FullName, "Views", "PcieAnalysisView.xaml")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        var xml = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(folder!.FullName, "Views", "PcieAnalysisView.xaml"));
        var bindings = xml.Descendants().SelectMany(element => element.Attributes("Binding")).Select(attribute => attribute.Value).ToArray();
        Assert.Contains("{Binding TheoreticalSingleLaneLabel}", bindings);
        Assert.Contains("{Binding NegotiatedBwLabel}", bindings);
        Assert.Contains("{Binding CapableBwLabel}", bindings);
        Assert.DoesNotContain(xml.Descendants(), element => (string?)element.Attribute("Path") == "TransferRateGTs");
        var linkXml = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(folder.FullName, "Views", "PcieLinkView.xaml"));
        var trigger = Assert.Single(linkXml.Descendants(), element =>
            (string?)element.Attribute("Binding") == "{Binding BandwidthEstimateAvailable}");
        Assert.Contains(trigger.Elements(), element => (string?)element.Attribute("TargetName") == "BandwidthBar"
            && (string?)element.Attribute("Value") == "Collapsed");
        Assert.Contains(trigger.Elements(), element => (string?)element.Attribute("TargetName") == "BandwidthUnknown"
            && (string?)element.Attribute("Value") == "Visible");
    }
    [Fact]
    public void ProductionLinkRowsCarrySpeedCodesWithoutSecondRateConversion()
    {
        var row = Assert.Single(PcieAnalysisService.AnalyseSources([], [Link("03:00.0", 16)]));
        Assert.Equal(4, row.NegotiatedGen);
        Assert.Equal(4, row.CapableGen);
        Assert.Equal(31507.69230769231, row.NegotiatedBandwidthMBps, 7);
        Assert.Equal("Gen4 x16", row.NegotiatedLabel);
        var gen1 = new PcieLinkRow("", "04:00.0", "endpoint", 1, 1, 1, 1, "", 0);
        Assert.Equal(250, Assert.Single(PcieAnalysisService.AnalyseSources([], [gen1])).NegotiatedBandwidthMBps);
    }
}
