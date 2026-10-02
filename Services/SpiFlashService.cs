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

/// <summary>PCH SPI 快閃暫存器的純解碼器。不碰硬體；實際讀取由服務層經 PCI（找 SPIBAR）與 MMIO（讀暫存器）取得後餵進來。</summary>
public static class SpiFlash
{
    private const uint KnownHsfstsBits = 0x0001 | 0x0002 | 0x0004 | 0x0800 | 0x2000 | 0x8000;
    private const uint KnownFrapBits = 0x00FF;

    public static SpiHsfstsDecode DecodeHsfsts(uint raw)
    {
        var verdict = (raw & 0x8000) != 0 ? ChipsetSecurityVerdict.Protected : ChipsetSecurityVerdict.Unprotected;
        return new(
            (raw & 0x0001) != 0, (raw & 0x0002) != 0, (raw & 0x0004) != 0,
            (raw & 0x0800) != 0, (raw & 0x2000) != 0, (raw & 0x8000) != 0,
            raw & ~KnownHsfstsBits, verdict);
    }

    public static SpiFrapDecode DecodeFrap(uint raw)
        => new((byte)(raw & 0xF), (byte)((raw >> 4) & 0xF), raw & ~KnownFrapBits);

    public static SpiFregDecode DecodeFreg(uint raw)
        => new((ushort)(raw & 0x7FFF), (ushort)((raw >> 16) & 0x7FFF), raw == 0);

    public static SpiPrxDecode DecodePrx(uint raw)
        => new((ushort)(raw & 0x7FFF), (ushort)((raw >> 16) & 0x7FFF), (raw & 0x8000) != 0, (raw & 0x80000000) != 0);
}

/// <summary>
/// PCH SPI 快閃控制器的三態事實：HSFSTS 旗號、FRAP 區域存取權限、FREG 區域地圖、PR 保護範圍。
/// SPIBAR 由 PCI 0:1F.5 +0x10 的 BAR 取得（PCI 設定空間現有 WinRing0 讀得到）；暫存器本體在 SPIBAR MMIO——
/// 自家驅動未載時四組全部三態標示，並把已知的 SPIBAR 位址寫進原因，絕不假裝讀過。
/// 暫存器佈局依 Intel PCH EDS（與 CHIPSEC spi_lock/spi_desc 同源）：HSFSTS@0x04、FRAP@0x50、FREG0-5@0x54-0x68、PR0-4@0x74-0x84。
/// </summary>
public static class SpiFlashService
{
    private const string Category = "韌體安全";
    public const byte SpiBus = 0, SpiDevice = 0x1F, SpiFunction = 5;
    public const int BlockLength = 0x88; // 蓋得到 PR4 結尾（0x84+4）

