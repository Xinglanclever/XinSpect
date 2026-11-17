using System.Buffers.Binary;

namespace XinSpect;

/// <summary>
/// CPER <c>WHEA_MEMORY_ERROR_SECTION</c>（U10 第三圈 WH-0003／WH-0004）的解碼結果。
/// </summary>
/// <param name="ValidBits">哪些欄位「韌體說它有意義」——不是每個欄位都存在。</param>
/// <param name="ErrorStatusRaw">WHEA_ERROR_STATUS 原始 64 位元。</param>
/// <param name="ErrorStatusType">ErrorStatus 的 ErrorType 欄（bit 8–15）。</param>
/// <param name="ErrorStatusFlags">Address／Control／Data／Responder／Requester／FirstError／Overflow 旗標。</param>
/// <param name="PhysicalAddress">出錯的實體位址（僅在 ValidBits 說它有義時才算）。</param>
/// <param name="PhysicalAddressMask">位址遮罩（可指出是哪個位元範圍出錯）。</param>
/// <param name="Node">NUMA 節點。</param>
/// <param name="Card">卡號。</param>
/// <param name="Module">模組號（DIMM 的 SMBIOS handle 對應）。</param>
/// <param name="Bank">bank 號。</param>
/// <param name="Device">裝置號（DRAM 顆粒）。</param>
/// <param name="Row">列位址。</param>
/// <param name="Column">行位址。</param>
/// <param name="BitPosition">出錯的位元位置。</param>
/// <param name="ErrorType">WHEA 記憶體錯誤類型碼（0x00–0x0C）。</param>
/// <param name="RankNumber">rank 號。</param>
/// <param name="CardHandle">SMBIOS 卡 handle。</param>
/// <param name="ModuleHandle">SMBIOS 模組 handle。</param>
public sealed record WheaMemorySection(
    ulong ValidBits,
    ulong ErrorStatusRaw,
    byte ErrorStatusType,
    WheaErrorStatusFlags ErrorStatusFlags,
    ulong PhysicalAddress,
    ulong PhysicalAddressMask,
    ushort Node,
    ushort Card,
    ushort Module,
    ushort Bank,
    ushort Device,
    ushort Row,
    ushort Column,
    ushort BitPosition,
    byte ErrorType,
    ushort RankNumber,
    ushort CardHandle,
    ushort ModuleHandle)
{
    /// <summary>
    /// 韌體說「實體位址有意義」（bit 1）。
    /// </summary>
    /// <remarks>
    /// 位元位置照 cper.h 的 <c>WHEA_MEMORY_ERROR_SECTION_VALIDBITS</c> 宣告順序：
    /// bit0 ErrorStatus、bit1 PhysicalAddress、bit2 PhysicalAddressMask、bit3 Node、bit4 Card、
    /// bit5 Module、bit6 Bank、bit7 Device、bit8 Row、bit9 Column、bit10 BitPosition、
    /// bit11 RequesterId、bit12 ResponderId、bit13 TargetId、bit14 ErrorType、bit15 RankNumber、
    /// bit16 CardHandle、bit17 ModuleHandle。
    /// </remarks>
    public bool HasPhysicalAddress => (ValidBits & (1UL << 1)) != 0;

    /// <summary>韌體說「模組號有意義」（bit 5）——這是能把錯誤指到某條 DIMM 的欄位之一。</summary>
    public bool HasModule => (ValidBits & (1UL << 5)) != 0;

    /// <summary>韌體說「出錯位元位置有意義」（bit 10）——單一位元 vs 多位的分界線索。</summary>
    public bool HasBitPosition => (ValidBits & (1UL << 10)) != 0;

    /// <summary>韌體說「ErrorType 有意義」（bit 14）。</summary>
    public bool HasErrorType => (ValidBits & (1UL << 14)) != 0;

    /// <summary>
    /// 韌體說「ModuleHandle 有意義」（bit 17）——這是能跟 <c>sb.*</c> 的 SMBIOS 記憶體裝置表對起來的欄位。
    /// </summary>
    public bool HasModuleHandle => (ValidBits & (1UL << 17)) != 0;

    public bool HasErrorStatus => (ValidBits & (1UL << 0)) != 0;
    public bool HasPhysicalAddressMask => (ValidBits & (1UL << 2)) != 0;
    public bool HasNode => (ValidBits & (1UL << 3)) != 0;
    public bool HasCard => (ValidBits & (1UL << 4)) != 0;
    public bool HasBank => (ValidBits & (1UL << 6)) != 0;
    public bool HasDevice => (ValidBits & (1UL << 7)) != 0;
    public bool HasRow => (ValidBits & (1UL << 8)) != 0;
    public bool HasColumn => (ValidBits & (1UL << 9)) != 0;
    public bool HasRequesterId => (ValidBits & (1UL << 11)) != 0;
    public bool HasResponderId => (ValidBits & (1UL << 12)) != 0;
    public bool HasTargetId => (ValidBits & (1UL << 13)) != 0;
    public bool HasRankNumber => (ValidBits & (1UL << 15)) != 0;
    public bool HasCardHandle => (ValidBits & (1UL << 16)) != 0;
    public ulong RequesterId { get; init; }
    public ulong ResponderId { get; init; }
    public ulong TargetId { get; init; }
}

