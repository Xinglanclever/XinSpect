namespace XinSpect;

/// <summary>一個 PCI 能力（legacy 或擴充）：識別碼、位移、是否為擴充能力。</summary>
public readonly record struct PciCapability(ushort Id, int Offset, bool Extended);

/// <summary>PCIe Link Capabilities 要點（@cap+0x0C）。速度以 GT/s 代碼原樣帶出，不換算成頻寬。</summary>
public readonly record struct PcieLinkCap(uint MaxLinkSpeedCode, uint MaxLinkWidth, uint AspmSupport);

/// <summary>SR-IOV 虛擬功能數（@cap+0x0C…+0x16）。</summary>
public readonly record struct SriovInfo(ushort InitialVfs, ushort TotalVfs, ushort NumVfs, ushort VfOffset, ushort VfStride);

/// <summary>L1 PM Substates 三個暫存器原值（@cap+0x04／+0x08／+0x0C）。</summary>
public readonly record struct L1PmSubstates(uint Capabilities, uint Control1, uint Control2);

/// <summary>DPC（下游埠遏制）能力暫存器原值（@cap+0x04 的前 16 位）。</summary>
public readonly record struct DpcCapability(ushort Capabilities);

/// <summary>Virtual Channel 能力要點。</summary>
public readonly record struct VcInfo(uint PortVcCapability1, uint PortVcCapability2, uint ResourceControl);

/// <summary>AER 完整欄位（狀態／遮罩／嚴重度；@cap+0x04…+0x18）。</summary>
public readonly record struct AerFull(uint UncorrectableStatus, uint UncorrectableMask, uint UncorrectableSeverity,
    uint CorrectableStatus, uint CorrectableMask, uint CapabilitiesAndControl);

/// <summary>
/// PCI 能力鏈的純解碼器：legacy 能力鏈（0x34 起）與擴充能力鏈（0x100 起）走訪，以及各能力的欄位解讀。
/// </summary>
/// <remarks>
/// <para><b>出處</b>：能力識別碼與欄位位移核對 Linux <c>include/uapi/linux/pci_regs.h</c>（2026-10-11 抓取），
/// 鏈結結構依 PCI Local Bus Spec 3.0 §6.7（legacy：Status bit4＝能力清單、Capability Pointer@0x34）
/// 與 PCIe Base Spec §7.5.3（擴充能力：CapID u16、Version／Next Offset）。</para>
/// <para><b>界線</b>：未收錄的識別碼如實回報原始碼（不硬掰名字）；每個能力只解出規格明定的欄位，
/// 其餘位元組保留原值——「解不出來」不寫成 0。</para>
/// </remarks>
public static class PciCapabilities
{
    /// <summary>legacy 能力鏈起點指標（狀態暫存器 @0x06 bit4 為 1 時 @0x34 才有意義）。</summary>
    public const int StatusRegister = 0x06;
    public const int CapabilityPointer = 0x34;
    public const int ExtendedStart = 0x100;

    /// <summary>能力名稱（未收錄的識別碼回 <c>0xXXXX</c>，如實不猜）。</summary>
    [SpecRef("PCI Local Bus Spec 3.0 §6.7 能力 ID 表；PCIe Base Spec §7.5.3／附錄擴充能力 ID 表——核對 Linux pci_regs.h（PCI_CAP_ID_*、PCI_EXT_CAP_ID_*）")]
    public static string Name(ushort id, bool extended) => extended ? ExtendedName(id) : LegacyName(id);

