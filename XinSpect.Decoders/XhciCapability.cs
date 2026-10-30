namespace XinSpect;

/// <summary>
/// xHCI 能力暫存器區的基本欄位（U10 第三圈 NP-0002）。
/// </summary>
/// <param name="Caplength">能力暫存器區長度（位元組）——同時是作業暫存器區的起點。</param>
/// <param name="HciVersion">HCIVERSION，BCD 編碼（<c>0x0100</c> ＝ 1.00）。</param>
/// <param name="MaxSlots">HCSPARAMS1 bits 7:0。</param>
/// <param name="MaxInterrupters">HCSPARAMS1 bits 15:8。</param>
/// <param name="MaxPorts">HCSPARAMS1 bits 31:24——<b>這是 xHCI 版的埠數上限</b>。</param>
/// <param name="Ist">HCSPARAMS2 bits 3:0。</param>
/// <param name="ErstMax">HCSPARAMS2 bits 7:4。</param>
/// <param name="ScratchpadBuffers">HCSPARAMS2 的高低位組成的 scratchpad 數量。</param>
/// <param name="Ac64">HCCPARAMS1 bit 0：支援 64 位元定址。</param>
/// <param name="Csz">HCCPARAMS1 bit 2：context 大小 64 位元組（否則 32）。</param>
/// <param name="Xecp">HCCPARAMS1 bits 31:16：擴充能力串列的 DWORD 位移（0＝沒有）。</param>
public sealed record XhciCapabilities(
    byte Caplength,
    ushort HciVersion,
    byte MaxSlots,
    byte MaxInterrupters,
    byte MaxPorts,
    byte Ist,
    byte ErstMax,
    ushort ScratchpadBuffers,
    bool Ac64,
    bool Csz,
    ushort Xecp)
{
    /// <summary>版次文字（<c>0x0100</c> → <c>1.00</c>；<c>0x0110</c> → <c>1.10</c>）。</summary>
    public string VersionText => $"{(HciVersion >> 8) & 0xFF}.{(HciVersion & 0xFF):X2}";
}

/// <summary>
/// 一個「支援協定」擴充能力所描述的東西（USB 2.0／USB 3.x）。
/// </summary>
/// <param name="MajorRevision">主修訂：<c>0x02</c>＝USB 2.0、<c>0x03</c>＝USB 3.x。</param>
/// <param name="MinorRevision">次修訂。</param>
/// <param name="NameString">名稱字串索引（0＝無）。</param>
/// <param name="CompliancePortPower">相容埠供電模式。</param>
/// <param name="ProtocolDefined">協定定義（bit0–1 是協定型別）。</param>
/// <param name="Speeds">PSI 表解出的速度（Mb/s 與倍率）。</param>
public sealed record XhciProtocol(
    byte MajorRevision,
    byte MinorRevision,
    byte NameString,
    byte CompliancePortPower,
    ushort ProtocolDefined,
    IReadOnlyList<XhciSpeedId> Speeds)
{
    /// <summary>協定名稱：只認 2.0 與 3.x，其餘寫出原始值。</summary>
    public string Name => MajorRevision switch
    {
        0x02 => "USB 2.0",
        0x03 => "USB 3.x（SuperSpeed）",
        _ => $"主修訂 0x{MajorRevision:X2}（本版不命名）",
    };
}

