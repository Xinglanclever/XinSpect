namespace XinSpect;

/// <summary>原始暫存器快照的完整性信封（sha256 over canonical JSON，不含本欄位本身）。</summary>
public sealed record RawSnapshotIntegrity
{
    public required string Algorithm { get; init; }
    public required string Hash { get; init; }
}

/// <summary>
/// 一份原始暫存器快照：逐來源的位元組區集合。與語義快照（HardwareSnapshot）並存，各自有自己的完整性信封。
/// 誠實界線：raw 就是原始位元組，不做匿名化——檔案可能含 OEM 原始材料（如 MSDM），存檔是使用者主動行為。
/// </summary>
public sealed record RawRegisterSnapshot
{
    public required string AppVersion { get; init; }
    public required DateTimeOffset TakenAtUtc { get; init; }
    public required IReadOnlyList<RawRegisterRegion> Regions { get; init; }
    public RawSnapshotIntegrity? Integrity { get; init; }
}

/// <summary>
/// 原始暫存器收集器（深層暫存器計畫 P4）。只收「今天真的讀得到」的來源，逐區三態：
/// PCI 設定空間安全暫存器（BIOS_CNTL/SMRAMC，WinRing0）、ACPI 表整表位元組（usermode）、
/// 平台安全 MSR（FEATURE_CONTROL/DEBUG_INTERFACE/SMI_COUNT，呼叫執行緒所在邏輯核心）、
/// SPIBAR MMIO 區塊（自家驅動，未載即三態）。讀不到的區標 Availability+原因，絕不以 0 或空位元組填補。
/// </summary>
public static class RawRegisterCollectService
{
    public static RawRegisterSnapshot Create(string appVersion, IReadOnlyList<RawRegisterRegion> regions, DateTimeOffset at)
        => new() { AppVersion = appVersion, TakenAtUtc = at, Regions = regions };

    public static IReadOnlyList<RawRegisterRegion> Collect(
        IPciConfigReader pci, IAcpiTableSource acpi, IKernelMsrReader msr, IMmioReader mmio, DateTimeOffset at)
    {
        var regions = new List<RawRegisterRegion>
        {
            PciRegion(pci, "pcicfg:00:1f.0+dc", 0x1F, 0, 0xDC, at),
            PciRegion(pci, "pcicfg:00:00.0+88", 0x00, 0, 0x88, at),
            // SMI 計數器（byte0-3）與 DEBUG_OCCURRED（byte7 bit31）會隨時間變動：遮罩，避免每次快照都在變
            MsrRegion(msr, "msr:0x3a", 0x3A, at),
            MsrRegion(msr, "msr:0xc80", 0xC80, at, [0, 0, 0, 0, 0, 0, 0, 1]),
            MsrRegion(msr, "msr:0x34", 0x34, at, [1, 1, 1, 1, 0, 0, 0, 0]),
        };

        if (acpi.Available)
        {
            foreach (var table in acpi.ReadAll())
            {
                if (table.Length < 8) continue;
                string sig = System.Text.Encoding.ASCII.GetString(table[..4]).TrimEnd('\0', ' ');
                if (sig.Length == 0) continue;
                regions.Add(new RawRegisterRegion
                {
                    Source = $"acpi:{sig}",
                    Bytes = table,
                    Availability = FactAvailability.Present,
                });
            }
        }
        else
        {
            regions.Add(new RawRegisterRegion
            {
                Source = "acpi:*",
                Availability = FactAvailability.InsufficientPrivilege,
                UnavailableReason = acpi.UnavailableReason ?? "無法列舉 ACPI 表",
            });
        }

        if (mmio.Available)
        {
            var block = mmio.ReadBlock(0xFED10000, 0x88);
            regions.Add(block is null
                ? new RawRegisterRegion
                {
                    Source = "mmio:spi:0xfed10000+88",
                    Availability = FactAvailability.ReadError,
                    UnavailableReason = mmio.LastFailReason ?? "SPIBAR MMIO 讀取失敗",
                }
                : new RawRegisterRegion
                {
                    Source = "mmio:spi:0xfed10000+88",
                    Bytes = block,
                    // HSFSTS 的 FDONE/FCERR/AEL（byte0x04 低位）隨快閃週期變動，遮罩避免淹沒真正的設定變動
                    VolatilityMask = MakeMask(0x88, 0x04),
                });
        }
        else
        {
            regions.Add(new RawRegisterRegion
            {
                Source = "mmio:spi:0xfed10000+88",
                Availability = FactAvailability.InsufficientPrivilege,
                UnavailableReason = mmio.UnavailableReason ?? "缺 MMIO 讀取",
            });
        }

        // MCHBAR（記憶體控制器視窗）：基底可解析時整段 raw 收入；解讀刻意未實作（世代相依，待對準規格）。
        var mchbar = MchbarService.ResolveBase(pci);
        if (mchbar is null)
        {
            regions.Add(new RawRegisterRegion
            {
                Source = "mmio:mchbar",
                Availability = FactAvailability.NotApplicable,
                UnavailableReason = "MCHBAR 基底無法解析（缺 ring0／無主機橋／未啟用）",
            });
        }
        else if (!mmio.Available)
        {
            regions.Add(new RawRegisterRegion
            {
                Source = $"mmio:mchbar:0x{mchbar.Value:X}+100",
                Availability = FactAvailability.InsufficientPrivilege,
                UnavailableReason = mmio.UnavailableReason ?? "缺 MMIO 讀取",
            });
        }
        else
        {
            var mchbarBlock = mmio.ReadBlock(mchbar.Value, 0x100);
            regions.Add(mchbarBlock is null
                ? new RawRegisterRegion
                {
                    Source = $"mmio:mchbar:0x{mchbar.Value:X}+100",
                    Availability = FactAvailability.ReadError,
                    UnavailableReason = mmio.LastFailReason ?? "MCHBAR MMIO 讀取失敗",
                }
                : new RawRegisterRegion { Source = $"mmio:mchbar:0x{mchbar.Value:X}+100", Bytes = mchbarBlock });
        }

        return regions;
    }

