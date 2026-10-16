namespace XinSpect;

/// <summary>DMAR 子結構標頭：型別（u16）與長度（u16）。</summary>
public readonly record struct DmarStructure(ushort Type, ushort Length, int Offset);

/// <summary>DMAR 的 DRHD（型別 0）：硬體單位暫存器基底、範圍涵蓋全機、裝置範圍段。</summary>
public readonly record struct DmarDrhd(
    byte Flags, ushort Segment, ulong RegisterBase,
    bool CoversWholeMachine, IReadOnlyList<(ushort Start, ushort End)> DeviceScopes);

/// <summary>DMAR 的 RMRR（型別 1）：保留記憶體區域——那些區域不受 IOMMU 保護。</summary>
public readonly record struct DmarRmrr(
    ushort Segment, ulong BaseAddress, ulong Limit,
    IReadOnlyList<(ushort Start, ushort End)> DeviceScopes);

/// <summary>DMAR 的 ATSR（型別 2）：支援 ATS 的裝置範圍。</summary>
public readonly record struct DmarAtsr(
    byte Flags, ushort Segment, IReadOnlyList<(ushort Start, ushort End)> DeviceScopes);

/// <summary>
/// ACPI DMAR（DMA Remapping Reporting Table）的純解碼器：IOMMU 的範圍與例外。
///
/// <para>
/// <b>為什麼這張表同時是效能事實與安全事實：</b>
/// DMAR 宣告「哪些裝置的 DMA 要經過 IOMMU 轉譯」。
/// 而 <c>RMRR</c>（Reserved Memory Region Reporting）宣告的是
/// <b>「這些記憶體區域不受那個保護」</b>——它是 IOMMU 保護的<b>例外清單</b>。
/// </para>
///
/// <para>
/// <b>界線：這裡只陳述韌體宣告了什麼，不判定保護是否有效。</b>
/// DMAR 說有 RMRR，不等於那個區域現在沒有被保護（OS 可能另外處理），
/// 也不等於有被保護（IOMMU 可能沒啟用）。這張表的角色是<b>指出保護的邊界在哪裡</b>，
/// 讓使用者知道「這一塊是設計上留的洞，不是漏掉的」。
/// </para>
/// </summary>
public static class AcpiDmar
{
    // 子結構型別（ACPI 6.5 Table 5.24「DMA Remapping Structure Types」）。
    public const ushort TypeDrhd = 0;
    public const ushort TypeRmrr = 1;
    public const ushort TypeAtsr = 2;
    public const ushort TypeRhsa = 3;
    public const ushort TypeAndd = 4;
    public const ushort TypeSidp = 5;

    /// <summary>
    /// 走訪全部子結構（自表頭 36 bytes ＋ 旗標 1 byte ＋ 保留 10 bytes 之後起）。
    /// <b>型別與長度都是 16 位元</b>；長度不可信即停，不硬讀。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.23（DMAR）：Header 36 bytes＋Host Address Width u8@36、Flags u8@37、Reserved u8×10@38；其後每子結構 type u16@0、length u16@2")]
    public static IReadOnlyList<DmarStructure> Structures(ReadOnlySpan<byte> table)
    {
        if (table.Length < AcpiTable.HeaderLength + 12 || !table[..4].SequenceEqual("DMAR"u8)) return [];
        var list = new List<DmarStructure>();
        int off = AcpiTable.HeaderLength + 12;
        while (off + 4 <= table.Length)
        {
            ushort type = BitConverter.ToUInt16(table[off..]);
            ushort len = BitConverter.ToUInt16(table[(off + 2)..]);
            // len < 4 會讓位移不前進（無窮迴圈）；超出表尾代表表壞了。兩者都停。
            if (len < 4 || off + len > table.Length) break;
            list.Add(new DmarStructure(type, len, off));
            off += len;
        }
        return list;
    }

    /// <summary>DMAR 表頭的 Host Address Width 與旗標（@36／@37）。非 DMAR 或過短回 null。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.23：Host Address Width u8@36、Flags u8@37（bit0 INTR_REMAP、bit1 X2APIC_OPT_OUT、bit2 DMA_CTRL_PLATFORM_OPT_IN）")]
    public static (byte HostAddressWidth, byte Flags)? Header(ReadOnlySpan<byte> table)
        => table.Length >= AcpiTable.HeaderLength + 2 && table[..4].SequenceEqual("DMAR"u8)
            ? (table[36], table[37])
            : null;

