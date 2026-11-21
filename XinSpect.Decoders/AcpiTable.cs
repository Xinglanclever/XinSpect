namespace XinSpect;

/// <summary>ACPI 表標準表頭（36 bytes）。純解碼、不碰韌體；實際取表由服務層經 GetSystemFirmwareTable 取得後餵進來。</summary>
public readonly record struct AcpiTableHeader(
    string Signature, uint Length, byte Revision, string OemId, string OemTableId, bool ChecksumValid);

/// <summary>MCFG 條目：一個 PCI segment group 的 ECAM 基底與 bus 範圍。</summary>
public sealed record McfgEntry(ulong Base, ushort SegmentGroup, byte StartBus, byte EndBus);

/// <summary>HEST source descriptor boundary. It contains only source identity and byte range, never error-record payload.</summary>
public sealed record HestErrorSourceDescriptor(int Index, ushort Type, ushort? SourceId, int Offset, int Length, bool IsKnownType);

/// <summary>Result of structurally parsing a HEST table. Issue is a stable machine-readable code; partial Sources are complete descriptors only.</summary>
public sealed record HestParseResult(
    bool IsValid, bool ChecksumValid, uint? DeclaredSourceCount, IReadOnlyList<HestErrorSourceDescriptor> Sources, string Issue, int? ErrorOffset);

/// <summary>ACPI 表的純解析器。校驗和需要整表位元組（和 mod 256 = 0）；只有頭 36 bytes 時無法驗證即標 false，不假裝有效。</summary>
public static class AcpiTable
{
    public const int HeaderLength = 36;
    private const uint MaxHestSourceCount = 4096; // Bound allocation/work on malformed firmware tables.

    [SpecRef("ACPI Spec, 表頭通用格式（36 bytes）：Signature@0、Length@4、Revision@8、OEMID@10、OEM Table ID@16、Checksum@9，全表位元組和 mod 256 = 0")]
    public static bool TryParseHeader(ReadOnlySpan<byte> table, out AcpiTableHeader header)
    {
        header = default;
        if (table.Length < HeaderLength) return false;

        string sig = System.Text.Encoding.ASCII.GetString(table[..4]).TrimEnd('\0', ' ');
        uint length = BitConverter.ToUInt32(table[4..8]);
        byte revision = table[8];
        string oemId = System.Text.Encoding.ASCII.GetString(table[10..16]).TrimEnd('\0', ' ');
        string oemTableId = System.Text.Encoding.ASCII.GetString(table[16..24]).TrimEnd('\0', ' ');

        // 校驗和只有在拿得到整表（長度 >= 宣告長度）時才驗；拿不到就標 false，不假裝有效。
        bool checksumValid = length >= HeaderLength && table.Length >= length && Checksum(table[..(int)length]) == 0;

        header = new AcpiTableHeader(sig, length, revision, oemId, oemTableId, checksumValid);
        return true;
    }

    /// <summary>HEST 錯誤源數：標頭(36)+ErrorSourceCount(u32@36)。非 HEST 或過短回 null。</summary>
    [SpecRef("ACPI Spec, HEST 表：Error Source Count u32 @36")]
    public static uint? HestErrorSourceCount(ReadOnlySpan<byte> table)
    {
        if (table.Length < 40 || !table[..4].SequenceEqual("HEST"u8)) return null;
        uint declaredLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(table[4..8]);
        return declaredLength >= 40 && declaredLength <= table.Length
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(table[36..40])
            : null;
    }