/// <summary>WHEA_ERROR_STATUS 的旗標位（cper.h：bit 8–15 是 ErrorType，16 起是旗標）。</summary>
[Flags]
public enum WheaErrorStatusFlags
{
    None = 0,
    Address = 1 << 0,
    Control = 1 << 1,
    Data = 1 << 2,
    Responder = 1 << 3,
    Requester = 1 << 4,
    FirstError = 1 << 5,
    Overflow = 1 << 6,
}

/// <summary>
/// CPER 記憶體錯誤節區的解碼器（U10 第三圈 WH-0003＋WH-0004）。純位元組解析，不碰事件記錄。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要它：</b>既有事實（<c>os.ev.whea.*</c>）只回答「有幾筆 WHEA 事件」。
/// 那個數字回答不了「哪一條 DIMM、哪個位元、單一位元還是多位」——而使用者看到
/// 「已修正的硬體錯誤」時問的正是這三件事。
/// </para>
/// <para>
/// <b>版面對準 Windows SDK <c>um/cper.h</c></b>（10.0.26100.0）的 <c>CPER_FIELD_CHECK</c>：
/// 那幾行就是編譯器會驗的位移宣告，比檔案內的 struct 宣告更硬（struct 有對齊填充，
/// <c>CPER_FIELD_CHECK</c> 是逐欄位元組數）。每個位移都抄在那裡，不是算出來的。
/// </para>
/// <para>
/// <b>界線：</b>只解記憶體節區。處理器節區（<c>WHEA_XPF_*</c>）、PCIe 節區（<c>WHEA_PCIE_*</c>）、
/// 韌體錯誤資訊（<c>WHEA_FIRMWARE_ERROR_RECORD_REFERENCE</c>）都不在本版範圍；
/// <c>Extended</c> 位元組與擴充欄位也不解——本版未核定其位義。
/// </para>
/// </remarks>
public static class WheaMemorySectionDecoder
{
    /// <summary>
    /// 節區固定長度。<c>ModuleHandle</c>（位移 78、2 位元組）是宣告的最後一欄，結束在 80。
    /// </summary>
    /// <remarks>
    /// <b>這個數字是算出來的，不是查來的：</b>cper.h 的 <c>CPER_FIELD_CHECK</c> 只驗到
    /// <c>ErrorType</c>（位移 72），後面三個欄位（<c>Extended</c>73／<c>RankNumber</c>74／
    /// <c>CardHandle</c>76／<c>ModuleHandle</c>78）照 struct 宣告順序接下去。
    /// 取整到 8 之前正好是 80；<c>Extended</c> 是結構內的位元組，本版不解其內容，
    /// 不把它解釋為附加節區存在旗標。
    /// </remarks>
    public const int SectionBytes = 80;

    /// <summary>記憶體錯誤類型碼的上界（<c>WHEA_MEMERRTYPE_MEMORYSPARING</c>＝0x0C）。</summary>
    public const byte MaxKnownErrorType = 0x0C;

    /// <summary>
    /// 記憶體錯誤類型碼 → 中文語意。<b>0x0D 以上回 <c>null</c></b>——不是「未知」，是「本版沒有它的出處」。
    /// </summary>
    /// <remarks>
    /// 這張表的每一格都對應 cper.h 裡一個 <c>#define WHEA_MEMERRTYPE_*</c>；
    /// 沒有 define 的碼不放在表裡，也不猜它「大概」是什麼。
    /// </remarks>
    [SpecRef("cper.h（10.0.26100.0）#define WHEA_MEMERRTYPE_*：UNKNOWN 0x00／NOERROR 0x01／SINGLEBITECC 0x02／"
        + "MULTIBITECC 0x03／SINGLESYMCHIPKILL 0x04／MULTISYMCHIPKILL 0x05／MASTERABORT 0x06／"
        + "TARGETABORT 0x07／PARITYERROR 0x08／WATCHDOGTIMEOUT 0x09／INVALIDADDRESS 0x0A／"
        + "MIRRORBROKEN 0x0B／MEMORYSPARING 0x0C。界線＝上表之外一律回 null，不猜")]
    public static string? ErrorTypeName(byte code) => code switch
    {
        0x00 => "未知（韌體說是錯誤但沒分類）",
        0x01 => "無錯誤（NOERROR——記錄存在但這一節區沒有錯）",
        0x02 => "單一位元 ECC",
        0x03 => "多位 ECC",
        0x04 => "單一符號 chip-kill",
        0x05 => "多符號 chip-kill",
        0x06 => "master abort",
        0x07 => "target abort",
        0x08 => "同位元錯誤",
        0x09 => "看門狗逾時",
        0x0A => "位址無效",
        0x0B => "鏡像已破",
        0x0C => "記憶體備援（sparing）切換",
        _ => null,
    };