    /// <summary>
    /// 走訪整條能力鏈（legacy ＋ 擴充），回傳依位移排序的清單。防環、防越界、防超長鏈。
    /// </summary>
    [SpecRef("PCI Local Bus Spec 3.0 §6.7：Status@0x06 bit4 能力清單、Capability Pointer@0x34（bit1:0 保留）；PCIe Base Spec §7.5.3：擴充能力自 0x100 起，表頭 CapID u16@0、Version@2(bits3:0)、Next Offset@2(bits15:4)")]
    public static IReadOnlyList<PciCapability> Enumerate(ReadOnlySpan<byte> config4k)
    {
        var list = new List<PciCapability>();
        if (config4k.Length < 0x40) return list;

        // legacy：只有狀態位元說有清單才走（沒有清單時 0x34 是其他用途的位元組）
        bool hasList = (config4k[StatusRegister] & 0x10) != 0;
        if (hasList)
        {
            int offset = config4k[CapabilityPointer] & 0xFC;
            var seen = new HashSet<int>();
            while (offset >= 0x40 && offset + 2 <= 0x100 && seen.Add(offset))
            {
                byte id = config4k[offset];
                if (id == 0) break;
                list.Add(new PciCapability(id, offset, false));
                offset = config4k[offset + 1] & 0xFC;   // 下一指標 bit1:0 保留 → 遮罩
                if (offset == 0) break;
            }
        }

        // 擴充：自 0x100 起，表頭 4 bytes，Next Offset 為 dword bits[31:20]，單位 4 bytes
        var seenExt = new HashSet<int>();
        int ext = ExtendedStart;
        while (ext >= ExtendedStart && ext + 4 <= config4k.Length && seenExt.Add(ext))
        {
            uint header = BitConverter.ToUInt32(config4k[ext..(ext + 4)]);
            ushort capId = (ushort)(header & 0xFFFF);
            if (capId == 0) break;
            list.Add(new PciCapability(capId, ext, true));
            int next = (int)((header >> 20) & 0xFFF);
            if (next == 0) break;
            ext = next;
        }

        return list.OrderBy(c => c.Offset).ToList();
    }

    /// <summary>PCIe Link Capabilities（PCIe 能力 +0x0C）：Max Link Speed bits[3:0]、Max Link Width bits[9:4]、ASPM bits[11:10]。</summary>
    [SpecRef("PCIe Base Spec, Link Capabilities Register（PCIe 能力 @+0x0C）：Max Link Speed bits[3:0]、Maximum Link Width bits[9:4]、ASPM Support bits[11:10]；核對 Linux pci_regs.h（PCI_EXP_LNKCAP 0x0C）")]
    public static PcieLinkCap? LinkCapabilities(ReadOnlySpan<byte> config4k, int pcieCapOffset)
    {
        if (pcieCapOffset < 0x40 || pcieCapOffset + 0x10 > config4k.Length) return null;
        uint v = BitConverter.ToUInt32(config4k[(pcieCapOffset + 0x0C)..(pcieCapOffset + 0x10)]);
        return new PcieLinkCap(v & 0xF, (v >> 4) & 0x3F, (v >> 10) & 0x3);
    }

    /// <summary>MSI-X Table Size＝Message Control（cap+0x02）bits[10:0] 加一（規格：N−1 編碼）。</summary>
    [SpecRef("PCIe Base Spec, MSI-X Message Control Register（MSI-X 能力 @+0x02）：Table Size bits[10:0] 為 N−1 編碼；核對 Linux pci_regs.h（PCI_MSIX_FLAGS 0x02）")]
    public static int? MsixTableSize(ReadOnlySpan<byte> config4k, int msixCapOffset)
    {
        if (msixCapOffset < 0x40 || msixCapOffset + 4 > config4k.Length) return null;
        ushort control = BitConverter.ToUInt16(config4k[(msixCapOffset + 0x02)..(msixCapOffset + 0x04)]);
        return (control & 0x07FF) + 1;
    }

    /// <summary>ACS 能力位元（@cap+0x04）：SV/TB/RR/CR/UF/EC/DT。</summary>
    [SpecRef("PCIe Base Spec, ACS Capability Register（ACS 擴充能力 @+0x04）：SV 0x0001、TB 0x0002、RR 0x0004、CR 0x0008、UF 0x0010、EC 0x0020、DT 0x0040；核對 Linux pci_regs.h")]
    public static string AcsFlags(ReadOnlySpan<byte> config4k, int acsCapOffset)
    {
        if (acsCapOffset < ExtendedStart || acsCapOffset + 6 > config4k.Length) return "";
        ushort cap = BitConverter.ToUInt16(config4k[(acsCapOffset + 0x04)..(acsCapOffset + 0x06)]);
        var on = new List<string>();
        if ((cap & 0x0001) != 0) on.Add("SV");
        if ((cap & 0x0002) != 0) on.Add("TB");
        if ((cap & 0x0004) != 0) on.Add("RR");
        if ((cap & 0x0008) != 0) on.Add("CR");
        if ((cap & 0x0010) != 0) on.Add("UF");
        if ((cap & 0x0020) != 0) on.Add("EC");
        if ((cap & 0x0040) != 0) on.Add("DT");
        return on.Count > 0 ? string.Join("+", on) : "（未設任何位元）";
    }