    /// <summary>
    /// Parses HEST source boundaries without reading hardware or interpreting error payloads.
    /// Known legacy source types use their ACPI-defined fixed prefixes; revision 2 types 12+ use
    /// their standard Type+Length header and are safely retained as unknown descriptors.
    /// A bad checksum or malformed structure is never reported as valid.
    /// </summary>
    [SpecRef("ACPI Spec §18.3.2, HEST Table 18.2–18.16: source structures and length-coded extensions")]
    public static HestParseResult ParseHest(ReadOnlySpan<byte> table)
    {
        static HestParseResult Fail(string issue, bool checksumValid = false, uint? sourceCount = null,
            List<HestErrorSourceDescriptor>? sources = null, int? errorOffset = null)
        {
            IReadOnlyList<HestErrorSourceDescriptor> parsed = sources is null
                ? Array.Empty<HestErrorSourceDescriptor>()
                : sources.AsReadOnly();
            return new(false, checksumValid, sourceCount, parsed, issue, errorOffset);
        }

        if (table.Length < 40) return Fail("TableTooShort", errorOffset: table.Length);
        if (!table[..4].SequenceEqual("HEST"u8)) return Fail("NotHest");

        uint declaredLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(table[4..8]);
        if (declaredLength < 40) return Fail("DeclaredLengthTooSmall", errorOffset: 4);
        if (declaredLength > table.Length) return Fail("DeclaredLengthExceedsBuffer", errorOffset: 4);

        int tableLength = (int)declaredLength;
        ReadOnlySpan<byte> declaredTable = table[..tableLength];
        uint sourceCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(declaredTable[36..40]);
        if (Checksum(declaredTable) != 0) return Fail("ChecksumMismatch", sourceCount: sourceCount, errorOffset: 9);
        byte revision = declaredTable[8];
        if (revision is < 1 or > 2) return Fail("UnsupportedRevision", true, sourceCount, errorOffset: 8);
        // Every standard source has at least a 4-byte Type+Length header. This also bounds work
        // before iterating a potentially malicious u32 source count.
        int bodyLength = tableLength - 40;
        if (sourceCount > (uint)(bodyLength / 4))
            return Fail("SourceCountExceedsTable", checksumValid: true, sourceCount: sourceCount, errorOffset: 36);
        if (sourceCount > MaxHestSourceCount)
            return Fail("SourceCountLimitExceeded", true, sourceCount, errorOffset: 36);

        int offset = 40;
        ushort? previousLegacyType = null;
        var sources = new List<HestErrorSourceDescriptor>((int)sourceCount);
        for (int index = 0; index < (int)sourceCount; index++)
        {
            int remaining = tableLength - offset;
            if (remaining < 2) return Fail("TruncatedSourceType", true, sourceCount, sources, offset);
            ushort type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(declaredTable[offset..]);

            int length;
            bool knownType = true;
            ushort? sourceId = null;
            if (type >= 12)
            {
                if (revision < 2)
                    return Fail("UnsupportedLegacySourceType", true, sourceCount, sources, offset);
                if (remaining < 4) return Fail("TruncatedExtensionHeader", true, sourceCount, sources, offset);
                length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(declaredTable[(offset + 2)..]);
                if (length < 4) return Fail("InvalidExtensionLength", true, sourceCount, sources, offset + 2);
                knownType = false;
            }
            else
            {
                // ACPI HEST source types 3–5 are reserved; their layouts are not defined.
                if (type is >= 3 and <= 5) return Fail("ReservedSourceType", true, sourceCount, sources, offset);
                int prefixLength;
                int bankCountOffset = -1;
                int bankSize = 28;
                switch (type)
                {
                    case 0: prefixLength = 40; bankCountOffset = 32; break; // IA-32 MCE
                    case 1: prefixLength = 48; bankCountOffset = 44; break; // IA-32 corrected MCE
                    case 2: prefixLength = 20; break;                      // IA-32 NMI
                    case 6: prefixLength = 48; break;                      // PCIe Root Port AER
                    case 7: prefixLength = 44; break;                      // PCIe Device AER
                    case 8: prefixLength = 56; break;                      // PCIe Bridge AER
                    case 9: prefixLength = 64; break;                      // Generic Hardware Error
                    case 10: prefixLength = 92; break;                    // Generic Hardware Error v2
                    case 11: prefixLength = 48; bankCountOffset = 44; break; // Deferred IA-32 MCE
                    default: return Fail("UnsupportedLegacySourceType", true, sourceCount, sources, offset);
                }

                if (remaining < prefixLength) return Fail("TruncatedSource", true, sourceCount, sources, offset);
                sourceId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(declaredTable[(offset + 2)..]);
                length = prefixLength;
                if (bankCountOffset >= 0)
                {
                    int bankCount = declaredTable[offset + bankCountOffset];
                    length += bankCount * bankSize;
                }
            }

            if (length > remaining) return Fail("TruncatedSource", true, sourceCount, sources, offset);
            if (revision >= 2 && type < 12)
            {
                if (previousLegacyType is { } previous && type < previous)
                    return Fail("LegacySourceTypesOutOfOrder", true, sourceCount, sources, offset);
                previousLegacyType = type;
            }

            sources.Add(new HestErrorSourceDescriptor(index, type, sourceId, offset, length, knownType));
            offset += length;
        }

        if (offset != tableLength) return Fail("UnexpectedTrailingBytes", true, sourceCount, sources, offset);
        return new HestParseResult(true, true, sourceCount, sources.AsReadOnly(), string.Empty, null);
    }

