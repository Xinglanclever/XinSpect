namespace XinSpect;

/// <summary>
/// MCHBAR（記憶體控制器 MMIO 視窗）三態事實。
/// 基底由 PCI 0:0.0 +0x48 的 64-bit BAR 取得（usermode 經 WinRing0 今天就讀得到）；
/// 暫存器本體要 MMIO——而且**刻意不解碼時序**：tCL/tRCD/tRP/tRAS 佈局在 Intel 公開規格未定義
/// （屬 MRC 訓練結果區），社群逆向值不合「對準規格」門檻。世代判定（CPUID）已接入，
/// 事實文字說清「本機是哪個世代、為什麼不出值」——不出值是誠實界線，不是待辦遺漏。
/// </summary>
public static class MchbarService
{
    private const string Category = "記憶體控制器";

    public static IReadOnlyList<HardwareFact> Collect(IPciConfigReader pci, IMmioReader mmio, DateTimeOffset at,
        Func<uint?>? cpuIdProbe = null)
    {
        const string baseKey = "mchbar.base", regKey = "mchbar.registers";
        const string baseName = "MCHBAR 基底", regName = "MCHBAR 暫存器讀取";

        if (!pci.Available)
        {
            string reason = pci.UnavailableReason ?? "缺 ring0：特權讀取未就緒";
            return
            [
                Unavailable(baseKey, baseName, "PCI 0:0.0+0x48", at, FactAvailability.InsufficientPrivilege, reason),
                Unavailable(regKey, regName, "MCHBAR MMIO", at, FactAvailability.InsufficientPrivilege, reason),
            ];
        }

        uint? lo = pci.ReadDword(0, 0, 0, 0x48);
        if (lo is null)
        {
            return
            [
                Unavailable(baseKey, baseName, "PCI 0:0.0+0x48", at, FactAvailability.ReadError, "PCI 設定空間讀取失敗"),
                Unavailable(regKey, regName, "MCHBAR MMIO", at, FactAvailability.ReadError, "PCI 設定空間讀取失敗"),
            ];
        }
        if (lo.Value == 0xFFFFFFFF)
        {
            return
            [
                Unavailable(baseKey, baseName, "PCI 0:0.0+0x48", at, FactAvailability.NotApplicable, "找不到主機橋（0:0.0 無回應）"),
                Unavailable(regKey, regName, "MCHBAR MMIO", at, FactAvailability.NotApplicable, "找不到主機橋（0:0.0 無回應）"),
            ];
        }
        if ((lo.Value & 0x1) == 0)
        {
            return
            [
                Unavailable(baseKey, baseName, "PCI 0:0.0+0x48", at, FactAvailability.NotApplicable, "MCHBAR 未啟用（BIOS 未映射此視窗）"),
                Unavailable(regKey, regName, "MCHBAR MMIO", at, FactAvailability.NotApplicable, "MCHBAR 未啟用（BIOS 未映射此視窗）"),
            ];
        }

        uint hi = pci.ReadDword(0, 0, 0, 0x4C) ?? 0;
        ulong mchbar = ((lo.Value & 0xFFFF8000u) | ((ulong)hi << 32)); // bit0=enable、bit14:1 保留；基底 32KiB 對齊（近代平台）
        var baseFact = new HardwareFact(baseKey, Category, baseName,
            $"0x{mchbar:X8}（32KiB 對齊；時序暫存器在公開規格未定義，本版僅報基底）", "", "PCI 0:0.0+0x48",
            FactTrustLevel.Measured, false, at);

        if (!mmio.Available)
            return [baseFact, Unavailable(regKey, regName, "MCHBAR MMIO", at, FactAvailability.InsufficientPrivilege,
                $"{mmio.UnavailableReason ?? "缺 MMIO 讀取"}；MCHBAR 0x{mchbar:X8} 需 MMIO")];

        var block = mmio.ReadBlock(mchbar, 0x100);
        return block is null
            ? [baseFact, Unavailable(regKey, regName, "MCHBAR MMIO", at, FactAvailability.ReadError,
                $"MCHBAR MMIO 讀取失敗{(mmio.LastFailReason is { } f ? $"：{f}" : "")}")]
            : [baseFact, new HardwareFact(regKey, Category, regName,
                $"已映射可讀（{GenerationText(cpuIdProbe)}——時序暫存器 tCL/tRCD/tRP/tRAS 佈局在公開規格未定義（屬 MRC 訓練結果區），維持不解碼；不出值是誠實界線，不是待辦遺漏）",
                "", $"MCHBAR 0x{mchbar:X8}", FactTrustLevel.Measured, false, at)];
    }

    /// <summary>世代文字：CPUID 判定成功就說明本機世代，失敗如實說未判定——兩種都不影響「不出值」的界線。</summary>
    private static string GenerationText(Func<uint?>? cpuIdProbe)
    {
        uint? eax = (cpuIdProbe ?? CpuGeneration.ReadSignature)();
        if (eax is not { } signature) return "世代未判定（CPUID 不可用）";
        var (family, model, _) = CpuGeneration.DecodeSignature(signature);
        return CpuGeneration.GenerationName(family, model) is { } name ? $"本機世代＝{name}" : $"世代未收錄（family {family}、model 0x{model:X2}）";
    }

    /// <summary>解析 MCHBAR 基底（提供原始快照收集器重用）：缺 ring0／無主機橋／未啟用回 null，細節由 Collect 的三態事實承載。</summary>
    public static ulong? ResolveBase(IPciConfigReader pci)
    {
        if (!pci.Available) return null;
        uint? lo = pci.ReadDword(0, 0, 0, 0x48);
        if (lo is null || lo.Value == 0xFFFFFFFF || (lo.Value & 0x1) == 0) return null;
        uint hi = pci.ReadDword(0, 0, 0, 0x4C) ?? 0;
        return (lo.Value & 0xFFFF8000u) | ((ulong)hi << 32);
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