    /// <summary>
    /// 類型碼的解讀界線；不能由類型碼推定嚴重度、位置或維修處置。
    /// </summary>
    /// <remarks>
    /// CPER 的 Section Severity 在節區描述子，不在此記憶體節區。
    /// 本解碼器沒有取得該欄，故類型不構成已更正、未更正或故障 FRU 證據。
    /// </remarks>
    [SpecRef("Windows SDK um/cper.h（10.0.26100.0）：WHEA_ERROR_RECORD_SECTION_DESCRIPTOR.SectionSeverity"
        + " CPER_FIELD_CHECK offset48/4；WHEA_MEMORY_ERROR_SECTION.ErrorType offset72/1 與 WHEA_MEMERRTYPE_*"
        + " 獨立列舉類型，不提供維修處置。"
        + "界線＝無 Section Severity、FRU 與平台證據，不能由類型推定更正狀態、故障位置或處置")]
    public static string? ErrorTypeHint(byte code) => code is >= 2 and <= MaxKnownErrorType
        ? "僅為錯誤類型；更正狀態、嚴重度與故障位置未由本節區確認" : null;

    /// <summary>解析一節的位元組。不足 <see cref="SectionBytes"/> 回 <c>null</c>（不猜欄位）。</summary>
    [SpecRef("Windows SDK um/cper.h（10.0.26100.0）CPER_FIELD_CHECK(WHEA_MEMORY_ERROR_SECTION, …)："
        + "ValidBits 0/8、ErrorStatus 8/8、PhysicalAddress 16/8、PhysicalAddressMask 24/8、"
        + "Node 32/2、Card 34/2、Module 36/2、Bank 38/2、Device 40/2、Row 42/2、Column 44/2、"
        + "BitPosition 46/2、RequesterId 48/8、ResponderId 56/8、TargetId 64/8、ErrorType 72/1；"
        + "WHEA_ERROR_STATUS：bit 8–15＝ErrorType，16＝Address、17＝Control、18＝Data、19＝Responder、"
        + "20＝Requester、21＝FirstError、22＝Overflow；WHEA_MEMERRTYPE_* 0x00–0x0C。"
        + "界線＝只解記憶體節區；Extended 位元組與擴充欄位、處理器／PCIe 節區不在範圍")]
    public static WheaMemorySection? Parse(byte[] section)
    {
        if (section.Length < SectionBytes) return null;
        var s = section.AsSpan();

        ulong validBits = BinaryPrimitives.ReadUInt64LittleEndian(s[..8]);
        ulong status = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(8, 8));
        byte statusType = (byte)((status >> 8) & 0xFF);
        var flags = WheaErrorStatusFlags.None;
        if ((status & (1UL << 16)) != 0) flags |= WheaErrorStatusFlags.Address;
        if ((status & (1UL << 17)) != 0) flags |= WheaErrorStatusFlags.Control;
        if ((status & (1UL << 18)) != 0) flags |= WheaErrorStatusFlags.Data;
        if ((status & (1UL << 19)) != 0) flags |= WheaErrorStatusFlags.Responder;
        if ((status & (1UL << 20)) != 0) flags |= WheaErrorStatusFlags.Requester;
        if ((status & (1UL << 21)) != 0) flags |= WheaErrorStatusFlags.FirstError;
        if ((status & (1UL << 22)) != 0) flags |= WheaErrorStatusFlags.Overflow;

        // 本機函式不能用 ref struct 的 Span 當捕獲（CS8175），所以直接展開寫。
        static ushort U16(ReadOnlySpan<byte> t, int at) =>
            BinaryPrimitives.ReadUInt16LittleEndian(t.Slice(at, 2));
        static ulong U64(ReadOnlySpan<byte> t, int at) =>
            BinaryPrimitives.ReadUInt64LittleEndian(t.Slice(at, 8));

        return new WheaMemorySection(
            validBits,
            status,
            statusType,
            flags,
            U64(s, 16),
            U64(s, 24),
            U16(s, 32), U16(s, 34), U16(s, 36), U16(s, 38), U16(s, 40), U16(s, 42), U16(s, 44), U16(s, 46),
            s[72],
            U16(s, 74),
            U16(s, 76),
            U16(s, 78))
        {
            RequesterId = U64(s, 48),
            ResponderId = U64(s, 56),
            TargetId = U64(s, 64),
        };
    }
}
