using System.Buffers.Binary;
using Xunit;
namespace XinSpect.Tests;
public sealed class WheaProcessorGenericSyntheticTests
{
    private static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-11-17T00:00:00Z");
    private static void U16(byte[] b,int p,ushort v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p),v);
    private static void U32(byte[] b,int p,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p),v);
    private static void U64(byte[] b,int p,ulong v)=>BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p),v);
    private static byte[] Processor(ulong valid=0x1fff)
    {
        var b=new byte[192];U64(b,0,valid);b[8]=0;b[9]=2;b[10]=1;b[11]=2;b[12]=8;b[13]=3;
        U64(b,16,0x12345678);U64(b,152,0x42);b.AsSpan(24,128).Fill(0x51);
        for(int i=160;i<192;i++)b[i]=0xee;
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


    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)]
    public void EachValidityBitGatesOnlyItsSafeField(int bit)
    {
        var p=Assert.IsType<WheaProcessorGenericSection>(WheaProcessorGenericSectionDecoder.Parse(Processor(1UL<<bit)));
        Assert.Equal(bit==0,p.ProcessorType.HasValue);Assert.Equal(bit==1,p.InstructionSet.HasValue);
        Assert.Equal(bit==2,p.ErrorType.HasValue);Assert.Equal(bit==3,p.Operation.HasValue);
        Assert.Equal(bit==4,p.Flags.HasValue);Assert.Equal(bit==5,p.Level.HasValue);
        Assert.False(p.CpuVersion.HasValue);Assert.Equal(bit==8,p.ProcessorId.HasValue);
        Assert.DoesNotContain("QQQQ",p.ToString());Assert.DoesNotContain("EEEE",p.ToString());
    }
    [Fact]
    public void ShortAndNativeModelValidityAreRejectedWithoutGuessing()
    {
        Assert.Null(WheaProcessorGenericSectionDecoder.Parse(new byte[191],out var shortWhy));
        Assert.Contains("192",shortWhy);
        Assert.Null(WheaProcessorGenericSectionDecoder.Parse(Processor(1UL<<13),out var why));
        Assert.Contains("NativeModelId",why);
    }
    [Fact]
    public void MixedShortProcessorDoesNotDiscardMemoryOrPcieEvidence()
    {
        var e=Envelope(Cper((new Guid("a5bc1114-6f64-4ede-b863-3e83ed7c83b1"),new byte[80]),
            (CperMemoryEnvelopeDecoder.PcieType,new byte[208]),
            (CperMemoryEnvelopeDecoder.ProcessorGenericType,new byte[191])));
        Assert.True(e.Valid);Assert.Single(e.Cper!.Sections);
        var facts=WheaErrorFactsService.Collect(Records(e),At);
        Assert.Contains(facts,f=>f.Key.StartsWith(WheaPcieFactsService.RecordPrefix));
        Assert.DoesNotContain(facts,f=>f.Key.StartsWith(WheaProcessorGenericFactsService.RecordPrefix));
        Assert.Contains("rejected 1",Assert.Single(facts,f=>f.Key==WheaProcessorGenericFactsService.SummaryKey).Value);
    }
    [Fact]
    public void AcceptedXmlToSharedProjectionPreservesIdentityTimesAndOmitsSensitivePayload()
    {
        var e=Envelope(Cper((CperMemoryEnvelopeDecoder.ProcessorGenericType,Processor())));
        var facts=WheaErrorFactsService.Collect(Records(e),At.AddHours(1));
        var row=Assert.Single(facts,f=>f.Key.StartsWith(WheaProcessorGenericFactsService.RecordPrefix));
        Assert.Equal("whea.proc.rec.e42.s0",row.Key);
        Assert.Equal(At.AddHours(1),row.MeasuredAtUtc);Assert.Contains(At.ToString("O"),row.Value);
        Assert.Contains("RecordSeverity 1",row.Value);Assert.Contains("SectionSeverity 2",row.Value);
        Assert.Contains("EventLevel 4",row.Value);Assert.Contains("local APIC",row.Value);
        Assert.DoesNotContain("EEEE",row.Value);Assert.DoesNotContain("QQQQ",row.Value);
    }
    [Fact]
    public void EnumerationFailureAndInjectedCompletenessCannotBecomePresentTotal()
    {
        var e=Envelope(Cper((CperMemoryEnvelopeDecoder.ProcessorGenericType,Processor())));
        var incomplete=WheaProcessorGenericFactsService.Collect(Records(e,WheaMemoryReadStatus.TimedOut),At);
        Assert.Equal(FactAvailability.ReadError,Assert.Single(incomplete,f=>f.Key==WheaProcessorGenericFactsService.SummaryKey).Availability);
        var plain=WheaProcessorGenericFactsService.Collect(Records(e).ToArray(),At);
        Assert.Equal(FactAvailability.Unknown,Assert.Single(plain,f=>f.Key==WheaProcessorGenericFactsService.SummaryKey).Availability);
    }
    [Fact]
    public void DuplicateCompoundIdentityAndListingBudgetAreBounded()
    {
        var e=Envelope(Cper((CperMemoryEnvelopeDecoder.ProcessorGenericType,Processor())));
        var duplicate=Records(e).Concat(Records(e)).ToArray();
        Assert.Single(WheaProcessorGenericFactsService.Collect(duplicate,At),f=>f.Key.StartsWith(WheaProcessorGenericFactsService.RecordPrefix));
        var many=Enumerable.Range(0,20).Select(i=>new WheaMemoryRecord(17,At,null,null)
        {Envelope=Envelope(Cper((CperMemoryEnvelopeDecoder.ProcessorGenericType,Processor())),(ulong)i)}).ToArray();
        Assert.Equal(16,WheaProcessorGenericFactsService.Collect(many,At).Count(f=>f.Key.StartsWith(WheaProcessorGenericFactsService.RecordPrefix)));
    }
    [Fact]
    public void UnknownEnumsHighCpuBitsAndExtensionPayloadDoNotGainInventedMeaning()
    {
        var payload=Processor();payload[8]=255;payload[9]=255;payload[10]=255;payload[11]=255;
        U64(payload,16,0xdeadbeef12345678UL);
        var parsed=Assert.IsType<WheaProcessorGenericSection>(WheaProcessorGenericSectionDecoder.Parse(payload));
        Assert.Null(parsed.CpuVersion);
        payload[8]=0;
        Assert.Equal(0x12345678UL,WheaProcessorGenericSectionDecoder.Parse(payload)!.CpuVersion);
        payload[8]=255;
        Assert.DoesNotContain("DEADBEEF",System.Text.Json.JsonSerializer.Serialize(parsed).ToUpperInvariant());
        Assert.Null(WheaProcessorGenericSectionDecoder.Parse(Processor(1UL<<63)));
        var extended=payload.Concat(Enumerable.Repeat((byte)0xee,32)).ToArray();
        var facts=WheaProcessorGenericFactsService.Collect(Records(Envelope(Cper((CperMemoryEnvelopeDecoder.ProcessorGenericType,extended)))),At);
        var row=Assert.Single(facts,f=>f.Key.StartsWith(WheaProcessorGenericFactsService.RecordPrefix));
        Assert.Contains("unassigned/unknown",row.Value);Assert.DoesNotContain("DEADBEEF",row.Value);
        Assert.DoesNotContain("EEEE",row.Value);
    }

}
