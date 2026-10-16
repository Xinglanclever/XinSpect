namespace XinSpect;

/// <summary>MADT 子結構標頭：型別、長度、在表中的位移。</summary>
public readonly record struct MadtStructure(byte Type, byte Length, int Offset);

/// <summary>MADT 的 Local APIC（型別 0）：處理器編號與 APIC ID。</summary>
public readonly record struct MadtLocalApic(byte AcpiProcessorId, byte ApicId, uint Flags);

/// <summary>MADT 的 I/O APIC（型別 1）：編號、位址、全域系統中斷基底。</summary>
public readonly record struct MadtIoApic(byte Id, uint Address, uint GlobalSystemInterruptBase);

/// <summary>MADT 的中斷來源覆寫（型別 2）：匯流排、來源、全域系統中斷、旗標。</summary>
public readonly record struct MadtInterruptOverride(
    byte Bus, byte Source, uint GlobalSystemInterrupt, ushort Flags);

/// <summary>
/// MADT 的 Local x2APIC（型別 9）：x2APIC ID 是 32 位元，
/// 而 ACPI Processor UID 也是 32 位元——這是它與型別 0 的實質差別，
/// 那個差別在 255 個處理器以上才會顯現。
/// </summary>
public readonly record struct MadtLocalX2Apic(uint X2ApicId, uint AcpiProcessorUid, uint Flags);

/// <summary>MADT 的 Local x2APIC NMI（型別 10）：ACPI Processor UID 是 32 位元。</summary>
public readonly record struct MadtLocalX2ApicNmi(uint AcpiProcessorUid, byte Lint, ushort Flags);

/// <summary>
/// ACPI MADT（Multiple APIC Description Table）的純解碼器：中斷控制器與處理器清單。
///
/// <para>
/// <b>為什麼這張表值得逐欄解：</b>它同時是「有哪些處理器」與「中斷怎麼送到處理器」的
/// 權威陳述。前者與 CPUID／OS 的處理器數是三個獨立來源，可以互相對照；
/// 後者是 x2APIC 與傳統 PIC 是否並存的直接證據——而那個並存狀態會影響
/// 中斷延遲類量測的解讀。
/// </para>
///
/// <para>
/// <b>界線</b>：只走訪結構與明定欄位，<b>不宣稱中斷設定正確</b>。
/// 韌體說的和 OS 實際用的是不是同一套，由服務層並列陳述——
/// 不一致是事實，不是判決。
/// </para>
/// </summary>
public static class AcpiMadt
{
    // 子結構型別（ACPI 6.5 Table 5.20「APIC Structure Types」）。
    public const byte TypeLocalApic = 0;
    public const byte TypeIoApic = 1;
    public const byte TypeInterruptSourceOverride = 2;
    public const byte TypeNmiSource = 3;
    public const byte TypeLocalApicNmi = 4;
    public const byte TypeLocalApicAddressOverride = 5;
    public const byte TypeIoSapic = 6;
    public const byte TypeLocalSapic = 7;
    public const byte TypePlatformInterruptSource = 8;
    public const byte TypeLocalX2Apic = 9;
    public const byte TypeLocalX2ApicNmi = 10;
    public const byte TypeGicc = 11;
    public const byte TypeGicd = 12;
    public const byte TypeGicMsiFrame = 13;
    public const byte TypeGicRedistributor = 14;
    public const byte TypeGicInterruptTranslationService = 15;
    public const byte TypeMultiprocessorWakeup = 16;

    /// <summary>
    /// 走訪全部子結構（自表頭 36 bytes ＋ Local APIC 位址 4 bytes 之後起）。
    /// <b>長度不可信即停，不硬讀</b>——一個長度為 0 的子結構會造成無限迴圈，
    /// 一個超出表尾的長度代表表本身壞了，兩者都不該被猜補。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.19（MADT）：Header 36 bytes＋Local APIC Address u32@36、Flags u32@40；其後每子結構 type u8@0、length u8@1")]
    public static IReadOnlyList<MadtStructure> Structures(ReadOnlySpan<byte> table)
    {
        if (table.Length < AcpiTable.HeaderLength + 8 || !table[..4].SequenceEqual("MADT"u8)) return [];
        var list = new List<MadtStructure>();
        int off = AcpiTable.HeaderLength + 8;
        while (off + 2 <= table.Length)
        {
            byte type = table[off];
            byte len = table[off + 1];
            // len < 2 會讓位移不前進（無限迴圈）；超出表尾代表表壞了。兩者都停。
            if (len < 2 || off + len > table.Length) break;
            list.Add(new MadtStructure(type, len, off));
            off += len;
        }
        return list;
    }

    /// <summary>MADT 表頭的 Local APIC 位址與旗標（@36／@40）。非 MADT 或過短回 null。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.19：Local APIC Address u32@36、Flags u32@40")]
    public static (uint Address, uint Flags)? Header(ReadOnlySpan<byte> table)
        => table.Length >= AcpiTable.HeaderLength + 8 && table[..4].SequenceEqual("MADT"u8)
            ? (BitConverter.ToUInt32(table[36..40]), BitConverter.ToUInt32(table[40..44]))
            : null;

