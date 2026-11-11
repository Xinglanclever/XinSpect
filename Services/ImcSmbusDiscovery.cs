using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
public sealed class WinRing0PciConfig : IPciConfig
{
    private readonly WinRing0Bridge _bridge;
    private readonly bool _allowReadProtocolWrites;

    public WinRing0PciConfig(WinRing0Bridge bridge) : this(bridge, allowReadProtocolWrites: false) { }

    internal WinRing0PciConfig(WinRing0Bridge bridge, bool allowReadProtocolWrites)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _allowReadProtocolWrites = allowReadProtocolWrites;
    }

    public uint? Read(byte bus, byte device, byte function, uint register)
        => _bridge.ReadPciConfig(bus, device, function, register);

    public bool Write(byte bus, byte device, byte function, uint register, uint value)
        => _allowReadProtocolWrites
            ? _bridge.WritePciForReadProtocol(bus, device, function, register, value)
            : _bridge.WritePciConfig(bus, device, function, register, value);
}

/// <summary>一組 iMC SMBus 控制器的位置。</summary>
/// <param name="Segment">同一個裝置裡的第幾組控制器（暫存器位移差 4 位元組）。</param>
public sealed record ImcSmbusLocation(byte Bus, byte Device, byte Function, int Segment);

/// <summary>A PCI bus/device/function address reported by Windows PnP.</summary>
public readonly record struct PciFunctionAddress(byte Bus, byte Device, byte Function);

/// <summary>Finds present PCI devnodes for a vendor/device pair without probing the PCI address space.</summary>
public interface IPciFunctionLocationSource
{
    IReadOnlyList<PciFunctionAddress> Find(ushort vendorId, ushort deviceId, out string diagnostic);
}

/// <summary>Uses SetupAPI's present-device list and PCI bus/address properties.</summary>
public sealed class WindowsPciFunctionLocationSource : IPciFunctionLocationSource
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint ErrorNoMoreItems = 259;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpBusNumber = 0x00000015;
    private const uint SpdrpAddress = 0x0000001C;
    private const uint RegDword = 4;

    public IReadOnlyList<PciFunctionAddress> Find(ushort vendorId, ushort deviceId, out string diagnostic)
    {
        nint set = SetupDiGetClassDevs(nint.Zero, "PCI", 0, DigcfPresent | DigcfAllClasses);
        if (set == -1 || set == 0)
        {
            diagnostic = $"Windows PCI 裝置列舉失敗（Win32 {Marshal.GetLastWin32Error()}）。";
            return [];
        }

        var found = new HashSet<PciFunctionAddress>();
        int matched = 0, missingLocation = 0;
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if ((uint)error == ErrorNoMoreItems) break;
                    diagnostic = $"Windows PCI 裝置列舉中斷（Win32 {error}）。";
                    return found.ToArray();
                }

                if (!HasHardwareId(set, ref info, vendorId, deviceId)) continue;
                matched++;
                if (!TryReadDword(set, ref info, SpdrpBusNumber, out uint bus)
                    || !TryReadDword(set, ref info, SpdrpAddress, out uint address)
                    || bus > byte.MaxValue
                    || !TryDecodeAddress((byte)bus, address, out var location))
                {
                    missingLocation++;
                    continue;
                }
                found.Add(location);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        diagnostic = found.Count > 0
            ? $"Windows PnP 找到 {found.Count} 個目標 PCI function" + (missingLocation > 0 ? $"；另有 {missingLocation} 個缺少有效位置" : "") + "。"
            : matched > 0
                ? $"找到 {matched} 個目標 PCI 裝置，但 Windows 未提供有效 bus/device/function 位置。"
                : $"Windows PnP 未列出 PCI {vendorId:X4}:{deviceId:X4}。";
        return found.OrderBy(x => x.Bus).ThenBy(x => x.Device).ThenBy(x => x.Function).ToArray();
    }

    internal static bool TryDecodeAddress(byte bus, uint address, out PciFunctionAddress location)
    {
        uint device = address >> 16;
        uint function = address & 0xFFFF;
        if (device >= 32 || function >= 8)
        {
            location = default;
            return false;
        }
        location = new PciFunctionAddress(bus, (byte)device, (byte)function);
        return true;
    }

    internal static bool TryDecodeDword(ReadOnlySpan<byte> bytes, out uint value)
    {
        if (bytes.Length < sizeof(uint))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private static bool HasHardwareId(nint set, ref SpDevInfoData info, ushort vendorId, ushort deviceId)
    {
        byte[]? bytes = ReadRegistryProperty(set, ref info, SpdrpHardwareId, out uint type);
        if (bytes is null || type != 7) return false;
        string[] ids = System.Text.Encoding.Unicode.GetString(bytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var identity = PciResourceAudit.ParseHardwareIds(ids);
        return identity?.VendorId == vendorId && identity.DeviceId == deviceId;
    }

    private static bool TryReadDword(nint set, ref SpDevInfoData info, uint property, out uint value)
    {
        byte[]? bytes = ReadRegistryProperty(set, ref info, property, out uint type);
        if (type == RegDword && bytes is not null && TryDecodeDword(bytes, out value)) return true;
        value = 0;
        return false;
    }

    private static byte[]? ReadRegistryProperty(nint set, ref SpDevInfoData info, uint property, out uint type)
    {
        type = 0;
        SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, null, 0, out uint required);
        if (required == 0) return null;
        var bytes = new byte[required];
        return SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, bytes, required, out _)
            ? bytes : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(nint classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);
}