/// <summary>
/// 一個 Protocol Speed ID（PSI）條目。
/// </summary>
/// <param name="SpeedId">PSIV（速度編號，1 起）。</param>
/// <param name="LinkProtocol">PLT：0＝USB 2.0 協定、1＝USB 3.0 協定、2＝兩者皆可。</param>
/// <param name="FullDuplex">PFD。</param>
/// <param name="Raw">整個 PSI DWORD 的原值——供人自行核對。</param>
/// <remarks>
/// <b>為什麼不換算成 Mb/s：</b>速度值欄位（PSIM 尾數／PSIS 十的次方）的**位界在公開資料裡互相矛盾**——
/// xHCI 規格 1.1 的表把 PSIM 放在 bits 19:16（4 位元，最大 15），而 Linux 的
/// <c>xhci-ext-caps.h</c> 把 <c>PSI_PSIM</c> 取成 bits 31:16。兩者不可能同時對：
/// 按前者，480 Mb/s 根本編不進去；按後者，PSIM 又與 PSIS 重疊。
/// <b>讀不出唯一定義就不編公式</b>——只給原值與不含爭議的三個欄位（PSIV／PLT／PFD）。
/// </remarks>
public sealed record XhciSpeedId(
    byte SpeedId,
    byte LinkProtocol,
    bool FullDuplex,
    uint Raw)
{
    /// <summary>協定名稱；PLT 只定義 0–2，其餘寫原始值。</summary>
    public string LinkProtocolName => LinkProtocol switch
    {
        0 => "USB 2.0",
        1 => "USB 3.0",
        2 => "兩者皆可",
        _ => $"PLT 0x{LinkProtocol:X}（本版不命名）",
    };
}

/// <summary>
/// xHCI 能力暫存器區的解碼器（U10 第三圈 NP-0002）。純位元組解析，不碰 MMIO。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼這一輪只能做到這裡：</b>候選項 NP-0002 要「xHCI 版本與支援速度位」。
/// 那兩個東西都住在<b>控制器 MMIO</b>（PCI BAR）裡——不是事件、不是登錄檔、不是 WMI。
/// 所以本服務的<b>解碼器</b>可以寫得很硬（有 xHCI 規格逐欄位），
/// 而<b>生產端</b>必須承認「沒有 ring0 後端就讀不到」。
/// </para>
/// <para>
/// <b>版面對準 xHCI 規格 1.1 §5.3（Host Controller Capability Registers）：</b>
/// 位移從 MMIO 基底起算：<c>0x00</c> CAPLENGTH、<c>0x02</c> HCIVERSION、
/// <c>0x04</c> HCSPARAMS1、<c>0x08</c> HCSPARAMS2、<c>0x0C</c> HCSPARAMS3、
/// <c>0x10</c> HCCPARAMS1。擴充能力串列從 <c>XECP × 4</c> 起算。
/// </para>
/// <para>
/// <b>界線：</b>只解能力區與「支援協定」擴充能力；不動作業暫存器、不讀埠狀態、
/// 不寫任何暫存器。HCSPARAMS3 的解讀延遲本版不解（單位與晶片實作有出入）。
/// </para>
/// </remarks>
public static class XhciCapabilityDecoder
{
    /// <summary>能力區至少要這麼長才夠解到 HCCPARAMS1。</summary>
    public const int CapabilityBytes = 0x14;

    /// <summary>擴充能力串列的 walk 上限——防止環狀串列把迴圈卡死。</summary>
    public const int MaxExtendedCaps = 32;

    /// <summary>「支援協定」擴充能力的 Capability ID。</summary>
    public const byte SupportedProtocolId = 0x02;

