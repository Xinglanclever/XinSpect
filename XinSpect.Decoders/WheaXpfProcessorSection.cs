using System.Buffers.Binary;
namespace XinSpect;

public enum WheaXpfCheckKind { Unknown, Cache, Tlb, Bus, Microarchitecture }

// No CPU/APIC identity, GUID from unknown input, pointer, register address or payload is retained.
public sealed record WheaXpfCheck(WheaXpfCheckKind Kind, bool CheckInfoValid,
    byte? TransactionType, byte? Operation, byte? Level, byte? ErrorType,
    bool? ProcessorContextCorrupt, bool? Uncorrected, bool? PreciseIp,
    bool? RestartableIp, bool? Overflow, byte? Participation, bool? Timeout, byte? AddressSpace);
public sealed record WheaXpfContext(ushort Type, ushort DataBytes);
public sealed record WheaXpfProcessorSection(bool LocalApicIdValid, bool CpuIdValid,
    IReadOnlyList<WheaXpfCheck> Checks, IReadOnlyList<WheaXpfContext> Contexts);

/// <summary>Saved XPF section metadata and check fields only. No hardware access or register payload projection.</summary>
public static class WheaXpfProcessorSectionDecoder
{
    public const int PrefixBytes = 64;
    public const int MaxBytes = 1024 * 1024;
    public static readonly Guid SectionType = new("dc3ea0b0-a144-4797-b95b-53fa242b6e1d");
    private static readonly Guid Cache = new("a55701f5-e3ef-43de-ac72-249b573fad2c");
    private static readonly Guid Tlb = new("fc06b535-5e1f-4562-9f25-0a3b9adb63c3");
    private static readonly Guid Bus = new("1cf3f8b3-c5b1-49a2-aa59-5eef92ffa63c");
    private static readonly Guid Ms = new("48ab7f57-dc34-4f6c-a7d3-b0b5b0a74314");

    [SpecRef("SDK10.0.28000.0 um/cper.h:840-962 XPF validbits, PROCINFO64B, CONTEXT_INFO16B; cperguid.h:120,199-216 XPF/check GUIDs; Microsoft Learn WHEA_XPF_CONTEXT_INFO Remarks: each context padded with zero bytes to a multiple of16")]
    public static WheaXpfProcessorSection? Parse(ReadOnlySpan<byte> bytes, out string? reason)
    {
        reason = null;
        if (bytes.Length < PrefixBytes || bytes.Length > MaxBytes)
        { reason = "XPF section size outside 64-byte prefix / 1MiB budget."; return null; }
        ulong valid = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if ((valid & ~0x3fffUL) != 0)
        { reason = "Unsupported XPF section validity bits."; return null; }
        int count = (int)((valid >> 2) & 63), contexts = (int)((valid >> 8) & 63);
        int offset = PrefixBytes;
        if (count > (bytes.Length - offset) / 64)
        { reason = "Truncated XPF processor information array."; return null; }
        var checks = new List<WheaXpfCheck>(count);
        for (int i = 0; i < count; i++, offset += 64)
        {
            var item = bytes.Slice(offset, 64);
            ulong fieldValid = BinaryPrimitives.ReadUInt64LittleEndian(item[16..]);
            if ((fieldValid & ~31UL) != 0)
            { reason = "Unsupported XPF processor-information validity bits."; return null; }
            var guid = new Guid(item[..16]);
            var kind = guid == Cache ? WheaXpfCheckKind.Cache : guid == Tlb ? WheaXpfCheckKind.Tlb
                : guid == Bus ? WheaXpfCheckKind.Bus : guid == Ms ? WheaXpfCheckKind.Microarchitecture : WheaXpfCheckKind.Unknown;
            bool enabled = (fieldValid & 1) != 0;
            ulong raw = enabled && kind != WheaXpfCheckKind.Unknown
                ? BinaryPrimitives.ReadUInt64LittleEndian(item[24..]) : 0;
            ulong allowedValid = kind == WheaXpfCheckKind.Bus ? 0x7ffUL
                : kind == WheaXpfCheckKind.Microarchitecture ? 0x3fUL : 0xffUL;
            if (enabled && kind != WheaXpfCheckKind.Unknown && ((raw & 0xffffUL) & ~allowedValid) != 0)
            { reason = "Unsupported XPF nested check validity bits."; return null; }
            // Ignore invalid data bits, including reserved value bits. Do not turn stale/garbage into facts.
            bool common = kind is WheaXpfCheckKind.Cache or WheaXpfCheckKind.Tlb or WheaXpfCheckKind.Bus;
            bool micro = kind == WheaXpfCheckKind.Microarchitecture;
            checks.Add(new(kind, enabled,
                common ? Field(raw, 0, 16, 3) : null,
                common ? Field(raw, 1, 18, 15) : null,
                common ? Field(raw, 2, 22, 7) : null,
                micro ? Field(raw, 0, 16, 7) : null,
                common ? Flag(raw, 3, 25) : micro ? Flag(raw, 1, 19) : null,
                common ? Flag(raw, 4, 26) : micro ? Flag(raw, 2, 20) : null,
                common ? Flag(raw, 5, 27) : micro ? Flag(raw, 3, 21) : null,
                common ? Flag(raw, 6, 28) : micro ? Flag(raw, 4, 22) : null,
                common ? Flag(raw, 7, 29) : micro ? Flag(raw, 5, 23) : null,
                kind == WheaXpfCheckKind.Bus ? Field(raw, 8, 30, 3) : null,
                kind == WheaXpfCheckKind.Bus ? Flag(raw, 9, 32) : null,
                kind == WheaXpfCheckKind.Bus ? Field(raw, 10, 33, 3) : null));
        }
        var contextList = new List<WheaXpfContext>(contexts);
        for (int i = 0; i < contexts; i++)
        {
            if (bytes.Length - offset < 16)
            { reason = "Truncated XPF context header."; return null; }
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]);
            int length = 16 + size;
            int stride = (length + 15) & ~15; // bounded u16 size; no arithmetic wrap
            if (stride > bytes.Length - offset)
            { reason = "Truncated XPF context data or required 16-byte padding."; return null; }
            if (bytes.Slice(offset + length, stride - length).IndexOfAnyExcept((byte)0) >= 0)
            { reason = "Nonzero XPF context alignment padding."; return null; }
            contextList.Add(new(type, size)); // Context payload/address bytes never enter the result.
            offset += stride;
        }
        if (offset != bytes.Length)
        { reason = "Unsupported trailing XPF section bytes."; return null; }
        return new((valid & 1) != 0, (valid & 2) != 0, checks.AsReadOnly(), contextList.AsReadOnly());
    }

    private static byte? Field(ulong raw, int validBit, int shift, ulong mask) =>
        (raw & (1UL << validBit)) != 0 ? (byte)((raw >> shift) & mask) : null;
    private static bool? Flag(ulong raw, int validBit, int shift) =>
        (raw & (1UL << validBit)) != 0 ? (raw & (1UL << shift)) != 0 : null;
}