    /// <summary>SR-IOV 虛擬功能數（@cap+0x0C InitialVFs、+0x0E TotalVFs、+0x10 NumVFs、+0x14 VF Offset、+0x16 VF Stride）。</summary>
    [SpecRef("PCIe Base Spec, SR-IOV 擴充能力：InitialVFs u16@+0x0C、TotalVFs u16@+0x0E、NumVFs u16@+0x10、Function Dependency Link u16@+0x12、First VF Offset u16@+0x14、VF Stride u16@+0x16；核對 Linux pci_regs.h")]
    public static SriovInfo? Sriov(ReadOnlySpan<byte> config4k, int sriovCapOffset)
    {
        if (sriovCapOffset < ExtendedStart || sriovCapOffset + 0x18 > config4k.Length) return null;
        return new SriovInfo(
            BitConverter.ToUInt16(config4k[(sriovCapOffset + 0x0C)..(sriovCapOffset + 0x0E)]),
            BitConverter.ToUInt16(config4k[(sriovCapOffset + 0x0E)..(sriovCapOffset + 0x10)]),
            BitConverter.ToUInt16(config4k[(sriovCapOffset + 0x10)..(sriovCapOffset + 0x12)]),
            BitConverter.ToUInt16(config4k[(sriovCapOffset + 0x14)..(sriovCapOffset + 0x16)]),
            BitConverter.ToUInt16(config4k[(sriovCapOffset + 0x16)..(sriovCapOffset + 0x18)]));
    }

    /// <summary>L1 PM Substates（@cap+0x04／+0x08／+0x0C 原值）。支援位元：bit0 PCI-PM L1.2、bit1 PCI-PM L1.1、bit2 ASPM L1.2、bit3 ASPM L1.1、bit4 L1PMSS。</summary>
    [SpecRef("PCIe Base Spec, L1 PM Substates 擴充能力：Capabilities u32@+0x04（bit0 PCI-PM L1.2、bit1 PCI-PM L1.1、bit2 ASPM L1.2、bit3 ASPM L1.1、bit4 L1PMSS Supported）、Control1 u32@+0x08、Control2 u32@+0x0C；核對 Linux pci_regs.h")]
    public static L1PmSubstates? L1Pm(ReadOnlySpan<byte> config4k, int l1ssCapOffset)
    {
        if (l1ssCapOffset < ExtendedStart || l1ssCapOffset + 0x10 > config4k.Length) return null;
        return new L1PmSubstates(
            BitConverter.ToUInt32(config4k[(l1ssCapOffset + 0x04)..(l1ssCapOffset + 0x08)]),
            BitConverter.ToUInt32(config4k[(l1ssCapOffset + 0x08)..(l1ssCapOffset + 0x0C)]),
            BitConverter.ToUInt32(config4k[(l1ssCapOffset + 0x0C)..(l1ssCapOffset + 0x10)]));
    }

    /// <summary>DPC 能力暫存器（@cap+0x04 的 u16）。位元語意未逐位收錄，故只帶原值——解不出來不寫成 0。</summary>
    [SpecRef("PCIe Base Spec, Downstream Port Containment 擴充能力（DPC Capability Register @+0x04，u16）；識別碼 0x1D 核對 Linux pci_regs.h（PCI_EXT_CAP_ID_DPC）——位元語意未收錄，只報原值")]
    public static DpcCapability? Dpc(ReadOnlySpan<byte> config4k, int dpcCapOffset)
    {
        if (dpcCapOffset < ExtendedStart || dpcCapOffset + 6 > config4k.Length) return null;
        return new DpcCapability(BitConverter.ToUInt16(config4k[(dpcCapOffset + 0x04)..(dpcCapOffset + 0x06)]));
    }

    /// <summary>Virtual Channel 能力（@cap+0x04 Port VC Capability 1、+0x08 Capability 2、+0x0C Port VC Control 之 u32 讀值）。</summary>
    [SpecRef("PCIe Base Spec, Virtual Channel 擴充能力：Port VC Capability Register 1 u32@+0x04、Port VC Capability Register 2 u32@+0x08、Port VC Control Register u16@+0x0C；核對 Linux pci_regs.h（PCI_VC_PORT_CAP1/CAP2/CTRL）")]
    public static VcInfo? VirtualChannel(ReadOnlySpan<byte> config4k, int vcCapOffset)
    {
        if (vcCapOffset < ExtendedStart || vcCapOffset + 0x10 > config4k.Length) return null;
        return new VcInfo(
            BitConverter.ToUInt32(config4k[(vcCapOffset + 0x04)..(vcCapOffset + 0x08)]),
            BitConverter.ToUInt32(config4k[(vcCapOffset + 0x08)..(vcCapOffset + 0x0C)]),
            BitConverter.ToUInt32(config4k[(vcCapOffset + 0x0C)..(vcCapOffset + 0x10)]));
    }

