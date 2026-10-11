namespace XinSpect;

/// <summary>BGRT 開機圖形資源表：版本／狀態／影像型別與位置（ACPI 5.0 起）。</summary>
public readonly record struct BgrtInfo(ushort Version, bool Displayed, byte ImageType, ulong ImageAddress, uint OffsetX, uint OffsetY);

/// <summary>SPCR 序列主控台表：介面型別與暫存器基底（Generic Address Structure）。</summary>
public readonly record struct SpcrInfo(byte InterfaceType, byte AddressSpaceId, ulong BaseAddress);

/// <summary>DBG2 除錯埠表：除錯裝置個數與偏移。</summary>
public readonly record struct Dbg2Info(uint InfoOffset, uint InfoCount);

/// <summary>HPET 高精度事件計時器表全欄位。</summary>
public readonly record struct HpetInfo(uint HardwareId, byte AddressSpaceId, ulong BaseAddress, byte Sequence, ushort MinimumTick, byte PageProtectFlags);

/// <summary>NFIT 子結構標頭（型別＋長度）。</summary>
public readonly record struct NfitStructure(ushort Type, ushort Length);

/// <summary>NFIT SPA（系統實體位址）範圍結構：索引／旗標／鄰近域／位址／長度。</summary>
public readonly record struct NfitSpaRange(ushort RangeIndex, ushort Flags, uint ProximityDomain, ulong Address, ulong Length);

/// <summary>NFIT 記憶體裝置映射結構：控制代碼／區索引／交錯通道數。</summary>
public readonly record struct NfitMemoryDevice(uint DeviceHandle, ushort RegionId, ushort InterleaveWays, ulong RegionSize);

/// <summary>SSDT 來源對照結果：平台主 DSDT 的 OEM ID，以及 OEM ID 不同的 SSDT 清單。</summary>
public sealed record SsdtSourceReport(string PlatformOemId, IReadOnlyList<string> ThirdPartyOemIds);

/// <summary>
/// ACPI 表層（非 AML 評估）的純解碼器。所有欄位位移取自 Linux ACPICA 標頭
/// <c>include/acpi/actbl1.h</c> 與 <c>actbl2.h</c>（2026-10-11 抓取核對）與 ACPI 規格表定義。
/// <b>界線：本解碼器不評估 AML 位元碼</b>（_PSS／_CPC／_DSM 等需要 AML 解譯器者一律不在這裡），
/// 也不宣稱把表內容解成語意——只把靜態欄位攤成事實。
/// </summary>
public static class AcpiDeep
{
    /// <summary>
    /// 在 DSDT/SSDT 位元碼中掃出可列印 ASCII 字串（長度 >= minLength）。
    /// 這是<b>詞法掃描不是反編譯</b>：只找連續可列印位元組，不解譯 opcode、不重建 AML 樹——
    /// 所以「掃到什麼字串」是事實，「這些字串代表什麼」不是本方法能回答的。
    /// </summary>
    [SpecRef("ACPI Spec §19 AML 位元碼；ASL 編譯器把字串以可列印 ASCII 連續位元組寫入——詞法掃描可列印區段，非反編譯")]
    public static IReadOnlyList<string> AmlStrings(ReadOnlySpan<byte> table, int minLength = 4)
    {
        if (minLength < 1) minLength = 1;
        var result = new List<string>();
        int i = AcpiTable.HeaderLength; // 跳過表頭，只看 body
        while (i < table.Length)
        {
            if (IsPrintable(table[i]))
            {
                int start = i;
                while (i < table.Length && IsPrintable(table[i])) i++;
                if (i - start >= minLength)
                    result.Add(System.Text.Encoding.ASCII.GetString(table[start..i]));
            }
            else i++;
        }
        return result;

        static bool IsPrintable(byte b) => b is >= 0x20 and <= 0x7E;
    }

    /// <summary>
    /// SSDT 來源對照：拿 DSDT 的 OEM ID 當平台基準，列出 OEM ID 與之不同的 SSDT 的 OEM Table ID。
    /// <b>界線：OEM ID 不同不等於惡意</b>——第三方 SSDT 在 OEM 與虛擬化平台上都是常態，
    /// 本方法只回答「有幾張表不是主 DSDT 廠商簽的」，不下判決。
    /// </summary>
    [SpecRef("ACPI Spec：每張表的標頭含 OEMID（@10）與 OEM Table ID（@16），DSDT 與 SSDT 由不同廠商簽署屬平台事實；不對簽署差異作惡意推論")]
    public static SsdtSourceReport SsdtSources(IReadOnlyList<AcpiTableHeader> headers)
    {
        string platform = headers.FirstOrDefault(h => h.Signature == "DSDT").OemId ?? "";
        var third = new List<string>();
        foreach (var h in headers)
        {
            if (h.Signature != "SSDT") continue;
            string oem = h.OemId.Length > 0 ? h.OemId : "(空)";
            if (!string.Equals(oem, platform, StringComparison.Ordinal))
                third.Add(h.OemTableId.Length > 0 ? $"{oem}/{h.OemTableId}" : oem);
        }
        return new SsdtSourceReport(platform, third);
    }