    [SpecRef("xHCI 規格 1.1 §5.3：Host Controller Capability Registers，位移自主控基底："
        + "CAPLENGTH u8@0x00、Rsvd u8@0x01、HCIVERSION u16@0x02（BCD，0x0100＝1.00）、"
        + "HCSPARAMS1 u32@0x04（MaxSlots bits7:0／MaxIntrs bits15:8／MaxPorts bits31:24）、"
        + "HCSPARAMS2 u32@0x08（IST bits3:0／ERST Max bits7:4／MaxScratchpadHi bits10:8／"
        + "SPR bits15:11／MaxScratchpadLo bits20:16）、HCSPARAMS3 u32@0x0C、"
        + "HCCPARAMS1 u32@0x10（AC64 bit0／CSZ bit2／xECP bits31:16，xECP 的單位是 DWORD）。"
        + "界線＝只解到 HCCPARAMS1；HCSPARAMS3 的延遲欄位本版不解")]
    public static XhciCapabilities? ParseCapabilities(byte[] mmio)
    {
        if (mmio.Length < CapabilityBytes) return null;
        var s = mmio.AsSpan();
        byte caplength = s[0];
        ushort hci = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(2, 2));
        uint sp1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4, 4));
        uint sp2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8, 4));
        uint cp1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(0x10, 4));

        return new XhciCapabilities(
            caplength,
            hci,
            (byte)(sp1 & 0xFF),
            (byte)((sp1 >> 8) & 0xFF),
            (byte)((sp1 >> 24) & 0xFF),
            (byte)(sp2 & 0x0F),
            (byte)((sp2 >> 4) & 0x0F),
            (ushort)(((sp2 >> 16) & 0x1F) | (((sp2 >> 8) & 0x07) << 5)),
            (cp1 & 0x01) != 0,
            ((cp1 >> 2) & 0x01) != 0,
            (ushort)((cp1 >> 16) & 0xFFFF));
    }

    /// <summary>
    /// 走擴充能力串列，取出所有「支援協定」能力。位址相對 <b>同一個緩衝區的基底</b>。
    /// </summary>
    /// <remarks>
    /// 串列的 Next pointer 是「DWORD 位移」且<b>相對主控基底</b>（不是相對前一項），
    /// 所以走訪時要一路加上基底位移。環狀或超界就停，並把「停在哪」交給呼叫端陳述。
    /// </remarks>
    [SpecRef("xHCI 規格 1.1 §7.1（Extended Capabilities）：每個擴充能力 4 位元組對齊，"
        + "byte0 Capability ID、byte1 Next Capability Pointer（DWORD 位移，0＝串列結束）；"
        + "§7.2 Supported Protocol（ID 0x02）：MinorRevision u8@0x02、MajorRevision u8@0x03、"
        + "NameString u8@0x04、CompliancePortPower u8@0x05、ProtocolDefined u16@0x06、"
        + "ProtocolSlotType u32@0x08、PSIC u8@0x0C、PSI[] 每筆 u32@0x10（PSIV bits3:0／"
        + "PSIE bit4／PLT bits6:5／PFD bit7）。"
        + "界線＝速度值欄位（PSIM／PSIS）不換算：規格表的位界（PSIM bits19:16）與 Linux xhci-ext-caps.h "
        + "的 PSI_PSIM（bits31:16）互相矛盾，讀不出唯一定義就不編公式——只輸出原始 DWORD")]
    public static IReadOnlyList<XhciProtocol> ParseSupportedProtocols(byte[] mmio, XhciCapabilities caps)
    {
        var list = new List<XhciProtocol>();
        if (caps.Xecp == 0) return list;

        var s = mmio.AsSpan();
        int offset = caps.Xecp * 4;
        var seen = new HashSet<int>();
        for (int step = 0; step < MaxExtendedCaps; step++)
        {
            if (offset <= 0 || offset + 4 > s.Length) break;
            if (!seen.Add(offset)) break; // 環狀串列

            byte capId = s[offset];
            byte next = s[offset + 1];

            if (capId == SupportedProtocolId && offset + 0x10 <= s.Length)
            {
                byte minor = s[offset + 0x02];
                byte major = s[offset + 0x03];
                byte nameString = s[offset + 0x04];
                byte compliance = s[offset + 0x05];
                ushort protocolDefined = System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt16LittleEndian(s.Slice(offset + 0x06, 2));
                byte psiCount = s[offset + 0x0C];

                var speeds = new List<XhciSpeedId>();
                for (int i = 0; i < psiCount; i++)
                {
                    int at = offset + 0x10 + i * 4;
                    if (at + 4 > s.Length) break;
                    uint psi = System.Buffers.Binary.BinaryPrimitives
                        .ReadUInt32LittleEndian(s.Slice(at, 4));
                    speeds.Add(new XhciSpeedId(
                        (byte)(psi & 0x0F),
                        (byte)((psi >> 5) & 0x03),
                        ((psi >> 7) & 0x01) != 0,
                        psi));
                }

                list.Add(new XhciProtocol(major, minor, nameString, compliance, protocolDefined, speeds));
            }

            if (next == 0) break;
            offset += next * 4;
        }
        return list;
    }
}