    /// <summary>
    /// Local APIC 結構（型別 0，長度 8）：ACPI 處理器編號、APIC ID、旗標。
    /// 長度不足 8 的節點略過——不猜缺失欄位。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.20.1（Processor Local APIC Structure）：ACPI Processor ID u8@2、APIC ID u8@3、Flags u32@4")]
    public static IReadOnlyList<MadtLocalApic> LocalApics(ReadOnlySpan<byte> table)
    {
        var list = new List<MadtLocalApic>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeLocalApic || s.Length < 8) continue;
            list.Add(new MadtLocalApic(
                table[s.Offset + 2],
                table[s.Offset + 3],
                BitConverter.ToUInt32(table[(s.Offset + 4)..(s.Offset + 8)])));
        }
        return list;
    }

    /// <summary>I/O APIC 結構（型別 1，長度 12）。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.20.2（I/O APIC Structure）：I/O APIC ID u8@2、Reserved u8@3、Address u32@4、Global System Interrupt Base u32@8")]
    public static IReadOnlyList<MadtIoApic> IoApics(ReadOnlySpan<byte> table)
    {
        var list = new List<MadtIoApic>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeIoApic || s.Length < 12) continue;
            list.Add(new MadtIoApic(
                table[s.Offset + 2],
                BitConverter.ToUInt32(table[(s.Offset + 4)..(s.Offset + 8)]),
                BitConverter.ToUInt32(table[(s.Offset + 8)..(s.Offset + 12)])));
        }
        return list;
    }

    /// <summary>中斷來源覆寫結構（型別 2，長度 10）——ISA IRQ 被改送到哪一個 GSI 的證據。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.20.3（Interrupt Source Override Structure）：Bus u8@2、Source u8@3、Global System Interrupt u32@4、Flags u16@8")]
    public static IReadOnlyList<MadtInterruptOverride> InterruptOverrides(ReadOnlySpan<byte> table)
    {
        var list = new List<MadtInterruptOverride>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeInterruptSourceOverride || s.Length < 10) continue;
            list.Add(new MadtInterruptOverride(
                table[s.Offset + 2],
                table[s.Offset + 3],
                BitConverter.ToUInt32(table[(s.Offset + 4)..(s.Offset + 8)]),
                BitConverter.ToUInt16(table[(s.Offset + 8)..(s.Offset + 10)])));
        }
        return list;
    }

    /// <summary>
    /// Local x2APIC 結構（型別 9，長度 16）：x2APIC ID 是 <b>32 位元</b>——
    /// 這是它與型別 0 的唯一實質差別，而那個差別在 255 個處理器以上才顯現。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.20.8（Processor Local x2APIC Structure）：Reserved u16@2、Processor ID u32@4、Flags u32@8、ACPI Processor UID u32@12")]
    public static IReadOnlyList<MadtLocalX2Apic> LocalX2Apics(ReadOnlySpan<byte> table)
    {
        var list = new List<MadtLocalX2Apic>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeLocalX2Apic || s.Length < 16) continue;
            list.Add(new MadtLocalX2Apic(
                BitConverter.ToUInt32(table[(s.Offset + 4)..(s.Offset + 8)]),
                BitConverter.ToUInt32(table[(s.Offset + 12)..(s.Offset + 16)]),
                BitConverter.ToUInt32(table[(s.Offset + 8)..(s.Offset + 12)])));
        }
        return list;
    }

    /// <summary>Local x2APIC NMI 結構（型別 10，長度 12）。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.20.9（Local x2APIC NMI Structure）：Flags u16@2、ACPI Processor UID u32@4、LINT# u8@8")]
    public static IReadOnlyList<MadtLocalX2ApicNmi> LocalX2ApicNmis(ReadOnlySpan<byte> table)
    {
        var list = new List<MadtLocalX2ApicNmi>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeLocalX2ApicNmi || s.Length < 12) continue;
            list.Add(new MadtLocalX2ApicNmi(
                BitConverter.ToUInt32(table[(s.Offset + 4)..(s.Offset + 8)]),
                table[s.Offset + 8],
                BitConverter.ToUInt16(table[(s.Offset + 2)..(s.Offset + 4)])));
        }
        return list;
    }

    /// <summary>
    /// 依型別統計子結構。<b>未收錄的型別要留下來而不是丟掉</b>——
    /// 「這張表裡有一個我不認得的結構」本身是事實，也是解碼器覆蓋率的證據。
    /// </summary>
    public static IReadOnlyDictionary<byte, int> CountByType(ReadOnlySpan<byte> table)
    {
        var counts = new Dictionary<byte, int>();
        foreach (var s in Structures(table))
            counts[s.Type] = counts.TryGetValue(s.Type, out int n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>型別的中文名；認不得的型別回 null（呼叫端要把它當事實說出來，不是當錯誤）。</summary>
    public static string? DescribeType(byte type) => type switch
    {
        TypeLocalApic => "Local APIC",
        TypeIoApic => "I/O APIC",
        TypeInterruptSourceOverride => "中斷來源覆寫",
        TypeNmiSource => "NMI 來源",
        TypeLocalApicNmi => "Local APIC NMI",
        TypeLocalApicAddressOverride => "Local APIC 位址覆寫",
        TypeIoSapic => "I/O SAPIC",
        TypeLocalSapic => "Local SAPIC",
        TypePlatformInterruptSource => "平台中斷來源",
        TypeLocalX2Apic => "Local x2APIC",
        TypeLocalX2ApicNmi => "Local x2APIC NMI",
        TypeGicc => "GIC CPU 介面",
        TypeGicd => "GIC 配置器",
        TypeGicMsiFrame => "GIC MSI Frame",
        TypeGicRedistributor => "GIC Redistributor",
        TypeGicInterruptTranslationService => "GIC ITS",
        TypeMultiprocessorWakeup => "多處理器喚醒",
        _ => null,
    };
}
