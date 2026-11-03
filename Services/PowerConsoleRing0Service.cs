namespace XinSpect;

/// <summary>強控台 SECTION 02 的一台 PCI 裝置：BDF、IDs、名稱與 256B 設定空間。</summary>
public sealed record PciConsoleDevice(
    byte Bus, byte Device, byte Function,
    ushort VendorId, ushort DeviceId,
    byte BaseClass, byte SubClass, byte ProgIf, byte Revision,
    string Name,
    IReadOnlyList<uint> ConfigSpace /* 64 個 DWORD＝0x00–0xFF */);

/// <summary>強控台 SECTION 02 的一次 MSR 讀取結果。</summary>
public sealed record MsrConsoleRead(uint Index, ulong Value, ulong Eax, ulong Edx, string? Error);

/// <summary>
/// 強控台的 Ring0 唯讀通路：PCI 設定空間掃描與 MSR 讀取。
/// </summary>
/// <remarks>
/// <para>
/// <b>唯讀。</b>PCI 只讀（<see cref="WinRing0Bridge.ReadPciConfig"/>），MSR 只 RDMSR；
/// 不呼叫任何 Write 通路，也不碰 IO 埠寫。
/// </para>
/// <para>
/// 掃描是<b>全匯流排</b>（bus 0–255、dev 0–31、fn 0–7）：ECAM／PCI Express 機制下，
/// 不存在的位址回 0xFFFF，不是「卡住」。整輪掃描放背景執行緒，UI 只吃結果。
/// </para>
/// </remarks>
public static class PowerConsoleRing0Service
{
    public const int BusMax = 255, DevMax = 31, FuncMax = 7;

    /// <summary>掃描全 PCI 匯流排；回傳回應的裝置（含 256B 設定空間快照）。讀不到 Ring0 就回 null 與原因。</summary>
    public static (IReadOnlyList<PciConsoleDevice>? Devices, string? Error) ScanPci()
    {
        using var ring0 = WinRing0Bridge.Create();
        if (!ring0.Available) return (null, ring0.Error ?? "Ring0 驅動沒有載入");
        if (!ring0.PciAvailable) return (null, "此版 Ring0 沒有 PCI 設定空間讀取能力");

        var devices = new List<PciConsoleDevice>();
        for (int bus = 0; bus <= BusMax; bus++)
        for (int dev = 0; dev <= DevMax; dev++)
        for (int fn = 0; fn <= FuncMax; fn++)
        {
            // 只掃 fn 0 開頭：fn0 不存在就跳過整組（多功能裝置才會有 fn1+）
            uint dword0 = ring0.ReadPciConfig((byte)bus, (byte)dev, (byte)fn, 0x00) ?? 0xFFFFFFFFu;
            ushort vendor = (ushort)(dword0 & 0xFFFF);
            if (vendor == 0xFFFF || vendor == 0x0000) continue;
            if (fn > 0 && !MultiFunction(devices, (byte)bus, (byte)dev)) continue;

            ushort deviceId = (ushort)(dword0 >> 16);
            uint cls = ring0.ReadPciConfig((byte)bus, (byte)dev, (byte)fn, 0x08) ?? 0;
            byte rev = (byte)(cls & 0xFF), progIf = (byte)((cls >> 8) & 0xFF);
            byte sub = (byte)((cls >> 16) & 0xFF), baseCls = (byte)((cls >> 24) & 0xFF);

            var cfg = new List<uint>(64);
            for (uint reg = 0; reg < 0x100; reg += 4)
                cfg.Add(ring0.ReadPciConfig((byte)bus, (byte)dev, (byte)fn, reg) ?? 0xFFFFFFFFu);

            string name = PciIdLookup.Describe((vendor, deviceId), (baseCls, sub, progIf, rev));
            devices.Add(new PciConsoleDevice((byte)bus, (byte)dev, (byte)fn, vendor, deviceId,
                baseCls, sub, progIf, rev, name, cfg));
        }
        return (devices, null);
    }

    private static bool MultiFunction(List<PciConsoleDevice> found, byte bus, byte dev)
        => found.Any(d => d.Bus == bus && d.Device == dev && d.Function == 0);

    /// <summary>讀一支 MSR（唯讀 RDMSR）。失敗帶原因，不編值。</summary>
    public static MsrConsoleRead ReadMsr(uint index)
    {
        using var ring0 = WinRing0Bridge.Create();
        if (!ring0.Available) return new MsrConsoleRead(index, 0, 0, 0, ring0.Error ?? "Ring0 驅動沒有載入");
        ulong? v = ring0.ReadMsrPair64(index);
        return v is null
            ? new MsrConsoleRead(index, 0, 0, 0, "RDMSR 讀不到（可能不支援或被核心擋下）")
            : new MsrConsoleRead(index, v.Value, v.Value & 0xFFFFFFFF, v.Value >> 32, null);
    }
}