    /// <summary>NFIT 子結構型別與長度清單（表頭 36 bytes＋保留 4 bytes 後起）。非 NFIT 或過短回空。</summary>
    [SpecRef("ACPI 6.0+ NFIT（Linux actbl2.h acpi_table_nfit）：表頭 36B＋保留 u32@36；每子結構開頭型別 u16@0、長度 u16@2")]
    public static IReadOnlyList<NfitStructure> NfitStructures(ReadOnlySpan<byte> table)
    {
        if (!IsNfit(table)) return [];
        var list = new List<NfitStructure>();
        int off = 40;
        while (off + 4 <= table.Length)
        {
            ushort type = BitConverter.ToUInt16(table[off..(off + 2)]);
            ushort len = BitConverter.ToUInt16(table[(off + 2)..(off + 4)]);
            if (len < 4 || off + len > table.Length) break; // 長度不可信即停，不硬讀
            list.Add(new NfitStructure(type, len));
            off += len;
        }
        return list;
    }

    /// <summary>
    /// NFIT SPA 範圍結構（型別 0）：索引 u16@4、旗標 u16@6、鄰近域 u32@12、範圍 GUID 16B@16、
    /// 位址 u64@32、長度 u64@40。非 NFIT 或無此型別回空。
    /// </summary>
    [SpecRef("ACPI 6.0+ NFIT SPA Range Structure（actbl2.h acpi_nfit_system_address）：header 4B＋range_index u16@4＋flags u16@6＋reserved u32@8＋proximity_domain u32@12＋range_guid[16]@16＋address u64@32＋length u64@40（60B 結構）")]
    public static IReadOnlyList<NfitSpaRange> NfitSpaRanges(ReadOnlySpan<byte> table)
    {
        var list = new List<NfitSpaRange>();
        int off = 40;
        while (off + 4 <= table.Length)
        {
            ushort type = BitConverter.ToUInt16(table[off..(off + 2)]);
            ushort len = BitConverter.ToUInt16(table[(off + 2)..(off + 4)]);
            if (len < 4 || off + len > table.Length) break;
            if (type == 0 && len >= 60)
                list.Add(new NfitSpaRange(
                    BitConverter.ToUInt16(table[(off + 4)..(off + 6)]),
                    BitConverter.ToUInt16(table[(off + 6)..(off + 8)]),
                    BitConverter.ToUInt32(table[(off + 12)..(off + 16)]),
                    BitConverter.ToUInt64(table[(off + 32)..(off + 40)]),
                    BitConverter.ToUInt64(table[(off + 40)..(off + 48)])));
            off += len;
        }
        return list;
    }

    /// <summary>
    /// NFIT 記憶體裝置映射結構（型別 1）：控制代碼 u32@4、區索引 u16@10、交錯通道數 u16@42、區大小 u64@16。
    /// </summary>
    [SpecRef("ACPI 6.0+ NFIT Memory Device to SPA Range Map（actbl2.h acpi_nfit_memory_map）：header 4B＋device_handle u32@4＋physical_id u16@8＋region_id u16@10＋range_index u16@12＋region_index u16@14＋region_size u64@16＋…＋interleave_ways u16@42（48B 結構）")]
    public static IReadOnlyList<NfitMemoryDevice> NfitMemoryDevices(ReadOnlySpan<byte> table)
    {
        var list = new List<NfitMemoryDevice>();
        int off = 40;
        while (off + 4 <= table.Length)
        {
            ushort type = BitConverter.ToUInt16(table[off..(off + 2)]);
            ushort len = BitConverter.ToUInt16(table[(off + 2)..(off + 4)]);
            if (len < 4 || off + len > table.Length) break;
            if (type == 1 && len >= 48)
                list.Add(new NfitMemoryDevice(
                    BitConverter.ToUInt32(table[(off + 4)..(off + 8)]),
                    BitConverter.ToUInt16(table[(off + 10)..(off + 12)]),
                    BitConverter.ToUInt16(table[(off + 42)..(off + 44)]),
                    BitConverter.ToUInt64(table[(off + 16)..(off + 24)])));
            off += len;
        }
        return list;
    }