    /// <summary>BERT 開機錯誤區長度：標頭(36)+BootErrorRegionLength(u32@36)。非 BERT 或過短回 null。實際錯誤記錄在實體位址，需 ring0（Phase 3）。</summary>
    [SpecRef("ACPI Spec, BERT 表：Boot Error Region Length u32 @36（Boot Error Region 位址在同表 @40 起）")]
    public static uint? BertBootErrorRegionLength(ReadOnlySpan<byte> table)
        => table.Length >= 48 && table[..4].SequenceEqual("BERT"u8) ? BitConverter.ToUInt32(table[36..40]) : null;

    /// <summary>
    /// MCFG 首條目（segment 0）：ECAM 基底與 bus 範圍。標頭(36)+保留(8)後每條目 16 bytes：基底 u64@0、PCI 群組 u16@8、起始 bus@10、結束 bus@11。
    /// 非 MCFG 或無條目回 null——ECAM 基底是平台事實，讀不到就說讀不到。
    /// </summary>
    /// <summary>MCFG 的全部條目。條目格式：基底 u64@0、PCI 群組 u16@8、起始 bus@10、結束 bus@11。
    /// 非 MCFG 或無條目回空陣列——ECAM 基底是平台事實，讀不到就說讀不到。</summary>
    [SpecRef("ACPI Spec, MCFG 表：標頭 36 bytes＋保留 8 bytes 後每條目 16 bytes——基底位址 u64@0、PCI Segment Group u16@8、起始 bus@10、結束 bus@11")]
    public static IReadOnlyList<McfgEntry> McfgEntries(ReadOnlySpan<byte> table)
    {
        if (table.Length < 44 || !table[..4].SequenceEqual("MCFG"u8)) return [];
        int count = (table.Length - 44) / 16;
        var entries = new List<McfgEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int off = 44 + i * 16;
            entries.Add(new McfgEntry(
                BitConverter.ToUInt64(table[off..(off + 8)]),
                BitConverter.ToUInt16(table[(off + 8)..(off + 10)]),
                table[off + 10], table[off + 11]));
        }
        return entries;
    }

    [SpecRef("ACPI Spec, MCFG 條目佈局同 McfgEntries；segment 0 條目＝平台 ECAM 基底（PCIe Spec §7.2.2 對應）")]
    public static (ulong Base, byte StartBus, byte EndBus)? McfgPrimaryEcam(ReadOnlySpan<byte> table)
    {
        var first = McfgEntries(table).FirstOrDefault();
        return first is { } e ? (e.Base, e.StartBus, e.EndBus) : null;
    }

    private static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (byte b in bytes) sum += b;
        return sum;
    }
}
