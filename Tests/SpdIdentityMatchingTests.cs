using System.Text;
using Xunit;

namespace XinSpect.Tests;

// Pure in-memory identity scenarios. No WMI/native I/O/hardware/MainViewModel.
// Synthetic bytes exercise the public decoder-adapter-Audit path, not decoder offset correctness.
public sealed class SpdIdentityMatchingTests
{
    private static SpdAuditSourceInput Native(string? serial, string? locator = null,
        string? part = "SAME-PART", int? capacity = 8192,
        SpdAuditLocatorScope scope = SpdAuditLocatorScope.Unspecified) =>
        new(SpdAuditSourceKind.NativeSpd, "native " + serial, locator, "Hynix", part, serial,
            capacity, 3200, 2024, 10, true, true, true, LocatorScope: scope);

    private static SpdAuditSourceInput External(string? serial,
        SpdAuditSourceKind kind = SpdAuditSourceKind.Smbios, string? locator = "external slot",
        string? part = "SAME-PART", int? capacity = 8192,
        SpdAuditLocatorScope scope = SpdAuditLocatorScope.Unspecified) =>
        new(kind, kind.ToString(), locator, "Hynix", part, serial,
            capacity, 3200, 2024, 10, LocatorScope: scope);

    private static IReadOnlyList<SpdSlotAudit> Audit(
        IReadOnlyList<SpdAuditSourceInput> native, params SpdAuditSourceInput[] external) =>
        SpdConsistencyAuditService.Audit(new SpdAuditInput(native,
            external.Where(x => x.Kind == SpdAuditSourceKind.CpuZ).ToArray(),
            external.Where(x => x.Kind == SpdAuditSourceKind.Smbios).ToArray()));

    private static void AssertUnbound(SpdSlotAudit audit)
    {
        Assert.DoesNotContain(audit.Sources,
            x => x.Kind is SpdAuditSourceKind.CpuZ or SpdAuditSourceKind.Smbios);
        Assert.Contains(audit.Findings, x => x.Field == SpdAuditField.SourceAvailability
            && x.Verdict == SpdAuditVerdict.MissingData);
        Assert.DoesNotContain(audit.Findings,
            x => x.Field is SpdAuditField.Capacity or SpdAuditField.Speed or SpdAuditField.ManufactureDate
            && x.Verdict == SpdAuditVerdict.Conflict);
    }

