namespace XinSpect;

/// <summary>PCIe AER（進階錯誤回報）狀態。純解碼：給完整 4KB 擴充組態空間位元組，找出 AER 能力並讀其錯誤狀態位。</summary>
public readonly record struct AerStatus(uint UncorrectableStatus, uint CorrectableStatus, bool HasUncorrectable, bool HasCorrectable);

/// <summary>
/// PCIe AER 解碼器（純函式）。AER 在擴充組態空間（&gt;0xFF），WinRing0 的 legacy 0xCF8/0xCFC 到不了，
/// 只有 ECAM/MMIO（Phase 3 的自家驅動）讀得到——此處只負責「拿到 4KB 後怎麼解」，實讀由服務層注入。
/// </summary>
public static class PcieAer
{
    public const int ExtConfigStart = 0x100;
    public const ushort ExtCapIdAer = 0x0001;

    /// <summary>走擴充能力鏈結串列（從 0x100 起）找 AER（cap id 0x0001）；找不到或越界回 null。防環、防越界。</summary>
    [SpecRef("PCI Express Base Spec, 擴充能力鏈（Extended Capability）：表頭 CapID bits[15:0]、Next Cap Offset bits[31:20]，自 ECAM 0x100 起；ECAM 機制見 PCIe Spec §7.2.2 與 ACPI MCFG 表")]
    public static int? FindAerCapOffset(ReadOnlySpan<byte> config4k)
    {
        int offset = ExtConfigStart;
        var seen = new HashSet<int>();
        while (offset >= ExtConfigStart && offset + 4 <= config4k.Length && seen.Add(offset))
        {
            uint header = BitConverter.ToUInt32(config4k[offset..(offset + 4)]);
            ushort capId = (ushort)(header & 0xFFFF);
            if (capId == 0) return null;               // 空表頭 = 無擴充能力
            if (capId == ExtCapIdAer) return offset;
            int next = (int)((header >> 20) & 0xFFF);
            if (next == 0) return null;
            offset = next;
        }
        return null;
    }

    /// <summary>解 AER：Uncorrectable Error Status 在 +0x04、Correctable Error Status 在 +0x10。越界回 null。</summary>
    [SpecRef("PCI Express Base Spec, AER Extended Capability 結構：Uncorrectable Error Status @+0x04、Correctable Error Status @+0x10")]
    public static AerStatus? DecodeAer(ReadOnlySpan<byte> config4k, int aerOffset)
    {
        if (aerOffset < ExtConfigStart || aerOffset + 0x14 > config4k.Length) return null;
        uint unc = BitConverter.ToUInt32(config4k[(aerOffset + 0x04)..(aerOffset + 0x08)]);
        uint corr = BitConverter.ToUInt32(config4k[(aerOffset + 0x10)..(aerOffset + 0x14)]);
        return new AerStatus(unc, corr, unc != 0, corr != 0);
    }
}
