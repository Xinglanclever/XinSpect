using System.Buffers.Binary;
using Xunit;
namespace XinSpect.Tests;

/// <summary>SDK/installed-manifest synthetic contracts only. No real WHEA hardware payload is available.</summary>
public class WheaEnvelopeSyntheticTests
{
    private static byte[] Cper(int count = 1)
    {
        var b = new byte[128 + count * (72 + 80)];
        "CPER"u8.CopyTo(b);
        void U16(int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), v);
        void U32(int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), v);
        U16(4, 0x0210); U32(6, uint.MaxValue); U16(10, (ushort)count);
        U32(12, 1); U32(20, (uint)b.Length);
        for (int i = 0; i < count; i++)
        {
            int d = 128 + i * 72, p = 128 + count * 72 + i * 80;
            U32(d, (uint)p); U32(d + 4, 80); U16(d + 8, 0x0300);
            new Guid("a5bc1114-6f64-4ede-b863-3e83ed7c83b1").TryWriteBytes(b.AsSpan(d + 16, 16));
            U32(d + 48, 2);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p), (1UL << 1) | (1UL << 14));
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p + 16), ulong.MaxValue);
            b[p + 72] = 2;
        }
        return b;
    }
    private static string Xml(byte[] b, int version = 0, string extra = "", int eventId = 17) =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events'><System><Provider Guid='c26c4f3c-3f66-4e99-8f8a-39405cfed220'/><EventID>{eventId}</EventID><Version>{version}</Version><EventRecordID>42</EventRecordID></System><EventData><Data Name='Length'>{b.Length}</Data><Data Name='RawData'>{Convert.ToHexString(b)}</Data>{extra}</EventData></Event>";

    [Theory]
    [InlineData(1)]
    [InlineData(18)]
    public void AddedCandidatesRequireValidatedMemorySectionNotEventId(int id)
    {
        var at = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var envelope = WheaEventEnvelopeParser.Parse(Xml(Cper(), eventId: id), id);
        Assert.True(envelope.Valid);
        var result = WheaMemoryRecordsReader.ReadResult(() => [new(id, at) { Envelope = envelope }], at, () => TimeSpan.Zero);
        Assert.Equal(WheaMemoryReadStatus.Complete, result.Status);
        Assert.Single(result);
        var facts = WheaMemoryFactsService.Collect(result, at);
        Assert.Equal(1d, facts.Single(f => f.Key == WheaMemoryFactsService.SummaryKey).NumericValue);
        Assert.Single(facts, f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix));
        var nonMemory = Cper();
        Guid.Empty.TryWriteBytes(nonMemory.AsSpan(144, 16));
        var other = WheaEventEnvelopeParser.Parse(Xml(nonMemory, eventId: id), id);
        Assert.True(other.Valid);
        Assert.Empty(other.Cper!.Sections);
        var otherFacts = WheaMemoryFactsService.Collect([new(id, at, null, null) { Envelope = other }], at);
        Assert.Contains("已驗證但無記憶體節區 1", otherFacts.Single(f => f.Key == WheaMemoryFactsService.SummaryKey).Value);
        Assert.DoesNotContain(otherFacts, f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(18)]
    public void AddedCandidateUnknownVersionRetainsEventAndRejection(int id)
    {
        var at = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var envelope = WheaEventEnvelopeParser.Parse(Xml(Cper(), version: 1, eventId: id), id);
        Assert.False(envelope.Valid);
        Assert.Contains("ID/version", envelope.Reason);
        var result = WheaMemoryRecordsReader.ReadResult(() => [new(id, at) { Envelope = envelope }], at, () => TimeSpan.Zero);
        Assert.Single(result);
        Assert.Equal(id, result[0].EventId);
        Assert.Equal(WheaMemoryReadStatus.Complete, result.Status);
        var facts = WheaMemoryFactsService.Collect(result, at);
        var summary = facts.Single(f => f.Key == WheaMemoryFactsService.SummaryKey);
        Assert.Equal(1d, summary.NumericValue);
        Assert.Contains("ID/version", summary.Value);
        Assert.Contains("記憶體節區 0 筆", summary.Value);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix));
    }
    [Theory]
    [InlineData(0x04u, "Simulated")]
    [InlineData(0x40u, "SectionsTruncated")]
    [InlineData(0x44u, "SectionsTruncated")]
    public void SimulatedOrTruncatedPayloadRetainsEventAndFlagsWithoutHardwareRows(uint flags, string reason)
    {
        var b = Cper();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(104), flags);
        var envelope = WheaEventEnvelopeParser.Parse(Xml(b), 17);
        Assert.False(envelope.Valid);
        Assert.Equal(flags, envelope.Cper!.HeaderFlags);
        Assert.Empty(envelope.Cper.Sections);
        Assert.Contains(reason, envelope.Reason);
        var at = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var result = WheaMemoryRecordsReader.ReadResult(() => [new(17, at) { Envelope = envelope }], at);
        Assert.Equal(WheaMemoryReadStatus.Complete, result.Status);
        Assert.Single(result);
        var facts = WheaMemoryFactsService.Collect(result, at);
        var summary = facts.Single(f => f.Key == WheaMemoryFactsService.SummaryKey);
        Assert.Equal(1d, summary.NumericValue);
        Assert.Contains(reason, summary.Value);
        Assert.DoesNotContain("已發生的事", summary.Value);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix));
        // A mixed valid/rejected input must retain the rejection reason as well.
        var mixed = WheaMemoryFactsService.Collect([result[0], new(17, at, null, null) { Envelope = WheaEventEnvelopeParser.Parse(Xml(Cper()), 17) }], at);
        Assert.Contains(reason, mixed.Single(f => f.Key == WheaMemoryFactsService.SummaryKey).Value);
        Assert.Single(mixed, f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix));
    }
    [Fact] public void DistinctSeveritiesAndDescriptorIdentitySurvive()
    {
        var e = WheaEventEnvelopeParser.Parse(Xml(Cper(2)), 17);
        Assert.True(e.Valid);
        Assert.Equal("42", e.EventIdentity);
        Assert.Equal(2, e.Cper!.Sections.Count);
        Assert.Equal(1u, e.Cper.Sections[1].RecordSeverity);
        Assert.Equal(2u, e.Cper.Sections[1].SectionSeverity);
        Assert.Equal(1, e.Cper.Sections[1].DescriptorIndex);
    }
    [Fact] public void EveryDescriptorIsCheckedIncludingNonMemory()
    {
        var b = Cper(2);
        Guid.Empty.TryWriteBytes(b.AsSpan(128 + 72 + 16, 16));
        Assert.Single(CperMemoryEnvelopeDecoder.Parse(b).Sections);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(128 + 72), 128u);
        Assert.False(CperMemoryEnvelopeDecoder.Parse(b).Valid);
    }
    [Fact] public void OverlapLengthAndRevisionsAreRejected()
    {
        var b = Cper(2);
        b.AsSpan(128, 4).CopyTo(b.AsSpan(200, 4));
        Assert.False(CperMemoryEnvelopeDecoder.Parse(b).Valid);
        b = Cper(); b[4] = 0;
        Assert.False(CperMemoryEnvelopeDecoder.Parse(b).Valid);
        b = Cper(); b[136] = 1;
        Assert.False(CperMemoryEnvelopeDecoder.Parse(b).Valid);
        b = Cper(); b[20] = 0;
        Assert.False(CperMemoryEnvelopeDecoder.Parse(b).Valid);
        Assert.False(CperMemoryEnvelopeDecoder.Parse(new byte[CperMemoryEnvelopeDecoder.MaxBytes + 1]).Valid);
    }
    [Fact] public void ManifestVersionDuplicateAndNamespaceAreRejected()
    {
        var b = Cper();
        Assert.False(WheaEventEnvelopeParser.Parse(Xml(b, 1), 17).Valid);
        Assert.False(WheaEventEnvelopeParser.Parse(Xml(b, extra: "<Data Name='Length'>280</Data>"), 17).Valid);
        Assert.False(WheaEventEnvelopeParser.Parse(Xml(b).Replace("http://schemas.microsoft.com/win/2004/08/events", "urn:wrong"), 17).Valid);
        Assert.False(WheaEventEnvelopeParser.Parse(Xml(b).Replace(Convert.ToHexString(b), "00"), 17).Valid);
        Assert.False(WheaEventEnvelopeParser.Parse(new string('x', WheaEventEnvelopeParser.MaxXmlChars + 1), 17).Valid);
    }
    [Fact] public void OneEventManySectionsHasGlobalCapAndSensitiveStableKeys()
    {
        var envelope = WheaEventEnvelopeParser.Parse(Xml(Cper(20)), 17);
        var at = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var facts = WheaMemoryFactsService.Collect([new(17, at, null, null) { Envelope = envelope }], at);
        Assert.Equal(1d, facts.Single(f => f.Key == WheaMemoryFactsService.SummaryKey).NumericValue);
        Assert.Contains("記憶體節區 20", facts.Single(f => f.Key == WheaMemoryFactsService.SummaryKey).Value);
        var rows = facts.Where(f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix)).ToArray();
        Assert.Equal(16, rows.Length);
        Assert.Equal("whea.mem.rec.e42.s0", rows[0].Key);
        Assert.Contains("RecordSeverity 1", rows[0].Value);
        Assert.Contains("SectionSeverity 2", rows[0].Value);
        Assert.All(rows, f => Assert.True(f.Sensitive));
    }
    [Fact] public void MixedRejectedReasonsAreDistinctBoundedAndControlFree()
    {
        var at = DateTimeOffset.UnixEpoch;
        var good = new WheaMemoryRecord(17, at, null, null) { Envelope = WheaEventEnvelopeParser.Parse(Xml(Cper()), 17) };
        var bad = new WheaMemoryRecord(17, at, null, null) { Envelope = new(null, null, "unsupported-version\r\n" + new string('X', 1000)) };
        var summary = WheaMemoryFactsService.Collect([good, bad, bad], at).Single(f => f.Key == WheaMemoryFactsService.SummaryKey);
        Assert.Contains("unsupported-version", summary.Value);
        Assert.DoesNotContain("\r", summary.Value);
        Assert.DoesNotContain("\n", summary.Value);
        Assert.DoesNotContain(new string('X', 241), summary.Value);
        Assert.Equal(3d, summary.NumericValue);
        Assert.Contains("外層未通過驗證 2", summary.Value);
    }
    [Fact] public void InvalidPayloadDoesNotChangeEnumerationCompleteness()
    {
        var at = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
        var result = WheaMemoryRecordsReader.ReadResult(() => [new(17, at) { Envelope = WheaEventEnvelopeParser.Parse("bad", 17) }], at);
        Assert.Equal(WheaMemoryReadStatus.Complete, result.Status);
        var facts = WheaMemoryFactsService.Collect(result, at);
        Assert.Contains("外層未通過驗證 1", facts.Single(f => f.Key == WheaMemoryFactsService.SummaryKey).Value);
        Assert.DoesNotContain(facts, f => f.Key.StartsWith(WheaMemoryFactsService.RecordPrefix));
    }
}
