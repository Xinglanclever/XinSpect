namespace XinSpect;

/// <summary>PPTT 子結構（型別、長度、位移）。</summary>
public readonly record struct PpttStructure(byte Type, byte Length, int Offset);

/// <summary>PPTT 處理器節點：旗標與 ACPI 處理器 ID。</summary>
public readonly record struct PpttProcessor(uint Flags, uint Parent, uint AcpiProcessorId)
{
    /// <summary>bit0 實體封裝、bit1 處理器 ID 有效、bit2 為執行緒、bit3 葉節點、bit4 同質。</summary>
    public bool IsPhysicalPackage => (Flags & 0x01) != 0;
    public bool ProcessorIdValid => (Flags & 0x02) != 0;
    public bool IsThread => (Flags & 0x04) != 0;
    public bool IsLeaf => (Flags & 0x08) != 0;
}

/// <summary>PPTT 快取節點：大小與關聯度（僅在旗標說有效時才可信）。</summary>
public readonly record struct PpttCache(uint Flags, uint Size, uint NumberOfSets, byte Associativity, ushort LineSize)
{
    public bool SizeValid => (Flags & 0x01) != 0;
    public bool SetsValid => (Flags & 0x02) != 0;
    public bool AssociativityValid => (Flags & 0x04) != 0;
}

/// <summary>
/// PPTT（Processor Properties Topology Table）純解碼器。出處：Linux <c>include/acpi/actbl2.h</c>
/// （2026-10-11 抓取核對）——子結構表頭 <c>acpi_subtable_header</c>（type u8@0、length u8@1）、
/// Processor（type 0）：reserved u16@2、flags u32@4、parent u32@8、acpi_processor_id u32@12、
/// number_of_priv_resources u32@16；Cache（type 1）：flags u32@4、next u32@8、size u32@12、
/// number_of_sets u32@16、associativity u8@20、attributes u8@21、line_size u16@22。
/// </summary>
/// <remarks>
/// <b>界線</b>：只做結構走訪與明定欄位；<b>不宣稱拓撲正確</b>——PPTT 是韌體自述，
/// 與 CPUID／OS 的拓撲是否一致由服務層並列陳述（不一致是事實，不是判決）。
/// </remarks>
public static class AcpiPptt
{
    public const byte ProcessorType = 0;
    public const byte CacheType = 1;

    /// <summary>走訪全部子結構（自表頭 36 bytes 後起）。長度不可信即停，不硬讀。</summary>
    [SpecRef("ACPI 6.2+ PPTT（Linux actbl2.h acpi_table_pptt／acpi_pptt_processor）：表頭 36B 後每子結構 acpi_subtable_header——type u8@0、length u8@1")]
    public static IReadOnlyList<PpttStructure> Structures(ReadOnlySpan<byte> table)
    {
        if (table.Length < AcpiTable.HeaderLength + 2 || !table[..4].SequenceEqual("PPTT"u8)) return [];
        var list = new List<PpttStructure>();
        int off = AcpiTable.HeaderLength;
        while (off + 2 <= table.Length)
        {
            byte type = table[off];
            byte len = table[off + 1];
            if (len < 2 || off + len > table.Length) break;
            list.Add(new PpttStructure(type, len, off));
            off += len;
        }
        return list;
    }

    /// <summary>處理器節點（型別 0）清單；長度不足 20 的節點略過（不猜缺失欄位）。</summary>
    [SpecRef("ACPI 6.2+ PPTT Processor Structure（actbl2.h acpi_pptt_processor）：flags u32@4（bit0 Physical Package、bit1 ACPI Processor ID Valid、bit2 Is Thread、bit3 Leaf）、parent u32@8、acpi_processor_id u32@12")]
    public static IReadOnlyList<PpttProcessor> Processors(ReadOnlySpan<byte> table)
    {
        var list = new List<PpttProcessor>();
        foreach (var s in Structures(table))
        {
            if (s.Type != ProcessorType || s.Length < 20) continue;
            list.Add(new PpttProcessor(
                BitConverter.ToUInt32(table[(s.Offset + 4)..(s.Offset + 8)]),
                BitConverter.ToUInt32(table[(s.Offset + 8)..(s.Offset + 12)]),
                BitConverter.ToUInt32(table[(s.Offset + 12)..(s.Offset + 16)])));
        }
        return list;
    }

    /// <summary>快取節點（型別 1）清單；長度不足 24 的節點略過。</summary>
    [SpecRef("ACPI 6.2+ PPTT Cache Structure（actbl2.h acpi_pptt_cache）：flags u32@4（bit0 Size Valid、bit1 Number of Sets Valid、bit2 Associativity Valid）、size u32@12、number_of_sets u32@16、associativity u8@20、line_size u16@22")]
    public static IReadOnlyList<PpttCache> Caches(ReadOnlySpan<byte> table)
    {
        var list = new List<PpttCache>();
        foreach (var s in Structures(table))
        {
            if (s.Type != CacheType || s.Length < 24) continue;
            list.Add(new PpttCache(
                BitConverter.ToUInt32(table[(s.Offset + 4)..(s.Offset + 8)]),
                BitConverter.ToUInt32(table[(s.Offset + 12)..(s.Offset + 16)]),
                BitConverter.ToUInt32(table[(s.Offset + 16)..(s.Offset + 20)]),
                table[s.Offset + 20],
                BitConverter.ToUInt16(table[(s.Offset + 22)..(s.Offset + 24)])));
        }
        return list;
    }
}
