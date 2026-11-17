using System.Buffers.Binary;
namespace XinSpect;

public sealed record CperTypedSection(Guid SectionType, int DescriptorIndex, uint RecordSeverity, uint SectionSeverity, [property: System.Text.Json.Serialization.JsonIgnore] byte[] Bytes);
public sealed record CperMemorySection(int DescriptorIndex, uint RecordSeverity, uint SectionSeverity, byte[] Bytes);
public sealed record CperMemoryEnvelope(IReadOnlyList<CperMemorySection> Sections, string? Reason)
{
    public bool Valid => Reason is null;
    public uint? HeaderFlags { get; init; }
    // Narrow shared projection; Sections remains the existing memory-only compatibility view.
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<CperTypedSection> TypedSections { get; init; } = Array.Empty<CperTypedSection>();
}

/// <summary>Bounded SDK-layout parser. Synthetic tests do not establish event or hardware compatibility.</summary>
public static class CperMemoryEnvelopeDecoder
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxSections = 64;
    public static readonly Guid ProcessorGenericType = new("9876ccad-47b4-4bdb-b65e-16f193c4f3db");
    public static readonly Guid PcieType = new("d995e954-bbc1-430f-ad91-b44dcb3c6f35");
    private static readonly Guid MemoryType = new("a5bc1114-6f64-4ede-b863-3e83ed7c83b1");

    [SpecRef("Windows SDK10.0.26100.0 um/cper.h CPER_FIELD_CHECK header128 bytes, descriptor72 bytes; header revision0x0210, descriptor0x0300; cperguid.h MEMORY_ERROR_SECTION_GUID; SDK10.0.28000.0 cperguid.h PCIEXPRESS_ERROR_SECTION_GUID / PROCESSOR_GENERIC_ERROR_SECTION_GUID")]
    public static CperMemoryEnvelope Parse(byte[] bytes)
    {
        uint? headerFlags = null;
        CperMemoryEnvelope Reject(string reason) => new(Array.Empty<CperMemorySection>(), reason) { HeaderFlags = headerFlags };
        if (bytes.Length < 128 || bytes.Length > MaxBytes) return Reject("CPER buffer size rejected.");
        var s = bytes.AsSpan();
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p, 2));
        if (!s[..4].SequenceEqual("CPER"u8) || U32(6) != uint.MaxValue) return Reject("CPER signature rejected.");
        if (U16(4) != 0x0210) return Reject("CPER header revision unsupported.");
        headerFlags = U32(104);
        // cper.h WHEA_ERROR_RECORD_HEADER_FLAGS: Simulated bit2, SectionsTruncated bit6.
        // Preserve the event and flags, but never promote simulated/incomplete payloads to hardware facts.
        if ((headerFlags.Value & 0x44u) != 0)
        {
            string reasons = ((headerFlags.Value & 0x04u) != 0 ? "Simulated: not physical-error evidence. " : "")
                + ((headerFlags.Value & 0x40u) != 0 ? "SectionsTruncated: section completeness unavailable. " : "");
            return Reject($"CPER HeaderFlags=0x{headerFlags.Value:X8}: " + reasons);
        }
        int count = U16(10);
        if (count == 0 || count > MaxSections) return Reject("CPER section count rejected.");
        uint declared = U32(20);
        int endOfDescriptors = 128 + count * 72;
        if (declared != bytes.Length || endOfDescriptors > bytes.Length) return Reject("CPER declared length rejected.");
        uint severity = U32(12);
        if (severity > 3) return Reject("CPER record severity unsupported.");
        var ranges = new List<(uint Start, uint End)>();
        var memory = new List<CperMemorySection>();
        var typed = new List<CperTypedSection>();
        for (int i = 0; i < count; i++)
        {
            int d = 128 + i * 72;
            if (U16(d + 8) != 0x0300) return Reject("CPER descriptor revision unsupported.");
            uint offset = U32(d), length = U32(d + 4), sectionSeverity = U32(d + 48);
            if (offset < endOfDescriptors || offset > declared || length == 0 || length > declared - offset)
                return Reject("CPER section range rejected.");
            uint end = offset + length;
            if (ranges.Any(r => offset < r.End && r.Start < end)) return Reject("CPER sections overlap.");
            ranges.Add((offset, end));
            if (sectionSeverity > 3) return Reject("CPER section severity unsupported.");
            var type = new Guid(s.Slice(d + 16, 16));
            if (type == MemoryType)
            {
                if (length < WheaMemorySectionDecoder.SectionBytes) return Reject("CPER memory section truncated.");
                var payload = s.Slice((int)offset, (int)length).ToArray();
                memory.Add(new CperMemorySection(i, severity, sectionSeverity, payload));
                typed.Add(new CperTypedSection(type, i, severity, sectionSeverity, (byte[])payload.Clone()));
            }
            else if (type == PcieType || type == ProcessorGenericType || type == WheaXpfProcessorSectionDecoder.SectionType)
            {
                // Each typed decoder owns payload validity; malformed PCIe/Generic/XPF payloads do not discard other section evidence.
                typed.Add(new CperTypedSection(type, i, severity, sectionSeverity, s.Slice((int)offset, (int)length).ToArray()));
            }
        }
        return new(memory.AsReadOnly(), null) { HeaderFlags = headerFlags, TypedSections = typed.AsReadOnly() };
    }
}
