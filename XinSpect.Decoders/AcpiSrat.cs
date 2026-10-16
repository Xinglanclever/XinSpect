namespace XinSpect;

/// <summary>SRAT 的處理器親和結構：鄰近域、APIC/x2APIC ID、是否啟用。</summary>
public readonly record struct SratProcessorAffinity(
    uint ProximityDomain, uint ApicId, bool Enabled, bool IsX2Apic);

/// <summary>SRAT 的記憶體親和結構：鄰近域與它涵蓋的實體位址範圍。</summary>
public readonly record struct SratMemoryAffinity(
    uint ProximityDomain, ulong BaseAddress, ulong Length, bool Enabled);

/// <summary>SRAT 的一般親和結構（型別 5）：把某個裝置／匯流排對應到鄰近域。</summary>
public readonly record struct SratGenericAffinity(
    uint ProximityDomain, byte DeviceHandleType, uint Handle, uint Flags);

/// <summary>
/// ACPI SRAT（System Resource Affinity Table）的純解碼器：
/// <b>「哪個處理器、哪塊記憶體屬於哪個 NUMA 節點」的權威陳述</b>。
///
/// <para>
/// <b>為什麼它與 SLIT 是一對：</b>
/// SLIT 說的是「節點之間的距離」（相對值），SRAT 說的是「誰屬於哪個節點」（歸屬）。
/// 兩者合起來才能回答「這個量測為什麼慢」——<b>資料可能落在別的節點上</b>。
/// 本專案原本只有 SLIT 那一半（<c>numa.slit.nodes</c> 與 <c>numa.distance.*</c>），
/// 這一支補上歸屬那一半。
/// </para>
///
/// <para>
/// <b>界線：這是韌體宣告的歸屬，不是 OS 實際的排程。</b>
/// OS 可以（而且常常）覆寫或忽略它；SRAT 說某個處理器屬於節點 0，
/// 不代表排程器真的把它當節點 0。<b>不一致是事實，不是判決。</b>
/// </para>
/// </summary>
public static class AcpiSrat
{
    public const byte TypeProcessorAffinity = 0;
    public const byte TypeMemoryAffinity = 1;
    public const byte TypeProcessorX2ApicAffinity = 2;
    public const byte TypeGenericAffinity = 5;

    /// <summary>
    /// 走訪全部子結構（自表頭 36 bytes ＋ 保留 12 bytes 之後起）。
    /// 型別與長度都是 8 位元；長度不可信即停。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.40（SRAT）：Header 36 bytes＋Reserved u32@36、Reserved u64@40；其後每子結構 type u8@0、length u8@1")]
    public static IReadOnlyList<(byte Type, byte Length, int Offset)> Structures(ReadOnlySpan<byte> table)
    {
        if (table.Length < AcpiTable.HeaderLength + 12 || !table[..4].SequenceEqual("SRAT"u8)) return [];
        var list = new List<(byte, byte, int)>();
        int off = AcpiTable.HeaderLength + 12;
        while (off + 2 <= table.Length)
        {
            byte type = table[off];
            byte len = table[off + 1];
            // len < 2 會讓位移不前進（無窮迴圈）；超出表尾代表表壞了。兩者都停。
            if (len < 2 || off + len > table.Length) break;
            list.Add((type, len, off));
            off += len;
        }
        return list;
    }

    /// <summary>表頭的保留欄位（@36）——非零代表表不是我們認得的那個版本。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.40：Reserved u32@36、Reserved u64@40")]
    public static uint? ReservedField(ReadOnlySpan<byte> table)
        => table.Length >= AcpiTable.HeaderLength + 4 && table[..4].SequenceEqual("SRAT"u8)
            ? BitConverter.ToUInt32(table[36..40])
            : null;

    /// <summary>
    /// 處理器親和（型別 0，長 16）：<b>鄰近域高 8 位元在 @2、低 8 位元在 @9</b>——
    /// 這是 SRAT 最反直覺的欄位佈局，也是它最常見的解碼錯誤來源
    /// （看起來像兩個獨立的位元組，其實拼起來才是一個 32 位元的值）。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.41（Processor Local APIC/SAPIC Affinity）：Type u8@0、Length u8@1、Proximity Domain High u8@2、APIC ID u8@3、Flags u32@4、Local SAPIC EID u8@8、Proximity Domain Low u8@9、Clock Domain u32@12（共 16 bytes）")]
    public static IReadOnlyList<SratProcessorAffinity> ProcessorAffinities(ReadOnlySpan<byte> table)
    {
        var list = new List<SratProcessorAffinity>();
        foreach (var (type, len, off) in Structures(table))
        {
            if (type != TypeProcessorAffinity || len < 16) continue;
            uint domain = ((uint)table[off + 2] << 8) | table[off + 9];
            uint flags = BitConverter.ToUInt32(table[(off + 4)..]);
            list.Add(new SratProcessorAffinity(domain, table[off + 3], (flags & 1) != 0, false));
        }
        return list;
    }