    private static RawRegisterRegion PciRegion(IPciConfigReader pci, string source, byte dev, byte fn, uint reg, DateTimeOffset at)
    {
        if (!pci.Available)
            return new RawRegisterRegion
            {
                Source = source,
                Availability = FactAvailability.InsufficientPrivilege,
                UnavailableReason = pci.UnavailableReason ?? "缺 ring0：PCI 設定空間讀取未就緒",
            };
        uint? raw = pci.ReadDword(0, dev, fn, reg);
        if (raw is null)
            return new RawRegisterRegion { Source = source, Availability = FactAvailability.ReadError, UnavailableReason = "PCI 設定空間讀取失敗" };
        if (raw.Value == 0xFFFFFFFF)
            return new RawRegisterRegion { Source = source, Availability = FactAvailability.NotApplicable, UnavailableReason = "裝置無回應" };
        return new RawRegisterRegion { Source = source, Bytes = BitConverter.GetBytes(raw.Value) };
    }

    private static RawRegisterRegion MsrRegion(IKernelMsrReader msr, string source, uint index, DateTimeOffset at, byte[]? volatileMask = null)
    {
        if (!msr.Available)
            return new RawRegisterRegion
            {
                Source = source,
                Availability = FactAvailability.InsufficientPrivilege,
                UnavailableReason = msr.UnavailableReason ?? "缺 ring0：MSR 讀取未就緒",
            };
        ulong? raw = msr.ReadMsr(index);
        return raw is null
            ? new RawRegisterRegion { Source = source, Availability = FactAvailability.ReadError, UnavailableReason = "MSR 讀取失敗（此平台可能未實作）" }
            : new RawRegisterRegion { Source = source, Bytes = BitConverter.GetBytes(raw.Value), VolatilityMask = volatileMask };
    }

    private static byte[] MakeMask(int length, params int[] volatileOffsets)
    {
        var mask = new byte[length];
        foreach (int i in volatileOffsets) mask[i] = 1;
        return mask;
    }
}