/// <summary>
/// 在 Windows 已列出的 PCI function 中定位 iMC SMBus 控制器。
/// </summary>
/// <remarks>
/// Skylake-X／SP 把它放在 PCU 的 function 5（8086:2085）的 PCI 設定空間裡，一個裝置帶兩組控制器。
/// PCI 設定空間只讀 PnP 回報的候選位置；不再逐一探測 256×32×8 個 BDF。
/// </remarks>
public static class ImcSmbusDiscovery
{
    /// <summary>Skylake-X／SP uncore 的 PCU function 5。iMC SMBus 的三組暫存器就在它的設定空間裡。</summary>
    public const uint PcuFunction5Id = 0x2085_8086;

    public const int MaxSegments = 2;

    public static List<ImcSmbusLocation> Find(IPciConfig pci, IEnumerable<PciFunctionAddress> candidates,
                                              out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(pci);
        ArgumentNullException.ThrowIfNull(candidates);
        var found = new List<ImcSmbusLocation>(MaxSegments);
        var locations = candidates.Distinct().ToArray();
        foreach (var candidate in locations)
        {
            if ((pci.Read(candidate.Bus, candidate.Device, candidate.Function, 0x00) ?? 0xFFFFFFFF) != PcuFunction5Id)
                continue;

            for (int seg = 0; seg < MaxSegments; seg++)
            {
                uint? cmd = pci.Read(candidate.Bus, candidate.Device, candidate.Function, ImcSmbusController.CmdRegister(seg));
                uint? sts = pci.Read(candidate.Bus, candidate.Device, candidate.Function, ImcSmbusController.StsRegister(seg));
                uint? dat = pci.Read(candidate.Bus, candidate.Device, candidate.Function, ImcSmbusController.DatRegister(seg));
                if (cmd is null or 0xFFFFFFFF || sts is null or 0xFFFFFFFF || dat is null or 0xFFFFFFFF)
                    continue;
                if (cmd == 0 && sts == 0 && dat == 0) continue;
                found.Add(new ImcSmbusLocation(candidate.Bus, candidate.Device, candidate.Function, seg));
            }
        }

        diagnostic = found.Count > 0
            ? $"處理器 iMC SMBus 在 {string.Join("、", found.Select(x => $"{x.Bus:X2}:{x.Device:X2}.{x.Function} 第 {x.Segment} 組"))}。"
            : locations.Length == 0
                ? "Windows 沒有提供 PCU function 5 的 PCI 位置；已略過全 PCI 空間探測。"
                : $"已檢查 Windows 列出的 {locations.Length} 個 PCI 候選位置，找不到有效的 iMC SMBus 暫存器。";
        return found;
    }
}