    /// <summary>處理器 x2APIC 親和（型別 2，長 24）：x2APIC ID 是 32 位元，鄰近域也是 32 位元。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.42（Processor Local x2APIC Affinity）：Reserved u16@2、Proximity Domain u32@4、X2APIC ID u32@8、Flags u32@12、Clock Domain u32@16（共 24 bytes）")]
    public static IReadOnlyList<SratProcessorAffinity> X2ApicAffinities(ReadOnlySpan<byte> table)
    {
        var list = new List<SratProcessorAffinity>();
        foreach (var (type, len, off) in Structures(table))
        {
            if (type != TypeProcessorX2ApicAffinity || len < 24) continue;
            uint flags = BitConverter.ToUInt32(table[(off + 12)..]);
            list.Add(new SratProcessorAffinity(
                BitConverter.ToUInt32(table[(off + 4)..]),
                BitConverter.ToUInt32(table[(off + 8)..]),
                (flags & 1) != 0, true));
        }
        return list;
    }

    /// <summary>
    /// 記憶體親和（型別 1，長 40）：一個鄰近域涵蓋哪一段實體位址。
    /// 位址與長度都是 64 位元——<b>把它們當 32 位元讀會讓高位被截掉</b>，
    /// 而截掉之後的位址看起來仍然「像個位址」。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.43（Memory Affinity）：Proximity Domain u32@2、Reserved u16@6、Base Address u64@8、Length u64@16、Reserved u32@24、Flags u32@28（共 40 bytes）")]
    public static IReadOnlyList<SratMemoryAffinity> MemoryAffinities(ReadOnlySpan<byte> table)
    {
        var list = new List<SratMemoryAffinity>();
        foreach (var (type, len, off) in Structures(table))
        {
            if (type != TypeMemoryAffinity || len < 40) continue;
            uint flags = BitConverter.ToUInt32(table[(off + 28)..]);
            list.Add(new SratMemoryAffinity(
                BitConverter.ToUInt32(table[(off + 2)..]),
                BitConverter.ToUInt64(table[(off + 8)..]),
                BitConverter.ToUInt64(table[(off + 16)..]),
                (flags & 1) != 0));
        }
        return list;
    }

    /// <summary>一般親和（型別 5，長 16）：裝置控制代碼與它的鄰近域。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.44（Generic Initiator Affinity）：Reserved u8@2、Device Handle Type u8@3、Proximity Domain u32@4、Device Handle u32@8、Flags u32@12（共 16 bytes）")]
    public static IReadOnlyList<SratGenericAffinity> GenericAffinities(ReadOnlySpan<byte> table)
    {
        var list = new List<SratGenericAffinity>();
        foreach (var (type, len, off) in Structures(table))
        {
            if (type != TypeGenericAffinity || len < 16) continue;
            list.Add(new SratGenericAffinity(
                BitConverter.ToUInt32(table[(off + 4)..]),
                table[off + 3],
                BitConverter.ToUInt32(table[(off + 8)..]),
                BitConverter.ToUInt32(table[(off + 12)..])));
        }
        return list;
    }

    /// <summary>依型別統計——未收錄的型別要留下來而不是丟掉。</summary>
    public static IReadOnlyDictionary<byte, int> CountByType(ReadOnlySpan<byte> table)
    {
        var counts = new Dictionary<byte, int>();
        foreach (var (type, _, _) in Structures(table))
            counts[type] = counts.TryGetValue(type, out int n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>型別的中文名；認不得的型別回 null（呼叫端要把它當事實說出來）。</summary>
    public static string? DescribeType(byte type) => type switch
    {
        TypeProcessorAffinity => "處理器親和",
        TypeMemoryAffinity => "記憶體親和",
        TypeProcessorX2ApicAffinity => "處理器 x2APIC 親和",
        3 => "GICC 親和",
        4 => "GIC ITS 親和",
        TypeGenericAffinity => "一般啟動器親和",
        _ => null,
    };
}