    public static IReadOnlyList<HardwareFact> Collect(IPciConfigReader pci, IMmioReader mmio, DateTimeOffset at)
    {
        if (!pci.Available)
            return Unavailable(at, FactAvailability.InsufficientPrivilege, pci.UnavailableReason ?? "缺 ring0：特權讀取未就緒");

        uint? id = pci.ReadDword(SpiBus, SpiDevice, SpiFunction, 0x00);
        if (id is null)
            return Unavailable(at, FactAvailability.ReadError, "PCI 設定空間讀取失敗");
        if (id.Value == 0xFFFFFFFF)
            return Unavailable(at, FactAvailability.NotApplicable, "0:1F.5 無回應（找不到 SPI 控制器）");
        if ((id.Value & 0xFFFF) != 0x8086)
            return Unavailable(at, FactAvailability.NotApplicable, "0:1F.5 非 Intel 裝置（SPI 控制器不在此處）");

        uint? bar = pci.ReadDword(SpiBus, SpiDevice, SpiFunction, 0x10);
        if (bar is null)
            return Unavailable(at, FactAvailability.ReadError, "SPI BAR 讀取失敗");
        if (bar.Value == 0xFFFFFFFF || (bar.Value & ~0xFFFu) == 0)
            return Unavailable(at, FactAvailability.NotApplicable, "SPI 控制器未配置 SPIBAR");
        if ((bar.Value & 0x1) != 0)
            return Unavailable(at, FactAvailability.ReadError, "SPI BAR 為 I/O 型（非預期配置）");
        ulong spiBar = bar.Value & 0xFFFFF000u;

        if (!mmio.Available)
            return Unavailable(at, FactAvailability.InsufficientPrivilege,
                $"{mmio.UnavailableReason ?? "缺 MMIO 讀取"}；SPIBAR 0x{spiBar:X8} 需 MMIO（擴充暫存器無法經 PCI 設定空間到達）");

        var block = mmio.ReadBlock(spiBar, BlockLength);
        if (block is null || block.Length < BlockLength)
            return Unavailable(at, FactAvailability.ReadError,
                $"SPIBAR MMIO 讀取失敗{(mmio.LastFailReason is { } f ? $"：{f}" : "")}");

        return
        [
            HsfstsFact(BitConverter.ToUInt32(block, 0x04), at),
            FrapFact(BitConverter.ToUInt32(block, 0x50), at),
            RegionsFact(at, Enumerable.Range(0, 6).Select(i => BitConverter.ToUInt32(block, 0x54 + i * 4)).ToArray()),
            ProtectedRangesFact(at, Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(block, 0x74 + i * 4)).ToArray()),
        ];
    }

    private static string SpiSource => $"PCI 0:{SpiDevice:X2}.{SpiFunction} BAR → SPIBAR";

    private static HardwareFact[] Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
    {
        string[] keys = ["spi.hsfsts", "spi.frap", "spi.regions", "spi.prr"];
        string[] names = ["SPI 快閃鎖定狀態", "SPI 區域存取權限", "SPI 快閃區域地圖", "SPI 保護範圍"];
        return keys.Zip(names).Select(p => new HardwareFact(p.First, Category, p.Second, "", "", SpiSource,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToArray();
    }

    private static HardwareFact HsfstsFact(uint raw, DateTimeOffset at)
    {
        var d = SpiFlash.DecodeHsfsts(raw);
        var parts = new List<string>
        {
            d.Verdict == ChipsetSecurityVerdict.Protected
                ? "已鎖定（FLOCKDN=1）：SPI 保護設定不可改直至重置"
                : "未鎖定（FLOCKDN=0）：保護範圍與寫入停用設定仍可被 ring0 改動",
            d.WriteStatusDisable ? "寫入狀態已停用（WRSDIS=1，WRSR 遭擋）" : "寫入狀態未停用（WRSDIS=0）",
            $"FDOPSS={(d.DescriptorOverridePinStrap ? 1 : 0)}（描述符覆寫腳位狀態，僅報位元值）",
        };
        if (d.FlashCycleDone) parts.Add("FDONE=1（上次快閃週期完成）");
        if (d.FlashCycleError) parts.Add("FCERR=1（上次快閃週期錯誤）");
        if (d.AccessErrorLog) parts.Add("AEL=1（存取錯誤記錄待讀）");
        if (d.UnknownBits != 0) parts.Add($"未知位元 0x{d.UnknownBits:X}");
        return new HardwareFact("spi.hsfsts", Category, "SPI 快閃鎖定狀態", string.Join("；", parts), "",
            $"{SpiSource}+0x04", FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact FrapFact(uint raw, DateTimeOffset at)
    {
        var d = SpiFlash.DecodeFrap(raw);
        var text = d.BiosRegionHostWritable
            ? $"BIOS 區域可寫入：主機軟體獲准（BRWA=0x{d.BiosRegionWriteMask:X} bit1）"
            : $"BIOS 區域不可寫入：主機軟體未獲准（BRWA=0x{d.BiosRegionWriteMask:X} bit1=0）";
        text += $"；BIOS 讀取遮罩 BRRA=0x{d.BiosRegionReadMask:X}";
        if (d.UnknownBits != 0) text += $"；未知位元 0x{d.UnknownBits:X}";
        return new HardwareFact("spi.frap", Category, "SPI 區域存取權限", text, "",
            $"{SpiSource}+0x50", FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact RegionsFact(DateTimeOffset at, uint[] fregs)
    {
        string[] specNames = ["描述符", "BIOS", "Intel ME", "GbE", "平台資料", "EC"];
        var parts = new List<string>();
        for (int i = 0; i < fregs.Length; i++)
        {
            var d = SpiFlash.DecodeFreg(fregs[i]);
            if (d.Empty) continue;
            if (d.Limit4k < d.Base4k)
            {
                parts.Add($"{specNames[i]} 範圍異常（原始 0x{fregs[i]:X8}，不臆測）");
                continue;
            }
            ulong from = (ulong)d.Base4k << 12, to = ((ulong)d.Limit4k << 12) + 0xFFF;
            parts.Add($"{specNames[i]} 0x{from:X6}–0x{to:X6}（{SizeText((uint)(d.Limit4k - d.Base4k + 1))}）");
        }
        var text = parts.Count == 0 ? "FREG0-5 全為空（未依描述符配置區域）" : string.Join("；", parts);
        return new HardwareFact("spi.regions", Category, "SPI 快閃區域地圖", text, "",
            $"{SpiSource}+0x54", FactTrustLevel.Measured, false, at);
    }

    private static HardwareFact ProtectedRangesFact(DateTimeOffset at, uint[] prs)
    {
        var parts = new List<string>();
        for (int i = 0; i < prs.Length; i++)
        {
            var d = SpiFlash.DecodePrx(prs[i]);
            if (!d.Enabled) continue;
            ulong from = (ulong)d.Base4k << 12, to = ((ulong)d.Limit4k << 12) + 0xFFF;
            string kind = d.WriteProtect && d.ReadProtect ? "寫+讀保護" : d.WriteProtect ? "寫保護" : "讀保護";
            parts.Add($"PR{i} 0x{from:X6}–0x{to:X6} {kind}");
        }
        var text = parts.Count == 0
            ? "PR0-4 全部停用：SPI 硬體層無範圍保護（寫入面僅剩 BIOS_CNTL 與 FLOCKDN 把關）"
            : string.Join("；", parts);
        return new HardwareFact("spi.prr", Category, "SPI 保護範圍", text, "",
            $"{SpiSource}+0x74", FactTrustLevel.Measured, false, at);
    }

    private static string SizeText(uint kibUnits)
    {
        ulong bytes = (ulong)kibUnits * 4096;
        return bytes >= (1 << 20) ? $"{bytes >> 20} MiB" : $"{bytes >> 10} KiB";
    }
}