    /// <summary>
    /// BGRT：版本 u16@36、狀態 u8@38（bit0＝開機 logo 已顯示）、影像型別 u8@39、
    /// 影像位址 u64@40、水平位移 u32@48、垂直位移 u32@52。非 BGRT 或過短回 null。
    /// </summary>
    [SpecRef("ACPI 5.0 BGRT（actbl1.h acpi_table_bgrt）：header 36B＋version u16@36＋status u8@38（bit0 ACPI_BGRT_DISPLAYED）＋image_type u8@39＋image_address u64@40＋image_offset_x u32@48＋image_offset_y u32@52")]
    public static BgrtInfo? Bgrt(ReadOnlySpan<byte> table)
    {
        if (table.Length < 56 || !table[..4].SequenceEqual("BGRT"u8)) return null;
        return new BgrtInfo(
            BitConverter.ToUInt16(table[36..38]), (table[38] & 1) != 0, table[39],
            BitConverter.ToUInt64(table[40..48]),
            BitConverter.ToUInt32(table[48..52]), BitConverter.ToUInt32(table[52..56]));
    }

    /// <summary>
    /// SPCR：介面型別 u8@36（0＝完整 16550、1＝子集），序列埠為 Generic Address Structure（12B@40：
    /// 位址空間 u8@40、位寬 u8@41、位偏移 u8@42、存取大小 u8@43、位址 u64@44）。非 SPCR 或過短回 null。
    /// </summary>
    [SpecRef("ACPI SPCR（actbl1.h acpi_table_spcr）：header 36B＋interface_type u8@36＋reserved[3]＋serial_port GenericAddress@40（GAS：address_space_id u8@40、register_bit_width u8@41、register_bit_offset u8@42、access_size u8@43、address u64@44）")]
    public static SpcrInfo? Spcr(ReadOnlySpan<byte> table)
    {
        if (table.Length < 52 || !table[..4].SequenceEqual("SPCR"u8)) return null;
        return new SpcrInfo(table[36], table[40], BitConverter.ToUInt64(table[44..52]));
    }

    /// <summary>
    /// DBG2：子結構表位移 u32@36 與除錯裝置個數 u32@40。只報存在性與個數——
    /// 逐裝置子結構為變長且欄位位移依 Microsoft DBG2 規格，未在本機驗證前不出逐裝置細節。
    /// </summary>
    [SpecRef("Microsoft Debug Port Table 2（DBG2，actbl1.h acpi_table_dbg2）：header 36B＋info_offset u32@36＋info_count u32@40")]
    public static Dbg2Info? Dbg2(ReadOnlySpan<byte> table)
    {
        if (table.Length < 44 || !table[..4].SequenceEqual("DBG2"u8)) return null;
        return new Dbg2Info(BitConverter.ToUInt32(table[36..40]), BitConverter.ToUInt32(table[40..44]));
    }

    /// <summary>
    /// HPET 全欄位：硬體 ID u32@36、基底位址 GAS（位址空間 u8@40、位址 u64@44）、
    /// 序列號 u8@52、最小刻度 u16@54、頁保護 u8@56。非 HPET 或過短回 null。
    /// </summary>
    [SpecRef("ACPI HPET（actbl1.h acpi_table_hpet）：header 36B＋id u32@36＋address GenericAddress@40（address_space_id u8@40、address u64@44）＋sequence u8@52＋minimum_tick u16@54＋flags u8@56")]
    public static HpetInfo? Hpet(ReadOnlySpan<byte> table)
    {
        if (table.Length < 57 || !table[..4].SequenceEqual("HPET"u8)) return null;
        return new HpetInfo(
            BitConverter.ToUInt32(table[36..40]), table[40],
            BitConverter.ToUInt64(table[44..52]), table[52],
            BitConverter.ToUInt16(table[54..56]), table[56]);
    }

    /// <summary>MPAM 表存在性與 OEM Table ID（表頭即足；內容為資源分割設定，未納入解碼範圍）。</summary>
    [SpecRef("ACPI 6.4 MPAM（Linux actbl2.h：ACPI_SIG_MPAM）；只報存在性——表內容為 MPAM 資源分割設定，未對準欄位前不出值")]
    public static bool IsMpam(ReadOnlySpan<byte> table) => table.Length >= AcpiTable.HeaderLength && table[..4].SequenceEqual("MPAM"u8);

    private static bool IsNfit(ReadOnlySpan<byte> table) =>
        table.Length >= 40 && table[..4].SequenceEqual("NFIT"u8);
}