    /// <summary>
    /// DRHD（型別 0）：一個 IOMMU 硬體單位的暫存器基底與它負責的裝置範圍。
    /// <b>CoversWholeMachine（旗標 bit0）</b>是關鍵欄位——它宣告這個單位是否涵蓋全機；
    /// 沒有任何一個 DRHD 涵蓋全機，代表有些裝置的 DMA <b>不在這份表的管理範圍內</b>。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.25（DRHD Structure）：Flags u8@4、Reserved u8@5、Segment Number u16@6、Register Base Address u64@8；其後每裝置範圍 4 bytes：Device/Function u16、Reserved u16（固定部分共 16 bytes）")]
    public static IReadOnlyList<DmarDrhd> Drhds(ReadOnlySpan<byte> table)
    {
        var list = new List<DmarDrhd>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeDrhd || s.Length < 16) continue;
            byte flags = table[s.Offset + 4];
            list.Add(new DmarDrhd(
                flags,
                BitConverter.ToUInt16(table[(s.Offset + 6)..]),
                BitConverter.ToUInt64(table[(s.Offset + 8)..]),
                (flags & 1) != 0,
                DeviceScopes(table, s)));
        }
        return list;
    }

    /// <summary>
    /// RMRR（型別 1）：<b>保留記憶體區域——那些區域是 IOMMU 保護的例外</b>。
    /// 每個 RMRR 宣告一段基底～上限的實體記憶體，以及哪些裝置可以不受轉譯存取它
    /// （典型是舊式 USB／顯示控制器需要固定實體位址）。
    /// </summary>
    [SpecRef("ACPI Spec r6.5, Table 5.26（RMRR Structure）：Reserved u16@4、Segment Number u16@6、Reserved Memory Region Base Address u64@8、Reserved Memory Region Limit Address u64@16；其後每裝置範圍 4 bytes（固定部分共 24 bytes）")]
    public static IReadOnlyList<DmarRmrr> Rmrrs(ReadOnlySpan<byte> table)
    {
        var list = new List<DmarRmrr>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeRmrr || s.Length < 24) continue;
            list.Add(new DmarRmrr(
                BitConverter.ToUInt16(table[(s.Offset + 6)..]),
                BitConverter.ToUInt64(table[(s.Offset + 8)..]),
                BitConverter.ToUInt64(table[(s.Offset + 16)..]),
                DeviceScopes(table, s)));
        }
        return list;
    }

    /// <summary>ATSR（型別 2）：支援位址轉譯服務的裝置範圍。</summary>
    [SpecRef("ACPI Spec r6.5, Table 5.27（ATSR Structure）：Flags u8@4、Reserved u8@5、Segment Number u16@6；其後每裝置範圍 4 bytes（固定部分共 8 bytes）")]
    public static IReadOnlyList<DmarAtsr> Atsrs(ReadOnlySpan<byte> table)
    {
        var list = new List<DmarAtsr>();
        foreach (var s in Structures(table))
        {
            if (s.Type != TypeAtsr || s.Length < 8) continue;
            list.Add(new DmarAtsr(
                table[s.Offset + 4],
                BitConverter.ToUInt16(table[(s.Offset + 6)..]),
                DeviceScopes(table, s)));
        }
        return list;
    }

    /// <summary>
    /// 子結構的裝置範圍段（每段 4 bytes：Device/Function u16 ＋ 保留 u16，共 2 bytes 有效）。
    /// <b>0xFFFF 是範圍結束標記</b>，不是一個真實的裝置編號——把它當裝置數會多算一個。
    /// </summary>
    private static IReadOnlyList<(ushort Start, ushort End)> DeviceScopes(
        ReadOnlySpan<byte> table, DmarStructure s)
    {
        int fixedLen = s.Type switch
        {
            TypeDrhd => 16,
            TypeRmrr => 24,
            TypeAtsr => 8,
            _ => 4,
        };
        var scopes = new List<(ushort, ushort)>();
        int p = s.Offset + fixedLen;
        int end = s.Offset + s.Length;
        while (p + 4 <= end && p + 4 <= table.Length)
        {
            ushort start = BitConverter.ToUInt16(table[p..]);
            ushort finish = BitConverter.ToUInt16(table[(p + 2)..]);
            // 0xFFFF 是結束標記；遇到就停，不把它當一段。
            if (start == 0xFFFF) break;
            scopes.Add((start, finish));
            p += 4;
        }
        return scopes;
    }

    /// <summary>子結構型別的總數（含本版沒有解碼器的）——覆蓋率申報用。</summary>
    public static IReadOnlyDictionary<ushort, int> CountByType(ReadOnlySpan<byte> table)
    {
        var counts = new Dictionary<ushort, int>();
        foreach (var s in Structures(table))
            counts[s.Type] = counts.TryGetValue(s.Type, out int n) ? n + 1 : 1;
        return counts;
    }

    /// <summary>型別的中文名；認不得的型別回 null（呼叫端要把它當事實說出來）。</summary>
    public static string? DescribeType(ushort type) => type switch
    {
        TypeDrhd => "硬體單位（DRHD）",
        TypeRmrr => "保留記憶體區域（RMRR）",
        TypeAtsr => "位址轉譯服務（ATSR）",
        TypeRhsa => "保留記憶體區域（RHSA）",
        TypeAndd => "ACPI 具名裝置（ANDD）",
        TypeSidp => "SID 對照（SIDP）",
        _ => null,
    };
}
