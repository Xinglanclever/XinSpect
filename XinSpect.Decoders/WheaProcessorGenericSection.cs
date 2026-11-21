using System.Buffers.Binary;
namespace XinSpect;

// Sensitive addresses/brand are deliberately not retained in this model or ToString/JSON.
public sealed record WheaProcessorGenericSection(ulong ValidBits, byte? ProcessorType,
    byte? InstructionSet, byte? ErrorType, byte? Operation, byte? Flags, byte? Level,
    ulong? CpuVersion, ulong? ProcessorId);

public static class WheaProcessorGenericSectionDecoder
{
    public const int SectionBytes = 192;
    [SpecRef("Windows SDK10.0.28000.0 um/cper.h WHEA_PROCESSOR_GENERIC_ERROR_SECTION CPER_FIELD_CHECK0..184,192B; Microsoft Learn WHEA_PROCESSOR_GENERIC_ERROR_SECTION")]
    public static WheaProcessorGenericSection? Parse(byte[] bytes) => Parse(bytes, out _);
    [SpecRef("SDK28000 um/cper.h generic VALIDBITS0..12; bit13 NativeModelId semantics unsupported in this bounded decoder; GENPROC constants")]
    public static WheaProcessorGenericSection? Parse(byte[] bytes, out string? reason)
    {
        reason = null;
        if (bytes.Length < SectionBytes) { reason = "Processor generic section shorter than SDK192-byte layout."; return null; }
        ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset,8));
        ulong valid=U64(0);
        if ((valid & ~0x1fffUL)!=0)
        { reason="Unsupported processor generic validity bits (NativeModelId/extension not decoded)."; return null; }
        bool Has(int bit)=>(valid & (1UL<<bit))!=0;
        return new(valid,Has(0)?bytes[8]:null,Has(1)?bytes[9]:null,Has(2)?bytes[10]:null,
            Has(3)?bytes[11]:null,Has(4)?bytes[12]:null,Has(5)?bytes[13]:null,
            Has(6) && Has(0) && bytes[8]==0 ? (U64(16) & 0xffffffffUL) : null,Has(8)?U64(152):null);
        // Bits7/9..12 are acknowledged only; no brand/address payload is retained or output.
    }
}