    /// <summary>
    /// AER 完整欄位：Uncorrectable Status @+0x04、Mask @+0x08、Severity @+0x0C、
    /// Correctable Status @+0x10、Mask @+0x14、Capabilities &amp; Control @+0x18。
    /// </summary>
    [SpecRef("PCIe Base Spec, AER 擴充能力（cap id 0x0001）：Uncorrectable Error Status @+0x04、Mask @+0x08、Severity @+0x0C、Correctable Error Status @+0x10、Mask @+0x14、Advanced Error Capabilities and Control @+0x18")]
    public static AerFull? AerFullDecode(ReadOnlySpan<byte> config4k, int aerCapOffset)
    {
        if (aerCapOffset < ExtendedStart || aerCapOffset + 0x1C > config4k.Length) return null;
        return new AerFull(
            BitConverter.ToUInt32(config4k[(aerCapOffset + 0x04)..(aerCapOffset + 0x08)]),
            BitConverter.ToUInt32(config4k[(aerCapOffset + 0x08)..(aerCapOffset + 0x0C)]),
            BitConverter.ToUInt32(config4k[(aerCapOffset + 0x0C)..(aerCapOffset + 0x10)]),
            BitConverter.ToUInt32(config4k[(aerCapOffset + 0x10)..(aerCapOffset + 0x14)]),
            BitConverter.ToUInt32(config4k[(aerCapOffset + 0x14)..(aerCapOffset + 0x18)]),
            BitConverter.ToUInt32(config4k[(aerCapOffset + 0x18)..(aerCapOffset + 0x1C)]));
    }

    private static string LegacyName(ushort id) => id switch
    {
        0x01 => "電源管理", 0x02 => "AGP", 0x03 => "VPD", 0x04 => "Slot ID", 0x05 => "MSI",
        0x06 => "CompactPCI Hot Swap", 0x07 => "PCI-X", 0x08 => "HyperTransport", 0x09 => "廠商自訂",
        0x0A => "Debug Port", 0x0B => "CompactPCI 中央資源控制", 0x0C => "PCI Hot Plug",
        0x0D => "子系統廠商 ID", 0x0E => "AGP 8x", 0x0F => "Secure Device", 0x10 => "PCI Express",
        0x11 => "MSI-X", 0x12 => "SATA 資料／索引設定", 0x13 => "Advanced Features",
        _ => $"0x{id:X2}"
    };

    private static string ExtendedName(ushort id) => id switch
    {
        0x01 => "AER（進階錯誤回報）", 0x02 => "Virtual Channel", 0x03 => "Device Serial Number",
        0x04 => "Power Budgeting", 0x05 => "Root Complex Link Declaration", 0x06 => "Root Complex Internal Link Control",
        0x07 => "Root Complex Event Collector", 0x08 => "Multi-Function VC", 0x09 => "VC with MF",
        0x0A => "Root Complex Register Block", 0x0B => "廠商自訂（擴充）", 0x0C => "Config Access（已過時）",
        0x0D => "ACS（存取控制服務）", 0x0E => "ARI", 0x0F => "ATS", 0x10 => "SR-IOV",
        0x11 => "Multi-Root IOV", 0x12 => "Multicast", 0x13 => "PRI（頁面請求）", 0x15 => "Resizable BAR",
        0x16 => "Dynamic Power Allocation", 0x17 => "TPH Requester", 0x18 => "LTR",
        0x19 => "Secondary PCIe", 0x1A => "Protocol Multiplexing", 0x1B => "PASID",
        0x1D => "DPC（下游埠遏制）", 0x1E => "L1 PM Substates", 0x1F => "PTM",
        0x23 => "DVSEC（指定廠商自訂）", 0x24 => "VF Resizable BAR", 0x25 => "Data Link Feature",
        0x26 => "PHY 16.0 GT/s", 0x29 => "NPEM", 0x2A => "PHY 32.0 GT/s",
        0x2E => "DOE（資料物件交換）", 0x2F => "Device 3", 0x30 => "IDE（完整性與資料加密）",
        0x31 => "PHY 64.0 GT/s",
        _ => $"0x{id:X4}"
    };
}
