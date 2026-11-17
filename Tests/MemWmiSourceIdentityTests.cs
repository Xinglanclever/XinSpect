using Xunit;

namespace XinSpect.Tests;

// R384 offline regressions: only existing public members are referenced, so this file also
// compiles against the original service for the root's original-source negative control.
public sealed class MemWmiSourceIdentityTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private static WmiPhysicalMemoryRow Wmi(string? serial = "BBBB0002", string? part = "SAME-PART", long? bytes = 17179869184L)
        => new("DIMM_B1", "fixture", part, serial, bytes, 3200);
    private static SpdAuditSourceInput Source(SpdAuditSourceKind kind, string? serial = "BBBB0002",
        string? part = "SAME-PART", int? mib = 16384)
        => new(kind, "fixture", null, "fixture", part, serial, mib);

    [Theory]
    [InlineData(16384, 0)]
    [InlineData(8192, 1)]
    public void AddingSecondSourceCannotEraseMatchedModuleOrNativeCapacityConflict(int nativeMiB, int conflicts)
    {
        var n = Source(SpdAuditSourceKind.NativeSpd, mib: nativeMiB);
        var s = Source(SpdAuditSourceKind.Smbios);
        var before = MemWmiCrossFactsService.Cross([Wmi()], [n], null);
        var after = MemWmiCrossFactsService.Cross([Wmi()], [n], [s]);
        Assert.Equal(1, before.ComparedModules);
        Assert.Equal(conflicts, before.CapacityConflicts);
        Assert.Equal(1, after.ComparedModules);
        Assert.Equal(conflicts, after.CapacityConflicts);
        Assert.Equal(0, after.SerialConflicts);
    }

    [Fact]
    public void BothSourcesDisagreeingCountOncePerModuleAndRetainEachSourceCounters()
    {
        var n = Source(SpdAuditSourceKind.NativeSpd, part: "NATIVE-PART", mib: 8192);
        var s = Source(SpdAuditSourceKind.Smbios, part: "SMBIOS-PART", mib: 32768);
        var state = MemWmiCrossFactsService.Cross([Wmi()], [n], [s]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(1, state.CapacityConflicts);
        Assert.Equal(1, state.PartConflicts);
        var fact = MemWmiCrossFactsService.Collect(At, [n], [s], () => [Wmi()]);
        Assert.Contains("WMI×原生 SPD：配對 1、容量矛盾 1、料號矛盾 1", fact.Value);
        Assert.Contains("WMI×SMBIOS：配對 1、容量矛盾 1、料號矛盾 1", fact.Value);
        Assert.Contains("不構成獨立驗證來源", fact.Value);
        Assert.DoesNotContain("BBBB0002", fact.Value);
        Assert.DoesNotContain("NATIVE-PART", fact.Value);
        Assert.DoesNotContain("SMBIOS-PART", fact.Value);
        Assert.False(fact.Sensitive);
    }

    [Fact]
    public void DifferentConflictsPerSourceRemainVisibleWithoutArbitratingWhichIsRight()
    {
        var n = Source(SpdAuditSourceKind.NativeSpd, mib: 8192);
        var s = Source(SpdAuditSourceKind.Smbios, part: "OTHER-PART");
        var state = MemWmiCrossFactsService.Cross([Wmi()], [n], [s]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(1, state.CapacityConflicts);
        Assert.Equal(1, state.PartConflicts);
        var fact = MemWmiCrossFactsService.Collect(At, [n], [s], () => [Wmi()]);
        Assert.Contains("WMI×原生 SPD：配對 1、容量矛盾 1、料號矛盾 0", fact.Value);
        Assert.Contains("WMI×SMBIOS：配對 1、容量矛盾 0、料號矛盾 1", fact.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateSerialInsideOneSourceNeverBorrowsIdentityFromPartOrOrder(bool reverse)
    {
        var a = Source(SpdAuditSourceKind.NativeSpd, part: "SAME-PART", mib: 8192);
        var b = Source(SpdAuditSourceKind.NativeSpd, part: "OTHER-PART", mib: 32768);
        var state = MemWmiCrossFactsService.Cross([Wmi()], reverse ? [b, a] : [a, b], null);
        Assert.Equal(0, state.ComparedModules);
        Assert.Equal(0, state.CapacityConflicts);
        Assert.Equal(0, state.PartConflicts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousNativeDoesNotSuppressUnambiguousSmbiosEvidence(bool reverse)
    {
        var n = Source(SpdAuditSourceKind.NativeSpd, mib: 8192);
        var s = Source(SpdAuditSourceKind.Smbios, mib: 32768);
        var state = MemWmiCrossFactsService.Cross([Wmi()], reverse ? [n with { PartNumber = "OTHER" }, n] : [n, n with { PartNumber = "OTHER" }], [s]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(1, state.CapacityConflicts);
        var fact = MemWmiCrossFactsService.Collect(At, [n, n], [s], () => [Wmi()]);
        Assert.Contains("WMI×原生 SPD：配對 0", fact.Value);
        Assert.Contains("WMI×SMBIOS：配對 1、容量矛盾 1", fact.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateWmiSerialIsAmbiguousInBothSourceComparisons(bool reverse)
    {
        var a = Wmi();
        var b = Wmi(part: "OTHER-PART", bytes: 8589934592L);
        var state = MemWmiCrossFactsService.Cross(reverse ? [b, a] : [a, b],
            [Source(SpdAuditSourceKind.NativeSpd)], [Source(SpdAuditSourceKind.Smbios)]);
        Assert.Equal(2, state.WmiModules);
        Assert.Equal(0, state.ComparedModules);
        Assert.Equal(0, state.SerialConflicts);
        Assert.Equal(0, state.CapacityConflicts);
        Assert.Equal(0, state.PartConflicts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000")]
    [InlineData("FFFFFFFF")]
    [InlineData("Unknown")]
    [InlineData("Default string")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Serial Number")]
    [InlineData("System Serial Number")]
    [InlineData("System-Serial_Number")]
    [InlineData("System Product Name")]
    [InlineData("System-Product_Name")]
    [InlineData("System Version")]
    [InlineData("System-Version")]
    public void MissingOrGenericSerialCannotBecomeIdentityThroughSamePartOrLocator(string? serial)
    {
        foreach (var kind in new[] { SpdAuditSourceKind.NativeSpd, SpdAuditSourceKind.Smbios })
        {
            var candidate = Source(kind, serial) with { Locator = "DIMM_B1" };
            SpdAuditSourceInput[] native = kind == SpdAuditSourceKind.NativeSpd ? [candidate] : [];
            SpdAuditSourceInput[] smbios = kind == SpdAuditSourceKind.Smbios ? [candidate] : [];
            var state = MemWmiCrossFactsService.Cross([Wmi(serial)], native, smbios);
            Assert.Equal(0, state.ComparedModules);
            Assert.Equal(0, state.SerialConflicts);
        }
    }

    [Fact]
    public void ForeignSerialWithIdenticalPartNeverCreatesASerialConflict()
    {
        var state = MemWmiCrossFactsService.Cross([Wmi()],
            [Source(SpdAuditSourceKind.NativeSpd, "AAAA0001", mib: 8192)], null);
        Assert.Equal(0, state.ComparedModules);
        Assert.Equal(0, state.SerialConflicts);
        Assert.Equal(0, state.CapacityConflicts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceAndWmiPermutationPreservesPartialInventoryIdentity(bool reverse)
    {
        var w1 = Wmi("AAAA0001");
        var w2 = Wmi("BBBB0002");
        var n1 = Source(SpdAuditSourceKind.NativeSpd, "AAAA0001", mib: 8192);
        var n2 = Source(SpdAuditSourceKind.NativeSpd, "BBBB0002");
        var state = MemWmiCrossFactsService.Cross(reverse ? [w2, w1] : [w1, w2],
            reverse ? [n2, n1] : [n1, n2], [Source(SpdAuditSourceKind.Smbios, "BBBB0002", mib: 32768)]);
        Assert.Equal(2, state.ComparedModules);
        Assert.Equal(2, state.CapacityConflicts);
        Assert.Equal(0, state.SerialConflicts);
    }

    [Fact]
    public void MissingComparedFieldsDoNotFabricateConflict()
    {
        var state = MemWmiCrossFactsService.Cross([Wmi(part: null, bytes: null)],
            [Source(SpdAuditSourceKind.NativeSpd, part: "PART", mib: 8192)],
            [Source(SpdAuditSourceKind.Smbios, part: null, mib: null)]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(0, state.CapacityConflicts);
        Assert.Equal(0, state.PartConflicts);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("  uNkNoWn  ")]
    [InlineData("N/A")]
    [InlineData("  n/a  ")]
    [InlineData("Not Specified")]
    [InlineData("Not Available")]
    [InlineData("Not Applicable")]
    [InlineData("Default string")]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("To be filled by OEM")]
    [InlineData("System Serial Number")]
    [InlineData("System Product Name")]
    [InlineData("System Version")]
    [InlineData("System manufacturer")]
    [InlineData("None")]
    [InlineData("Null")]
    [InlineData("Part Number")]
    [InlineData("  pArT nUmBeR  ")]
    [InlineData("Serial Number")]
    [InlineData("  sErIaL nUmBeR  ")]
    public void RawPartPlaceholderOnEitherSideIsMissingInBothSourceComparisons(string placeholder)
    {
        foreach (var kind in new[] { SpdAuditSourceKind.NativeSpd, SpdAuditSourceKind.Smbios })
        foreach (bool placeholderInWmi in new[] { false, true })
        {
            var w = Wmi(part: placeholderInWmi ? placeholder : "REAL-PART");
            var c = Source(kind, part: placeholderInWmi ? "REAL-PART" : placeholder);
            SpdAuditSourceInput[] native = kind == SpdAuditSourceKind.NativeSpd ? [c] : [];
            SpdAuditSourceInput[] smbios = kind == SpdAuditSourceKind.Smbios ? [c] : [];
            var state = MemWmiCrossFactsService.Cross([w], native, smbios);
            Assert.Equal(1, state.ComparedModules); // Unique serial still identifies the observation.
            Assert.Equal(0, state.PartConflicts);   // Missing part is not an asserted disagreement.
            Assert.Equal(0, state.CapacityConflicts);
            var fact = MemWmiCrossFactsService.Collect(At, native, smbios, () => [w]);
            Assert.Contains(kind == SpdAuditSourceKind.NativeSpd
                ? "WMI×原生 SPD：配對 1、容量矛盾 0、料號矛盾 0"
                : "WMI×SMBIOS：配對 1、容量矛盾 0、料號矛盾 0", fact.Value);
            Assert.Contains("缺欄不計矛盾，不表示已核一致", fact.Value);
        }
    }

    [Theory]
    [InlineData("NA")]
    [InlineData("PARTNUMBER")]
    [InlineData("SERIALNUMBER")]
    [InlineData("DefaultString")]
    [InlineData("UNKNOWNPART")]
    public void LiteralPartIdentifiersRemainComparableInBothDirections(string part)
    {
        foreach (var kind in new[] { SpdAuditSourceKind.NativeSpd, SpdAuditSourceKind.Smbios })
        foreach (bool literalInWmi in new[] { false, true })
        {
            var w = Wmi(part: literalInWmi ? part : "REAL-PART");
            var c = Source(kind, part: literalInWmi ? "REAL-PART" : part);
            var state = MemWmiCrossFactsService.Cross([w],
                kind == SpdAuditSourceKind.NativeSpd ? [c] : [],
                kind == SpdAuditSourceKind.Smbios ? [c] : []);
            Assert.Equal(1, state.ComparedModules);
            Assert.Equal(1, state.PartConflicts);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPartInOneSourceDoesNotHideTheOtherSourceRealDisagreement(bool nativeMissing)
    {
        var n = Source(SpdAuditSourceKind.NativeSpd, part: nativeMissing ? "N/A" : "DIFFERENT-PART");
        var s = Source(SpdAuditSourceKind.Smbios, part: nativeMissing ? "DIFFERENT-PART" : "Unknown");
        var state = MemWmiCrossFactsService.Cross([Wmi()], [n], [s]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(1, state.PartConflicts);
        var fact = MemWmiCrossFactsService.Collect(At, [n], [s], () => [Wmi()]);
        Assert.Contains(nativeMissing
            ? "WMI×原生 SPD：配對 1、容量矛盾 0、料號矛盾 0"
            : "WMI×原生 SPD：配對 1、容量矛盾 0、料號矛盾 1", fact.Value);
        Assert.Contains(nativeMissing
            ? "WMI×SMBIOS：配對 1、容量矛盾 0、料號矛盾 1"
            : "WMI×SMBIOS：配對 1、容量矛盾 0、料號矛盾 0", fact.Value);
    }

    [Fact]
    public void WmiMissingPartDoesNotReportAgreementOrDisagreementAgainstTwoRealParts()
    {
        var w = Wmi(part: "N/A");
        var n = Source(SpdAuditSourceKind.NativeSpd, part: "NA");
        var s = Source(SpdAuditSourceKind.Smbios, part: "DIFFERENT-PART");
        var state = MemWmiCrossFactsService.Cross([w], [n], [s]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(0, state.PartConflicts);
        var fact = MemWmiCrossFactsService.Collect(At, [n], [s], () => [w]);
        Assert.Contains("WMI×原生 SPD：配對 1、容量矛盾 0、料號矛盾 0", fact.Value);
        Assert.Contains("WMI×SMBIOS：配對 1、容量矛盾 0、料號矛盾 0", fact.Value);
        Assert.Contains("缺欄不計矛盾，不表示已核一致", fact.Value);
    }

    [Fact]
    public void WrongSourceKindDoesNotLeakAcrossInputInventories()
    {
        var state = MemWmiCrossFactsService.Cross([Wmi()],
            [Source(SpdAuditSourceKind.Smbios)], [Source(SpdAuditSourceKind.NativeSpd)]);
        Assert.Equal(0, state.NativeModules);
        Assert.Equal(0, state.SmbiosModules);
        Assert.Equal(0, state.ComparedModules);
    }

    [Fact]
    public void ProductionAdaptersReachTheSameSourceIdentityAndKeepCapacityDisagreement()
    {
        // Synthetic decoded observation; adapter integration, not SPD bit-layout verification.
        byte[] raw = new byte[512];
        raw[2] = 0x0C;
        var decoded = Assert.IsType<SpdSnapshot>(SpdDecoder.Decode(raw));
        decoded = decoded with { SerialHex = "BBBB0002", PartNumber = "SAME-PART",
            Geometry = decoded.Geometry with { CapacityMib = 8192 } };
        var native = SpdConsistencyAuditService.FromNative(new SpdDirectRead("fixture bus", 0x50, raw, decoded));
        var smbios = SpdConsistencyAuditService.FromSmbios(new SmbiosDimmRow("DIMM_B1", "BANK B",
            "16 GB", "DDR4", "3200 MT/s", "3200 MT/s", "fixture", "BBBB0002", "SAME-PART", "1"));
        Assert.Null(native.Locator);
        Assert.Equal(16384, smbios.CapacityMiB);
        var state = MemWmiCrossFactsService.Cross([Wmi()], [native], [smbios]);
        Assert.Equal(1, state.ComparedModules);
        Assert.Equal(1, state.CapacityConflicts);
        var fact = MemWmiCrossFactsService.Collect(At, [native], [smbios], () => [Wmi()]);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Equal(1d, fact.NumericValue);
        Assert.Contains("WMI×原生 SPD：配對 1、容量矛盾 1", fact.Value);
        Assert.Contains("WMI×SMBIOS：配對 1、容量矛盾 0", fact.Value);
    }
}
