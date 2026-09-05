using System.Diagnostics;
using System.Threading;

namespace XinSpect;

/// <summary>
/// PCI 設定空間讀寫的最小抽象。存在的唯一理由是讓 <see cref="ImcSmbusController"/> 的狀態機
/// 能在測試裡被完整驅動——設定空間裡一寫就讓機器當場停住的東西太多了。
/// </summary>
public interface IPciConfig
{
    uint? Read(byte bus, byte device, byte function, uint register);
    bool Write(byte bus, byte device, byte function, uint register, uint value);
}

/// <summary>把 <see cref="IPciConfig"/> 接到真實的 WinRing0 橋接上。</summary>
public sealed class WinRing0PciConfig(WinRing0Bridge bridge) : IPciConfig
{
    public uint? Read(byte bus, byte device, byte function, uint register)
        => bridge.ReadPciConfig(bus, device, function, register);

    public bool Write(byte bus, byte device, byte function, uint register, uint value)
        => bridge.WritePciConfig(bus, device, function, register, value);
}

/// <summary>一組 iMC SMBus 控制器的位置。</summary>
/// <param name="Segment">同一個裝置裡的第幾組控制器（暫存器位移差 4 位元組）。</param>
public sealed record ImcSmbusLocation(byte Bus, byte Device, byte Function, int Segment);

/// <summary>
/// 在處理器 uncore 裡找出 iMC SMBus 控制器。
/// </summary>
/// <remarks>
/// Skylake-X／SP 把它放在 PCU 的 function 5（8086:2085）的 PCI 設定空間裡，一個裝置帶兩組控制器。
/// uncore 匯流排號會隨平台與插槽數變動（本機是 0x16），所以<b>用裝置 ID 掃，不寫死位置</b>。
/// </remarks>
public static class ImcSmbusDiscovery
{
    /// <summary>Skylake-X／SP uncore 的 PCU function 5。iMC SMBus 的三組暫存器就在它的設定空間裡。</summary>
    public const uint PcuFunction5Id = 0x2085_8086;

    public const int MaxSegments = 2;

    public static List<ImcSmbusLocation> Find(IPciConfig pci, out string diagnostic)
    {
        var found = new List<ImcSmbusLocation>(MaxSegments);
        for (int bus = 0; bus < 256; bus++)
        {
            for (byte dev = 0; dev < 32; dev++)
            {
                for (byte fn = 0; fn < 8; fn++)
                {
                    if ((pci.Read((byte)bus, dev, fn, 0x00) ?? 0xFFFFFFFF) != PcuFunction5Id) continue;

                    for (int seg = 0; seg < MaxSegments; seg++)
                    {
                        uint? cmd = pci.Read((byte)bus, dev, fn, ImcSmbusController.CmdRegister(seg));
                        uint? sts = pci.Read((byte)bus, dev, fn, ImcSmbusController.StsRegister(seg));
                        uint? dat = pci.Read((byte)bus, dev, fn, ImcSmbusController.DatRegister(seg));
                        if (cmd is null or 0xFFFFFFFF || sts is null or 0xFFFFFFFF || dat is null or 0xFFFFFFFF)
                            continue;
                        if (cmd == 0 && sts == 0 && dat == 0) continue;      // 沒接出來的區段
                        found.Add(new ImcSmbusLocation((byte)bus, dev, fn, seg));
                    }

                    diagnostic = found.Count > 0
                        ? $"處理器 iMC SMBus 在 {bus:X2}:{dev:X2}.{fn}，共 {found.Count} 組控制器。"
                        : $"在 {bus:X2}:{dev:X2}.{fn} 找到 PCU function 5，但兩組 SMBus 暫存器都讀不出有效值。";
                    return found;
                }
            }
        }

        diagnostic = "找不到處理器 iMC SMBus（PCU function 5，8086:2085）——"
                   + "本讀取器的 iMC 路徑只驗證過 Skylake-X／SP 的 uncore 佈局。";
        return found;
    }
}