    private static void AssertOnlyBHasSmbios(IReadOnlyList<SpdSlotAudit> results)
    {
        var auditA = Assert.Single(results, x => x.Sources[0].SerialNumber == "AAAA0001");
        var auditB = Assert.Single(results, x => x.Sources[0].SerialNumber == "BBBB0002");
        AssertUnbound(auditA);
        Assert.Equal("BBBB0002", Assert.Single(auditB.Sources,
            x => x.Kind == SpdAuditSourceKind.Smbios).SerialNumber);
        Assert.DoesNotContain(results.SelectMany(x => x.Findings),
            x => x.Field == SpdAuditField.SerialNumber && x.Verdict == SpdAuditVerdict.Conflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialExternalInventoryMatchesUniqueSerialRegardlessOfNativeOrder(bool reverse)
    {
        var a = Native("AAAA0001");
        var b = Native("BBBB0002");
        AssertOnlyBHasSmbios(Audit(reverse ? [b, a] : [a, b], External("BBBB0002")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionOverloadDoesNotStealPartialSmbiosRecord(bool reverse)
    {
        var a = Direct("AAAA0001", 0x50);
        var b = Direct("BBBB0002", 0x51);
        var row = new SmbiosDimmRow("DIMM_B1", "BANK B", "8 GB", "DDR4",
            "3200 MT/s", "3200 MT/s", "SK Hynix", "BBBB0002", "SAME-PART", "1");

        Assert.Null(SpdConsistencyAuditService.FromNative(a).Locator);
        Assert.Equal(SpdAuditLocatorScope.Unspecified, SpdConsistencyAuditService.FromNative(a).LocatorScope);
        Assert.Equal("AAAA0001", a.Decoded.SerialHex);
        Assert.Equal("SAME-PART", b.Decoded.PartNumber);
        AssertOnlyBHasSmbios(SpdConsistencyAuditService.Audit(reverse ? [b, a] : [a, b], smbios: [row]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompleteDistinctSerialInventoryIsInvariantToBothOrders(bool reverseNative, bool reverseExternal)
    {
        var a = Native("AAAA0001");
        var b = Native("BBBB0002");
        var sa = External("AAAA0001");
        var sb = External("BBBB0002");
        var results = Audit(reverseNative ? [b, a] : [a, b], reverseExternal ? [sb, sa] : [sa, sb]);

        Assert.All(results, audit => Assert.Equal(audit.Sources[0].SerialNumber,
            Assert.Single(audit.Sources, x => x.Kind == SpdAuditSourceKind.Smbios).SerialNumber));
    }

    [Theory]
    [InlineData(SpdAuditSourceKind.CpuZ)]
    [InlineData(SpdAuditSourceKind.Smbios)]
    public void PartialOneToOneSameModelDifferentPhysicalModuleDoesNotCreateCapacityConflict(SpdAuditSourceKind kind)
    {
        // Ground truth A and B differ; one observed row on each side is not a complete inventory.
        var a = Native("AAAA0001", capacity: 8192);
        var b = External(null, kind, capacity: 16384) with { Vendor = "Other vendor", SpeedMTs = 2666 };
        var result = Assert.Single(Audit([a], b));

        AssertUnbound(result);
        Assert.DoesNotContain(result.Findings, x => x.Verdict is SpdAuditVerdict.Conflict or SpdAuditVerdict.Suspicious);
        Assert.Equal(SpdAuditVerdict.MissingData, result.Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneModelOnlyRecordDoesNotChooseBetweenIdenticalNativeModules(bool reverse)
    {
        var a = Native("AAAA0001");
        var b = Native("BBBB0002");
        Assert.All(Audit(reverse ? [b, a] : [a, b], External(null, SpdAuditSourceKind.CpuZ)), AssertUnbound);
    }

    [Fact]
    public void UniqueModelOnlyRecordsDoNotEstablishPhysicalIdentity()
    {
        // Corrects the v1 assumption that model uniqueness in a partial list proves a module.
        var results = Audit([Native("AAAA0001", part: "PART-A"), Native("BBBB0002", part: "PART-B")],
            External(null, SpdAuditSourceKind.CpuZ, part: "PART-B"),
            External(null, SpdAuditSourceKind.CpuZ, part: "PART-A"));

        Assert.All(results, AssertUnbound);
    }

    [Fact]
    public void CompletelyUnidentifiedOneToOnePairIsNotBound()
    {
        var native = new SpdAuditSourceInput(SpdAuditSourceKind.NativeSpd, "native");
        var external = new SpdAuditSourceInput(SpdAuditSourceKind.CpuZ, "CPU-Z");
        AssertUnbound(Assert.Single(Audit([native], external)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnidentifiedOneToOnePairWithDifferentSpeedDateAndLocatorsIsNotBound(bool nativeLocatorAbsent)
    {
        var native = new SpdAuditSourceInput(SpdAuditSourceKind.NativeSpd, "native",
            nativeLocatorAbsent ? null : "DIMM_A1", SpeedMTs: 3200,
            ManufactureYear: 2024, ManufactureWeek: 10, BaseCrcValid: true, ModuleCrcValid: true,
            LocatorScope: SpdAuditLocatorScope.SmbiosPhysicalSlot);
        var external = new SpdAuditSourceInput(SpdAuditSourceKind.CpuZ, "CPU-Z", "DIMM_B1",
            SpeedMTs: 2666, ManufactureYear: 2023, ManufactureWeek: 20,
            LocatorScope: SpdAuditLocatorScope.SmbiosPhysicalSlot);

        AssertUnbound(Assert.Single(Audit([native], external)));
    }

    [Fact]
    public void ProductionUnknownIdentityRetainsNativeSpeedDateWithoutComparingForeignCpuZ()
    {
        var raw = new byte[512];
        raw[2] = 0x0C;
        raw[4] = 0x0F; // Reserved density: no usable capacity.
        raw[18] = 5; // tCK 625 ps: retains a usable 3200 MT/s rate.
        raw[323] = 0x24;
        raw[324] = 0x10;
        var direct = new SpdDirectRead("synthetic bus", 0x50, raw, SpdDecoder.Decode(raw)!);
        var native = SpdConsistencyAuditService.FromNative(direct);
        var foreign = new SpdModule
        {
            Slot = "DIMM #2", MaxJedec = "DDR4-2666 (1333 MHz)", ManufacturingDate = "Week 20/Year 23",
        };

        Assert.Null(native.Locator);
        Assert.Null(native.CapacityMiB);
        Assert.Equal("00000000", native.SerialNumber);
        Assert.Equal("", native.PartNumber);
        Assert.Equal(3200, native.SpeedMTs);
        Assert.Equal(2024, native.ManufactureYear);
        Assert.Equal(10, native.ManufactureWeek);
        AssertUnbound(Assert.Single(SpdConsistencyAuditService.Audit([direct], cpuz: [foreign])));
    }

    [Fact]
    public void KnownDifferentSerialWithoutScopedSharedLocatorIsNotBound()
    {
        AssertUnbound(Assert.Single(Audit([Native("AAAA0001")], External("BBBB0002"))));
    }

    [Fact]
    public void SameLocatorTextWithoutProvenScopeDoesNotBindDifferentSerials()
    {
        AssertUnbound(Assert.Single(Audit([Native("AAAA0001", "DIMM_A1")],
            External("BBBB0002", locator: "DIMM_A1"))));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(9, 9)]
    public void DifferentOrUndefinedLocatorScopeDoesNotBind(int nativeScope, int externalScope)
    {
        AssertUnbound(Assert.Single(Audit(
            [Native("AAAA0001", "DIMM_A1", scope: (SpdAuditLocatorScope)nativeScope)],
            External("BBBB0002", locator: "DIMM_A1", scope: (SpdAuditLocatorScope)externalScope))));
    }

    [Fact]
    public void IndependentlyMappedUniqueLocatorReportsRealSerialConflictWithBothValues()
    {
        const SpdAuditLocatorScope scope = SpdAuditLocatorScope.SmbiosPhysicalSlot;
        var result = Assert.Single(Audit([Native("AAAA0001", "DIMM_A1 / BANK 0", scope: scope)],
            External("BBBB0002", locator: "dimm a1-bank 0", scope: scope)));

        var finding = Assert.Single(result.Findings, x => x.Field == SpdAuditField.SerialNumber);
        Assert.Equal(SpdAuditVerdict.Conflict, finding.Verdict);
        Assert.Contains(finding.Evidence, x => x.Kind == SpdAuditSourceKind.NativeSpd && x.Value == "AAAA0001");
        Assert.Contains(finding.Evidence, x => x.Kind == SpdAuditSourceKind.Smbios && x.Value == "BBBB0002");
    }

    [Fact]
    public void IndependentlyMappedLocatorCanCompareCapacityEvenWhenSerialIsMissing()
    {
        const SpdAuditLocatorScope scope = SpdAuditLocatorScope.SmbiosPhysicalSlot;
        var result = Assert.Single(Audit([Native(null, "DIMM_A1", scope: scope)],
            External(null, locator: "DIMM_A1", capacity: 16384, scope: scope)));

        Assert.Contains(result.Findings, x => x.Field == SpdAuditField.Capacity && x.Verdict == SpdAuditVerdict.Conflict);
    }

    [Fact]
    public void NativeLocatorTextDoesNotDeclareScopeAndSmbiosAdapterDoes()
    {
        var direct = Direct("AAAA0001", 0x50);
        var implicitScope = SpdConsistencyAuditService.FromNative(direct, "DIMM_A1 / BANK 0");
        var explicitMapping = SpdConsistencyAuditService.FromNative(direct, "DIMM_A1 / BANK 0")
            with { LocatorScope = SpdAuditLocatorScope.SmbiosPhysicalSlot };
        var row = new SmbiosDimmRow("DIMM_A1", "BANK 0", "8 GB", "DDR4", "3200 MT/s", "3200 MT/s",
            "SK Hynix", "BBBB0002", "SAME-PART", "1");
        var external = SpdConsistencyAuditService.FromSmbios(row);

        Assert.Equal(SpdAuditLocatorScope.Unspecified, implicitScope.LocatorScope);
        Assert.Equal(SpdAuditLocatorScope.SmbiosPhysicalSlot, external.LocatorScope);
        AssertUnbound(Assert.Single(Audit([implicitScope], external)));
        Assert.Contains(Assert.Single(Audit([explicitMapping], external)).Findings,
            x => x.Field == SpdAuditField.SerialNumber && x.Verdict == SpdAuditVerdict.Conflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateScopedNativeLocatorDoesNotStealUniqueSerialMatch(bool reverse)
    {
        const SpdAuditLocatorScope scope = SpdAuditLocatorScope.SmbiosPhysicalSlot;
        var a = Native("AAAA0001", "DIMM_B1", scope: scope);
        var b = Native("BBBB0002", "DIMM_B1", scope: scope);
        AssertOnlyBHasSmbios(Audit(reverse ? [b, a] : [a, b],
            External("BBBB0002", locator: "DIMM_B1", scope: scope)));
    }

    [Fact]
    public void DuplicateScopedExternalLocatorRequiresUniqueSerialEvidence()
    {
        const SpdAuditLocatorScope scope = SpdAuditLocatorScope.SmbiosPhysicalSlot;
        var result = Assert.Single(Audit([Native("AAAA0001", "DIMM_A1", scope: scope)],
            External("BBBB0002", locator: "DIMM_A1", scope: scope),
            External("AAAA0001", locator: "DIMM_A1", scope: scope)));

        Assert.Equal("AAAA0001", Assert.Single(result.Sources,
            x => x.Kind == SpdAuditSourceKind.Smbios).SerialNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedNativeSerialIsNotDisambiguatedByPartScore(bool reverse)
    {
        var a = Native("AAAA0001", part: "PART-A");
        var b = Native("AAAA0001", part: "PART-B");
        Assert.All(Audit(reverse ? [b, a] : [a, b], External("AAAA0001", part: "PART-B")), AssertUnbound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedExternalSerialIsNotDisambiguatedByPartScore(bool reverse)
    {
        var a = External("AAAA0001", locator: "A", part: "PART-A");
        var b = External("AAAA0001", locator: "B", part: "PART-B");
        AssertUnbound(Assert.Single(Audit([Native("AAAA0001", part: "PART-B")], reverse ? [b, a] : [a, b])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("...")]
    [InlineData("00000000")]
    [InlineData("00-00 00-00")]
    [InlineData("FFFFFFFF")]
    [InlineData("ff-ff")]
    [InlineData("Serial-Number")]
    [InlineData("System Serial Number")]
    [InlineData("System-Serial_Number")]
    [InlineData("System Product Name")]
    [InlineData("System-Product_Name")]
    [InlineData("System Version")]
    [InlineData("System-Version")]
    [InlineData("Unknown!")]
    [InlineData("0000000000000000")]
    public void MissingOrNormalizedPlaceholderSerialDoesNotBind(string? serial)
    {
        AssertUnbound(Assert.Single(Audit([Native(serial)], External(serial))));
    }

    [Fact]
    public void NormalizedUniqueValidSerialEstablishesIdentity()
    {
        var result = Assert.Single(Audit([Native("AA-AA 00 01")], External("AAAA0001")));
        Assert.Contains(result.Sources, x => x.Kind == SpdAuditSourceKind.Smbios);
        Assert.Equal(SpdAuditVerdict.Consistent,
            Assert.Single(result.Findings, x => x.Field == SpdAuditField.SerialNumber).Verdict);
    }

    [Fact]
    public void UniqueSerialRetainsRealPartAndCapacityDiscrepancies()
    {
        var result = Assert.Single(Audit([Native("AAAA0001", part: "PART-A")],
            External("AAAA0001", part: "PART-B", capacity: 16384)));
        Assert.Contains(result.Findings, x => x.Field == SpdAuditField.PartNumber && x.Verdict == SpdAuditVerdict.Conflict);
        Assert.Contains(result.Findings, x => x.Field == SpdAuditField.Capacity && x.Verdict == SpdAuditVerdict.Conflict);
    }

    [Fact]
    public void ResidualOneToOneModelOnlyPairIsNotBoundAfterARealSerialMatch()
    {
        var results = Audit([Native("AAAA0001"), Native(null)], External("AAAA0001"), External(null));
        Assert.Contains(results[0].Sources, x => x.Kind == SpdAuditSourceKind.Smbios);
        AssertUnbound(results[1]);
    }

    [Fact]
    public void EmptyNormalizedScopedLocatorDoesNotBindDifferentSerials()
    {
        const SpdAuditLocatorScope scope = SpdAuditLocatorScope.SmbiosPhysicalSlot;
        AssertUnbound(Assert.Single(Audit([Native("AAAA0001", "...", scope: scope)],
            External("BBBB0002", locator: "???", scope: scope))));
    }

    [Theory]
    [InlineData("Unknown!")]
    [InlineData("Default-String")]
    [InlineData("Not-Specified")]
    public void NormalizedGenericScopedLocatorDoesNotBindDifferentSerials(string locator)
    {
        const SpdAuditLocatorScope scope = SpdAuditLocatorScope.SmbiosPhysicalSlot;
        AssertUnbound(Assert.Single(Audit([Native("AAAA0001", locator, scope: scope)],
            External("BBBB0002", locator: locator, scope: scope))));
    }

    [Theory]
    [InlineData(false, "00-00-00-00")]
    [InlineData(true, "00-00-00-00")]
    [InlineData(false, "ff-ff-ff-ff")]
    [InlineData(true, "ff-ff-ff-ff")]
    [InlineData(false, "Serial-Number")]
    [InlineData(true, "Serial-Number")]
    public void ScopedLocatorDoesNotPromotePlaceholderSerialToConflict(bool nativePlaceholder, string placeholder)
    {
        const SpdAuditLocatorScope scope = SpdAuditLocatorScope.SmbiosPhysicalSlot;
        var native = Native(nativePlaceholder ? placeholder : "AAAA0001", "DIMM_A1", scope: scope);
        var external = External(nativePlaceholder ? "AAAA0001" : placeholder, locator: "DIMM_A1", scope: scope);
        var result = Assert.Single(Audit([native], external));

        Assert.Contains(result.Sources, x => x.Kind == SpdAuditSourceKind.Smbios);
        Assert.Equal(placeholder, result.Sources[nativePlaceholder ? 0 : 1].SerialNumber);
        var finding = Assert.Single(result.Findings, x => x.Field == SpdAuditField.SerialNumber);
        Assert.Equal(SpdAuditVerdict.MissingData, finding.Verdict);
        Assert.Contains(finding.Evidence, x => x.Value == "缺資料／通用值");
        Assert.Contains(finding.Evidence, x => x.Value == "AAAA0001");
    }

    private static SpdDirectRead Direct(string serial, byte address)
    {
        var raw = new byte[512];
        raw[2] = 0x0C;
        raw[320] = 0x80;
        raw[321] = 0xAD;
        Convert.FromHexString(serial).CopyTo(raw, 325);
        Encoding.ASCII.GetBytes("SAME-PART").CopyTo(raw, 329);
        return new SpdDirectRead("synthetic bus", address, raw, SpdDecoder.Decode(raw)!);
    }
}
