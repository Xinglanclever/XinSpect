using System.Buffers.Binary;
using Xunit;
namespace XinSpect.Tests;

// SDK synthetic fixtures and injected saved-event projections only. No real event/hardware evidence.
public sealed class WheaPcieSyntheticTests
{
    private static readonly Guid Memory = new("a5bc1114-6f64-4ede-b863-3e83ed7c83b1");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
    private static void U16(byte[] b, int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), v);
    private static void U32(byte[] b, int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), v);
    private static void U64(byte[] b, int p, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p), v);
    private static byte[] Pcie(ulong valid = 0xff)
    {
        var b = new byte[208];
        U64(b, 0, valid); U32(b, 8, 4); b[12] = 0x10; b[13] = 0x03;
        U16(b, 16, 0x1234); U16(b, 18, 0xabcd);
        U16(b, 24, 0x8086); U16(b, 26, 0x5678); U32(b, 28, 0x07123456);
        b[32] = 31; U16(b, 33, 0xfedc); b[35] = 0xab; b[36] = 0xcd;
        U16(b, 37, (ushort)(0x1abc << 3));
        U64(b, 40, 0xfedcba9876543210); U16(b, 48, 0x4567); U16(b, 50, 0x89ab);
        b.AsSpan(52).Fill(0xee); // contents must never appear in public record/summary facts
        return b;
    }
    private static byte[] Cper(params (Guid Type, byte[] Payload)[] sections)
    {
        var b = new byte[128 + 72 * sections.Length + sections.Sum(s => s.Payload.Length)];
        "CPER"u8.CopyTo(b); U16(b, 4, 0x0210); U32(b, 6, uint.MaxValue);
        U16(b, 10, (ushort)sections.Length); U32(b, 12, 1); U32(b, 20, (uint)b.Length);
        int p = 128 + 72 * sections.Length;
        for (int i = 0; i < sections.Length; i++)
        {
            int d = 128 + i * 72;
            U32(b, d, (uint)p); U32(b, d + 4, (uint)sections[i].Payload.Length); U16(b, d + 8, 0x0300);
            sections[i].Type.TryWriteBytes(b.AsSpan(d + 16, 16)); U32(b, d + 48, 2);
            sections[i].Payload.CopyTo(b, p); p += sections[i].Payload.Length;
        }
        return b;
    }
    private static WheaEventEnvelope Envelope(byte[] b, ulong eventId = 42) => WheaEventEnvelopeParser.Parse(
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events'><System><Provider Guid='c26c4f3c-3f66-4e99-8f8a-39405cfed220'/><EventID>17</EventID><Version>0</Version><Level>4</Level><EventRecordID>{eventId}</EventRecordID></System><EventData><Data Name='Length'>{b.Length}</Data><Data Name='RawData'>{Convert.ToHexString(b)}</Data></EventData></Event>", 17);
    private static WheaMemoryReadResult Records(WheaEventEnvelope envelope, WheaMemoryReadStatus status = WheaMemoryReadStatus.Complete) =>
        new([new(17, At, null, null) { Envelope = envelope }], status, At.AddDays(-7), At, "private arbitrary reason");

    [Fact]
    public void SdkUnalignedDeviceIdentityAndVersionDecodeWithoutSwappingSeverities()
    {
        var e = Envelope(Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie())));
        Assert.True(e.Valid); Assert.Empty(e.Cper!.Sections); Assert.Equal((byte)4, e.EventLevel);
        var section = Assert.Single(e.Cper.TypedSections);
        Assert.Equal(1u, section.RecordSeverity); Assert.Equal(2u, section.SectionSeverity);
        var p = Assert.IsType<WheaPcieSection>(WheaPcieSectionDecoder.Parse(section.Bytes));
        Assert.Equal((byte)0x03, p.MajorVersion); Assert.Equal((byte)0x10, p.MinorVersion);
        Assert.Equal((ushort)0x1234, p.Command); Assert.Equal((ushort)0xabcd, p.Status);
        var id = Assert.IsType<WheaPcieDeviceId>(p.DeviceId);
        Assert.Equal((ushort)0xfedc, id.Segment); Assert.Equal((byte)0xab, id.PrimaryBus);
        Assert.Equal((byte)31, id.Device); Assert.Equal((byte)7, id.Function);
        Assert.Equal(0x123456u, id.ClassCode); Assert.Equal((ushort)0x1abc, id.Slot);
        Assert.Equal((ushort)0x4567, p.BridgeSecondaryStatus); Assert.Equal((ushort)0x89ab, p.BridgeControl);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EachValidityBitAloneControlsItsFieldGroup(int bit)
    {
        var p = Assert.IsType<WheaPcieSection>(WheaPcieSectionDecoder.Parse(Pcie(1UL << bit)));
        Assert.Equal(bit == 0, p.PortType.HasValue); Assert.Equal(bit == 1, p.MajorVersion.HasValue);
        Assert.Equal(bit == 1, p.MinorVersion.HasValue); Assert.Equal(bit == 2, p.Command.HasValue);
        Assert.Equal(bit == 2, p.Status.HasValue); Assert.Equal(bit == 3, p.DeviceId is not null);
        Assert.Equal(bit == 4, p.DeviceSerialNumber.HasValue); Assert.Equal(bit == 5, p.BridgeControl.HasValue);
        Assert.Equal(bit == 5, p.BridgeSecondaryStatus.HasValue);
        Assert.Equal(bit == 6, p.HasExpressCapability); Assert.Equal(bit == 7, p.HasAerInfo);
    }
    [Fact]
    public void InvalidIdentityCannotDiscardIndependentCommandEvidence()
    {
        var bytes = Pcie(); bytes[31] = 8;
        var p = Assert.IsType<WheaPcieSection>(WheaPcieSectionDecoder.Parse(bytes));
        Assert.Null(p.DeviceId); Assert.NotNull(p.DeviceIdRejection); Assert.Equal((ushort)0x1234, p.Command);
    }
    [Fact]
    public void UnknownValidityAndShortPayloadHaveExplicitRejection()
    {
        Assert.Null(WheaPcieSectionDecoder.Parse(Pcie(0x100), out var why)); Assert.Contains("bits0..7", why);
        Assert.Null(WheaPcieSectionDecoder.Parse(new byte[207], out why)); Assert.Contains("208", why);
    }
    [Fact]
    public void MemoryCompatibilityAndTwoPcieDescriptorsSurviveWithOwnedSlices()
    {
        var memory = new byte[80]; U64(memory, 0, 1UL << 14); memory[72] = 2;
        var bytes = Cper((Memory, memory), (CperMemoryEnvelopeDecoder.PcieType, Pcie()), (CperMemoryEnvelopeDecoder.PcieType, Pcie()));
        var e = Envelope(bytes); Assert.True(e.Valid);
        Assert.Single(e.Cper!.Sections); Assert.Equal(3, e.Cper.TypedSections.Count);
        bytes.AsSpan().Fill(0);
        Assert.Equal((byte)2, e.Cper.Sections[0].Bytes[72]);
        e.Cper.TypedSections[0].Bytes[72] = 3;
        Assert.Equal((byte)2, e.Cper.Sections[0].Bytes[72]); // typed view cannot mutate memory compatibility slice
        var result = WheaErrorFactsService.Collect(Records(e), At);
        Assert.Contains(result, f => f.Key == "whea.mem.rec.e42.s0");
        Assert.Contains(result, f => f.Key == "whea.pcie.rec.e42.s1");
        Assert.Contains(result, f => f.Key == "whea.pcie.rec.e42.s2");
        Assert.Equal(2d, result.Single(f => f.Key == WheaPcieFactsService.SummaryKey).NumericValue);
    }
    [Theory]
    [InlineData(0x04u)] [InlineData(0x40u)]
    public void RejectedCperFlagsCannotPublishPcieRows(uint flag)
    {
        var b = Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie())); U32(b, 104, flag);
        var e = Envelope(b); Assert.False(e.Valid); Assert.Empty(e.Cper!.TypedSections);
        var facts = WheaPcieFactsService.Collect(Records(e), At);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith(WheaPcieFactsService.RecordPrefix));
        Assert.Null(facts.Single(f => f.Key == WheaPcieFactsService.SummaryKey).NumericValue);
    }
    [Fact]
    public void InvalidLastDescriptorCannotLeakPreviouslyAcceptedPcieSlice()
    {
        var b = Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie()), (Guid.Empty, new byte[8]));
        U32(b, 200, 128);
        var e = CperMemoryEnvelopeDecoder.Parse(b);
        Assert.False(e.Valid); Assert.Empty(e.TypedSections); Assert.Empty(e.Sections);
        var shortPcie = Cper((CperMemoryEnvelopeDecoder.PcieType, new byte[207]));
        var shortEnvelope = CperMemoryEnvelopeDecoder.Parse(shortPcie);
        Assert.True(shortEnvelope.Valid);
        Assert.Null(WheaPcieSectionDecoder.Parse(Assert.Single(shortEnvelope.TypedSections).Bytes));
        var mixed = CperMemoryEnvelopeDecoder.Parse(Cper((Memory, new byte[80]), (CperMemoryEnvelopeDecoder.PcieType, new byte[207])));
        Assert.True(mixed.Valid); Assert.Single(mixed.Sections); Assert.Equal(2, mixed.TypedSections.Count);
    }
    [Theory]
    [InlineData(WheaMemoryReadStatus.Truncated, FactAvailability.Unknown)]
    [InlineData(WheaMemoryReadStatus.TimedOut, FactAvailability.ReadError)]
    [InlineData(WheaMemoryReadStatus.InsufficientPrivilege, FactAvailability.InsufficientPrivilege)]
    [InlineData(WheaMemoryReadStatus.ReadError, FactAvailability.ReadError)]
    public void IncompleteEnumerationKeepsSavedEvidenceWithoutClaimingTotal(WheaMemoryReadStatus status, FactAvailability expected)
    {
        var facts = WheaPcieFactsService.Collect(Records(Envelope(Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie()))), status), At);
        var summary = facts.Single(f => f.Key == WheaPcieFactsService.SummaryKey);
        Assert.Equal(expected, summary.Availability); Assert.Null(summary.NumericValue);
        Assert.Contains("lower bound", summary.UnavailableReason);
        Assert.Contains(facts, f => f.Key == "whea.pcie.rec.e42.s0");
        Assert.DoesNotContain(facts, f => f.Value.Contains("private arbitrary reason"));
    }
    [Fact]
    public void SerialIsSeparateSensitiveAndDefaultSnapshotRedactsIt()
    {
        var facts = WheaPcieFactsService.Collect(Records(Envelope(Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie())))), At);
        var serial = facts.Single(f => f.Key.EndsWith(".serial")); Assert.True(serial.Sensitive);
        Assert.DoesNotContain(facts.Where(f => !f.Sensitive), f => f.Value.Contains("FEDCBA9876543210"));
        Assert.DoesNotContain(facts, f => f.Value.Contains(new string('E', 120)));
        var parsed = Assert.IsType<WheaPcieSection>(WheaPcieSectionDecoder.Parse(Pcie()));
        Assert.DoesNotContain("18364758544493064720", parsed.ToString());
        Assert.DoesNotContain("FEDCBA9876543210", parsed.ToString());
        Assert.DoesNotContain("18364758544493064720", System.Text.Json.JsonSerializer.Serialize(parsed));
        var envelope = Envelope(Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie())));
        string rawBase64 = Convert.ToBase64String(Pcie());
        Assert.DoesNotContain(rawBase64, System.Text.Json.JsonSerializer.Serialize(envelope.Cper));
        Assert.DoesNotContain(rawBase64, System.Text.Json.JsonSerializer.Serialize(Assert.Single(envelope.Cper!.TypedSections)));
        var snapshot = HardwareSnapshotService.Create("synthetic", facts, includeSensitive: false);
        Assert.DoesNotContain("FEDCBA9876543210", HardwareSnapshotService.ToJson(snapshot));
    }
    [Fact]
    public void CompoundIdentityDeduplicatesRepeatedEventWithoutCollapsingDifferentSections()
    {
        var e = Envelope(Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie(0)), (CperMemoryEnvelopeDecoder.PcieType, Pcie(0))));
        var r = Records(e)[0];
        var read = new WheaMemoryReadResult([r, r], WheaMemoryReadStatus.Complete, At.AddDays(-7), At, null);
        var facts = WheaPcieFactsService.Collect(read, At);
        Assert.Equal(2, facts.Count(f => f.Key.StartsWith(WheaPcieFactsService.RecordPrefix)));
        Assert.Equal(facts.Count, facts.Select(f => f.Key).Distinct().Count());
        Assert.Equal(2d, facts.Single(f => f.Key == WheaPcieFactsService.SummaryKey).NumericValue);
    }
    [Fact]
    public void ListingCapDoesNotDropSummaryCount()
    {
        var e = Envelope(Cper(Enumerable.Range(0, 20).Select(_ => (CperMemoryEnvelopeDecoder.PcieType, Pcie(0))).ToArray()));
        var facts = WheaPcieFactsService.Collect(Records(e), At);
        Assert.Equal(16, facts.Count(f => f.Key.StartsWith(WheaPcieFactsService.RecordPrefix)));
        Assert.Equal(20d, facts.Single(f => f.Key == WheaPcieFactsService.SummaryKey).NumericValue);
    }
    [Fact]
    public void PublicationProjectsSameReadAndRevocationCannotRestoreLatePcieFacts()
    {
        var e = Envelope(Cper((CperMemoryEnvelopeDecoder.PcieType, Pcie(0))));
        var lab = new EvidenceLabService(); int reads = 0;
        var lease = lab.BeginWheaMemoryCollection(0, CancellationToken.None);
        Assert.True(lab.CollectWheaMemory(lease, () => { reads++; return Records(e); }));
        Assert.Equal(1, reads); Assert.Contains(lab.AllFacts, f => f.Key == "whea.pcie.rec.e42.s0");
        var old = lab.BeginWheaMemoryCollection(0, CancellationToken.None);
        lab.RevokeWheaMemoryCollection(old, timedOut: true);
        var failure = lab.WheaMemoryFacts;
        Assert.DoesNotContain(failure, f => f.Key.StartsWith(WheaPcieFactsService.RecordPrefix));
        Assert.False(lab.CollectWheaMemory(old, () => Records(e)));
        Assert.Same(failure, lab.WheaMemoryFacts);
    }
}
