using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Xunit;
namespace XinSpect.Tests;

// Official SDK golden vectors through saved XML + existing publication/CLI pipeline; no EventLog/hardware calls.
public sealed class WheaXpfProcessorSyntheticTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-17T00:00:00Z");
    private static readonly Guid Xpf = new("dc3ea0b0-a144-4797-b95b-53fa242b6e1d");
    private static readonly Guid Cache = new("a55701f5-e3ef-43de-ac72-249b573fad2c");
    private static readonly Guid Tlb = new("fc06b535-5e1f-4562-9f25-0a3b9adb63c3");
    private static readonly Guid Bus = new("1cf3f8b3-c5b1-49a2-aa59-5eef92ffa63c");
    private static readonly Guid Ms = new("48ab7f57-dc34-4f6c-a7d3-b0b5b0a74314");
    private const ulong Secret = 0xfedcba9876543210UL;
    private static void U16(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), value);
    private static void U32(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value);
    private static void U64(byte[] b, int p, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p), value);
    private static byte[] Proc(Guid kind, ulong raw, ulong valid = 31)
    {
        var b = new byte[64]; kind.TryWriteBytes(b); U64(b, 16, valid); U64(b, 24, raw);
        for (int p = 32; p < 64; p += 8) U64(b, p, Secret);
        return b;
    }
    private static byte[] Context(ushort type = 0, ushort bytes = 1)
    {
        var b = new byte[(16 + bytes + 15) & ~15]; U16(b, 0, type); U16(b, 2, bytes);
        U32(b, 4, 0x76543210); U64(b, 8, Secret); b.AsSpan(16, bytes).Fill(0x5a); return b;
    }
    private static byte[] Section(byte[][] checks, byte[][]? contexts = null, ulong valid = 3)
    {
        contexts ??= [];
        var b = new byte[64 + checks.Sum(c => c.Length) + contexts.Sum(c => c.Length)];
        U64(b, 0, valid | ((ulong)checks.Length << 2) | ((ulong)contexts.Length << 8));
        U64(b, 8, Secret); b.AsSpan(16, 48).Fill(0x51);
        int offset = 64;
        foreach (var item in checks.Concat(contexts)) { item.CopyTo(b, offset); offset += item.Length; }
        return b;
    }
    private static byte[] Cper(params (Guid Type, byte[] Payload)[] sections)
    {
        var b = new byte[128 + 72 * sections.Length + sections.Sum(s => s.Payload.Length)];
        "CPER"u8.CopyTo(b); U16(b, 4, 0x0210); U32(b, 6, uint.MaxValue);
        U16(b, 10, (ushort)sections.Length); U32(b, 12, 1); U32(b, 20, (uint)b.Length);
        int offset = 128 + 72 * sections.Length;
        for (int i = 0; i < sections.Length; i++)
        {
            int d = 128 + i * 72; U32(b, d, (uint)offset); U32(b, d + 4, (uint)sections[i].Payload.Length);
            U16(b, d + 8, 0x0300); sections[i].Type.TryWriteBytes(b.AsSpan(d + 16)); U32(b, d + 48, 2);
            sections[i].Payload.CopyTo(b, offset); offset += sections[i].Payload.Length;
        }
        return b;
    }
    private static WheaEventEnvelope Envelope(byte[] payload, ulong identity = 42) => WheaEventEnvelopeParser.Parse(
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events'><System><Provider Guid='c26c4f3c-3f66-4e99-8f8a-39405cfed220'/><EventID>17</EventID><Version>0</Version><Level>4</Level><EventRecordID>{identity}</EventRecordID></System><EventData><Data Name='Length'>{payload.Length}</Data><Data Name='RawData'>{Convert.ToHexString(payload)}</Data></EventData></Event>", 17);
    private static WheaMemoryReadResult Read(WheaEventEnvelope envelope) => WheaMemoryRecordsReader.ReadResult(
        () => [new WheaMemoryEvent(17, At) { Envelope = envelope }], At, () => TimeSpan.Zero);
    private static WheaXpfProcessorSection Parse(byte[] bytes) => Assert.IsType<WheaXpfProcessorSection>(WheaXpfProcessorSectionDecoder.Parse(bytes, out _));

    [Fact]
    public void GoldenCacheTlbBusAndMicroarchitectureFieldsUseDifferentLayouts()
    {
        // SDK check bit positions computed here independently from the decoder helpers.
        ulong common = 0xffUL | (1UL << 16) | (4UL << 18) | (5UL << 22) | (0x15UL << 25);
        ulong bus = 0x7ffUL | (2UL << 16) | (6UL << 18) | (3UL << 22) | (0x1fUL << 25)
            | (2UL << 30) | (1UL << 32) | (3UL << 33);
        ulong micro = 0x3fUL | (5UL << 16) | (0x15UL << 19);
        var p = Parse(Section([Proc(Cache, common), Proc(Tlb, common), Proc(Bus, bus), Proc(Ms, micro)]));
        Assert.Equal(4, p.Checks.Count);
        foreach (var c in p.Checks.Take(2))
        {
            Assert.Equal((byte)1, c.TransactionType); Assert.Equal((byte)4, c.Operation); Assert.Equal((byte)5, c.Level);
            Assert.True(c.ProcessorContextCorrupt); Assert.False(c.Uncorrected); Assert.True(c.PreciseIp);
            Assert.False(c.RestartableIp); Assert.True(c.Overflow); Assert.Null(c.ErrorType); Assert.Null(c.Timeout);
        }
        Assert.Equal(WheaXpfCheckKind.Cache, p.Checks[0].Kind); Assert.Equal(WheaXpfCheckKind.Tlb, p.Checks[1].Kind);
        var b = p.Checks[2]; Assert.Equal((byte)2, b.Participation); Assert.True(b.Timeout); Assert.Equal((byte)3, b.AddressSpace);
        var m = p.Checks[3]; Assert.Equal((byte)5, m.ErrorType); Assert.Null(m.Level); Assert.Null(m.Operation);
        Assert.True(m.ProcessorContextCorrupt); Assert.False(m.Uncorrected); Assert.True(m.PreciseIp);
        Assert.False(m.RestartableIp); Assert.True(m.Overflow);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void EachBusValidityBitGatesExactlyOneSafeField(int bit)
    {
        var c = Parse(Section([Proc(Bus, (1UL << bit) | (0x7ffffUL << 16))])).Checks[0];
        bool[] present = [c.TransactionType.HasValue, c.Operation.HasValue, c.Level.HasValue,
            c.ProcessorContextCorrupt.HasValue, c.Uncorrected.HasValue, c.PreciseIp.HasValue,
            c.RestartableIp.HasValue, c.Overflow.HasValue, c.Participation.HasValue, c.Timeout.HasValue, c.AddressSpace.HasValue];
        for (int i = 0; i < present.Length; i++) Assert.Equal(i == bit, present[i]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void EachMicroarchitectureValidityBitUsesItsOwnShift(int bit)
    {
        var c = Parse(Section([Proc(Ms, (1UL << bit) | (0xffUL << 16))])).Checks[0];
        bool[] present = [c.ErrorType.HasValue, c.ProcessorContextCorrupt.HasValue, c.Uncorrected.HasValue,
            c.PreciseIp.HasValue, c.RestartableIp.HasValue, c.Overflow.HasValue];
        for (int i = 0; i < present.Length; i++) Assert.Equal(i == bit, present[i]);
        Assert.Null(c.TransactionType); Assert.Null(c.Operation); Assert.Null(c.Level);
    }

    [Fact]
    public void ParentValidityAndUnknownGuidDoNotDecodeGarbageOrPublishItsGuid()
    {
        var unknown = new Guid("fedcba98-7654-3210-fedc-ba9876543210");
        var p = Parse(Section([Proc(Cache, ulong.MaxValue, 0), Proc(unknown, ulong.MaxValue)]));
        Assert.False(p.Checks[0].CheckInfoValid); Assert.Null(p.Checks[0].Operation);
        Assert.Equal(WheaXpfCheckKind.Unknown, p.Checks[1].Kind); Assert.Null(p.Checks[1].Operation);
        Assert.DoesNotContain("FEDCBA98", JsonSerializer.Serialize(p).ToUpperInvariant());
        var facts = WheaXpfProcessorFactsService.Collect(Read(Envelope(Cper((Xpf, Section([Proc(unknown, ulong.MaxValue)]))))), At);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(facts, f => f.Key.StartsWith(WheaXpfProcessorFactsService.RecordPrefix)).Availability);
        Assert.DoesNotContain("FEDCBA98", JsonSerializer.Serialize(facts).ToUpperInvariant());
    }

    [Fact]
    public void ContextStrideUsesZeroPaddingAndDoesNotReadPayloadAsNextHeader()
    {
        var p = Parse(Section([], [Context(0, 1), Context(7, 17)]));
        Assert.Equal(new[] { new WheaXpfContext(0, 1), new WheaXpfContext(7, 17) }, p.Contexts);
        byte[] bad = Section([], [Context()]); bad[^1] = 1;
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(bad, out var why)); Assert.Contains("padding", why);
        var shortPadding = Section([], [Context()])[..^1];
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(shortPadding, out _));
        Assert.Equal(ushort.MaxValue, Assert.Single(Parse(Section([], [Context(0, ushort.MaxValue)])).Contexts).DataBytes);
    }

    [Fact]
    public void CountsTruncationTrailingBytesAndReservedValidityAreRejected()
    {
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(new byte[63], out _));
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(new byte[1024 * 1024 + 1], out _));
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(Section([], valid: 1UL << 14), out _));
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(Section([Proc(Cache, 0, 1UL << 5)]), out _));
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(Section([Proc(Cache, 1UL << 8)]), out _));
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(Section([Proc(Ms, 1UL << 6)]), out _));
        var shortArray = Section([]); U64(shortArray, 0, 63UL << 2);
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(shortArray, out _));
        var shortContext = Section([]); U64(shortContext, 0, 1UL << 8);
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(shortContext, out _));
        Assert.Null(WheaXpfProcessorSectionDecoder.Parse(Section([]).Concat(new byte[] { 0 }).ToArray(), out _));
        var maximum = Parse(Section(Enumerable.Range(0, 63).Select(_ => Proc(Cache, 0)).ToArray(),
            Enumerable.Range(0, 63).Select(_ => Context(0, 0)).ToArray()));
        Assert.Equal(63, maximum.Checks.Count); Assert.Equal(63, maximum.Contexts.Count);
    }

    [Fact]
    public void MalformedXpfDoesNotDiscardOtherAcceptedSectionEvidence()
    {
        var envelope = Envelope(Cper((CperMemoryEnvelopeDecoder.PcieType, new byte[208]), (Xpf, new byte[63])));
        Assert.True(envelope.Valid);
        var facts = WheaErrorFactsService.Collect(Read(envelope), At);
        Assert.Contains(facts, f => f.Key.StartsWith(WheaPcieFactsService.RecordPrefix));
        Assert.DoesNotContain(facts, f => f.Key.StartsWith(WheaXpfProcessorFactsService.RecordPrefix));
        Assert.Contains("rejected 1", Assert.Single(facts, f => f.Key == WheaXpfProcessorFactsService.SummaryKey).Value);
    }

    [Fact]
    public void RealPublicationAndCliUseSameAcceptedReadAndOmitAllSecretFields()
    {
        var envelope = Envelope(Cper((Xpf, Section([Proc(Cache, 1UL | (2UL << 16))], [Context(0, 17)]))));
        Assert.True(envelope.Valid);
        var lab = new EvidenceLabService(); int reads = 0;
        var lease = lab.BeginWheaMemoryCollection(0, CancellationToken.None);
        Assert.True(lab.CollectWheaMemory(lease, () => { reads++; return Read(envelope); }));
        Assert.Equal(1, reads);
        var row = Assert.Single(lab.AllFacts, f => f.Key.StartsWith(WheaXpfProcessorFactsService.RecordPrefix));
        Assert.Equal("whea.xpf.rec.i0001", row.Key); Assert.Equal(FactAvailability.Present, row.Availability);
        Assert.Contains("TransactionType 2", row.Value); Assert.Contains("RecordedAt " + At.ToString("O"), row.Value);
        Assert.Contains("RecordSeverity 1; SectionSeverity 2", row.Value);
        var model = Parse(Section([Proc(Cache, 0)], [Context(0, 17)]));
        foreach (string text in new[] { row.Value, model.ToString(), JsonSerializer.Serialize(model), JsonSerializer.Serialize(lab.AllFacts) })
        {
            Assert.DoesNotContain("FEDCBA9876543210", text.ToUpperInvariant()); Assert.DoesNotContain("QQQQ", text);
            Assert.DoesNotContain("5A5A5A5A", text.ToUpperInvariant()); Assert.DoesNotContain("e42", text);
        }
        using var output = new StringWriter(); using var error = new StringWriter();
        int result = CliService.Run(["--json", "evidence", "--query", "whea.xpf."], () => lab.AllFacts, output, error);
        Assert.True(result is CliService.ExitOk or CliService.ExitPartial);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Contains(json.RootElement.GetProperty("facts").EnumerateArray(), f => f.GetProperty("key").GetString() == "whea.xpf.rec.i0001");
        Assert.DoesNotContain("FEDCBA98", output.ToString().ToUpperInvariant());
        var old = lab.BeginWheaMemoryCollection(0, CancellationToken.None); lab.RevokeWheaMemoryCollection(old, true);
        Assert.False(lab.CollectWheaMemory(old, () => Read(envelope)));
        Assert.DoesNotContain(lab.AllFacts, f => f.Key.StartsWith(WheaXpfProcessorFactsService.RecordPrefix));
    }

    [Theory]
    [InlineData(WheaMemoryReadStatus.Truncated, FactAvailability.Unknown)]
    [InlineData(WheaMemoryReadStatus.TimedOut, FactAvailability.ReadError)]
    [InlineData(WheaMemoryReadStatus.InsufficientPrivilege, FactAvailability.InsufficientPrivilege)]
    [InlineData(WheaMemoryReadStatus.ReadError, FactAvailability.ReadError)]
    public void IncompleteReadCannotBecomePresentCount(WheaMemoryReadStatus status, FactAvailability expected)
    {
        var read = Read(Envelope(Cper((Xpf, Section([Proc(Cache, 0)])))));
        var partial = read with { Status = status, Reason = "SECRET-REASON" };
        var summary = Assert.Single(WheaXpfProcessorFactsService.Collect(partial, At), f => f.Key == WheaXpfProcessorFactsService.SummaryKey);
        Assert.Equal(expected, summary.Availability); Assert.Null(summary.NumericValue); Assert.DoesNotContain("SECRET-REASON", summary.Value);
    }

    [Fact]
    public void DedupAndListingBoundsKeepDescriptorCountWithoutPublishingEventIdentity()
    {
        var envelope = Envelope(Cper(Enumerable.Range(0, 20).Select(_ => (Xpf, Section([Proc(Cache, 0)]))).ToArray()));
        var read = Read(envelope);
        var facts = WheaXpfProcessorFactsService.Collect(read with { Records = read.Records.Concat(read.Records).ToArray() }, At);
        Assert.Equal(16, facts.Count(f => f.Key.StartsWith(WheaXpfProcessorFactsService.RecordPrefix)));
        Assert.Equal(20d, Assert.Single(facts, f => f.Key == WheaXpfProcessorFactsService.SummaryKey).NumericValue);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(WheaXpfProcessorFactsService.Collect(read.ToArray(), At), f => f.Key == WheaXpfProcessorFactsService.SummaryKey).Availability);
        var empty = new WheaMemoryReadResult([], WheaMemoryReadStatus.Complete, At.AddDays(-7), At, null);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(WheaXpfProcessorFactsService.Collect(empty, At), f => f.Key == WheaXpfProcessorFactsService.SummaryKey).Availability);
    }
}
