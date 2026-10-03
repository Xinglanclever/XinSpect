namespace XinSpect;


/// <summary>HSFSTS1（硬體排序快閃狀態，SPIBAR+0x04）旗號解碼。佈局依現代 Intel PCH（Lynx Point 起，CHIPSEC spi_lock 同源）：
/// bit0 FDONE、bit1 FCERR、bit2 AEL、bit11 WRSDIS、bit13 FDOPSS、bit15 FLOCKDN。FDOPSS 只解位元值不渲染判決
/// （各世代文件對其極性表述不一，寧可報 raw 也不冒判決風險）；其餘未知位元保留原值。</summary>
public readonly record struct SpiHsfstsDecode(
    bool FlashCycleDone, bool FlashCycleError, bool AccessErrorLog,
    bool WriteStatusDisable, bool DescriptorOverridePinStrap, bool FlashLockDown,
    uint UnknownBits, ChipsetSecurityVerdict Verdict);

/// <summary>FRAP（快閃區域存取權限，SPIBAR+0x50）解碼。bits[3:0] BRWA＝允許寫入的區域遮罩（bit1=BIOS 區）、bits[7:4] BRRA；
/// 高位欄位各世代命名不一，一律歸未知保留原值。</summary>
public readonly record struct SpiFrapDecode(byte BiosRegionWriteMask, byte BiosRegionReadMask, uint UnknownBits)
{
    public bool BiosRegionHostWritable => (BiosRegionWriteMask & 0x2) != 0;
}

/// <summary>FREGx（快閃區域登錄，SPIBAR+0x54 起）：bits[14:0] 區域基底、bits[30:16] 區域上限，皆 4KB 單位；全 0＝未使用。</summary>
public readonly record struct SpiFregDecode(ushort Base4k, ushort Limit4k, bool Empty);

/// <summary>PRx（SPI 保護範圍，SPIBAR+0x74 起）：bit15 WPE 寫保護、bit31 RPE 讀保護、基底/上限佈局同 FREG。</summary>
public readonly record struct SpiPrxDecode(ushort Base4k, ushort Limit4k, bool WriteProtect, bool ReadProtect)
{
    public bool Enabled => WriteProtect || ReadProtect;
}

/// <summary>BIOS 寫入面綜合裁決：把 BIOS_CNTL（BIOSWE/BLE/SMM_BWP）、FLOCKDN、FRAP、PR0-4 的裁決攤在同一行，輸入全部具名。</summary>
public sealed record BiosWriteSurface(ChipsetSecurityVerdict Verdict, string Text);

/// <summary>PCH SPI 快閃暫存器的純解碼器。不碰硬體；實際讀取由服務層經 PCI（找 SPIBAR）與 MMIO（讀暫存器）取得後餵進來。</summary>
public static class SpiFlash
{
    private const uint KnownHsfstsBits = 0x0001 | 0x0002 | 0x0004 | 0x0800 | 0x2000 | 0x8000;
    private const uint KnownFrapBits = 0x00FF;

    [SpecRef("Intel PCH EDS, SPIBAR HSFSTS（SPIBAR+0x04）：FDONE bit0、FCERR bit1、AEL bit2、WRSDIS bit11、FDOPSS bit13、FLOCKDN bit15；coreboot intelmetool／CHIPSEC spi_lock 交叉核對")]
    public static SpiHsfstsDecode DecodeHsfsts(uint raw)
    {
        var verdict = (raw & 0x8000) != 0 ? ChipsetSecurityVerdict.Protected : ChipsetSecurityVerdict.Unprotected;
        return new(
            (raw & 0x0001) != 0, (raw & 0x0002) != 0, (raw & 0x0004) != 0,
            (raw & 0x0800) != 0, (raw & 0x2000) != 0, (raw & 0x8000) != 0,
            raw & ~KnownHsfstsBits, verdict);
    }
    [SpecRef("Intel PCH EDS, SPIBAR FRAP（SPIBAR+0x50）：BRWA bits[3:0]（bit1=BIOS 區 host 寫允准）、BRRA bits[7:4]；CHIPSEC spi_desc 交叉核對")]
    public static SpiFrapDecode DecodeFrap(uint raw)
        => new((byte)(raw & 0xF), (byte)((raw >> 4) & 0xF), raw & ~KnownFrapBits);

    [SpecRef("Intel PCH EDS, SPIBAR FREG0-5（+0x54 起，每筆 4 bytes）：基底 bits[14:0]、上限 bits[30:16]，4KB 單位；0x7FFF 上限編碼見 handoff §9")]
    public static SpiFregDecode DecodeFreg(uint raw)
        => new((ushort)(raw & 0x7FFF), (ushort)((raw >> 16) & 0x7FFF), raw == 0);

    [SpecRef("Intel PCH EDS, SPIBAR PR0-4（+0x74 起，每筆 4 bytes）：WPE bit15、RPE bit31，基底/上限佈局同 FREG；coreboot intelmetool 交叉核對")]
    public static SpiPrxDecode DecodePrx(uint raw)
        => new((ushort)(raw & 0x7FFF), (ushort)((raw >> 16) & 0x7FFF), (raw & 0x8000) != 0, (raw & 0x80000000) != 0);

    /// <summary>
    /// BIOS 寫入面綜合裁決（純函式）：主軸沿用 BIOS_CNTL 的 SMM_BWP/BLE 階梯，其餘輸入以「暴露面」具名列出——
    /// FLOCKDN=0 代表 SPI 保護設定本身可被 ring0 改、FRAP bit1 代表描述符准 host 寫 BIOS 區、PR 全停用代表無範圍保護。
    /// 只綜合已量到的事實，不外推。
    /// </summary>
    [SpecRef("綜合裁決：輸入位元定義分別引 BIOS_CNTL（PCI 0:1F.0+0xDC，見 ChipsetSecurity）與 SPIBAR HSFSTS/FRAP/PR（Intel PCH EDS，見上）；組合邏輯為本專案方法學，只綜合已量到的事實不外推")]
    public static BiosWriteSurface ComposeWriteSurface(
        BiosCntlDecode biosCntl, SpiHsfstsDecode hsfsts, SpiFrapDecode frap, IReadOnlyList<SpiPrxDecode> prs)
    {
        string level = biosCntl.Verdict switch
        {
            ChipsetSecurityVerdict.SmmProtected => "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS",
            ChipsetSecurityVerdict.Protected => "有鎖保護：BLE=1，開啟寫入會觸發 SMI",
            _ => "未保護：BLE=0，任何 ring0 皆可寫 BIOS",
        };
        var exposures = new List<string>();
        if (!hsfsts.FlashLockDown) exposures.Add("SPI 旗號未鎖（FLOCKDN=0，保護設定可被改）");
        if (frap.BiosRegionHostWritable) exposures.Add("描述符准主機軟體寫 BIOS 區（FRAP bit1=1）");
        if (prs.All(p => !p.Enabled)) exposures.Add("PR0-4 無啟用範圍保護");
        var text = exposures.Count == 0 ? level : $"{level}；暴露面：{string.Join("、", exposures)}";
        return new BiosWriteSurface(biosCntl.Verdict, text);
    }
}
