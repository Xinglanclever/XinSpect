namespace XinSpect;

/// <summary>Level Zero 列舉到的一顆 GPU 的唯讀快照。</summary>
public sealed record LevelZeroGpuSnapshot(
    string Name, uint VendorId, uint DeviceId,
    ulong MemoryBytes, uint MemoryClockMhz, uint BusWidthBits,
    uint DriverVersion);

/// <summary>
/// Level Zero 後端的抽象——讓事實層可在不掛真的 ze_loader.dll 的前提下測行為形狀。
/// 回 null＝loader 不可用（沒有 Intel 顯示卡／驅動未裝），呼叫端如實 NotApplicable。
/// </summary>
public interface ILevelZeroBackend
{
    LevelZeroGpuSnapshot? CollectSingleGpu();
}

/// <summary>
/// GPU 非 NVIDIA 事實（R6）：Intel 顯示卡經 Level Zero（ze_loader.dll，usermode、唯讀）。
/// <b>未在本機驗證</b>：本機是 NVIDIA；loader 不存在時整組 NotApplicable——
/// 沒有 Intel 顯示卡的使用者不該看到三態噪音。AMD Radeon 的標準層（名稱／顯存／驅動版本）
/// 已由 <see cref="GpuAnalysisService"/> 的 WMI 路徑涵蓋；ADLX 深度遙測另輪。
/// </summary>
public static class LevelZeroFactsService
{
    private const string Category = "顯示卡";
    private const string Source = "oneAPI Level Zero（ze_loader.dll 唯讀列舉）";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, ILevelZeroBackend? backend = null)
    {
        var snapshot = (backend ?? new LevelZeroBackend()).CollectSingleGpu();
        if (snapshot is null)
            return
            [
                new("gpu.lz.name", Category, "Intel GPU（Level Zero）", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "本機沒有 ze_loader.dll（沒有 Intel 顯示卡或驅動未裝）——整組不適用"),
                new("gpu.lz.memory", Category, "Intel GPU 顯存", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "本機沒有 ze_loader.dll——整組不適用"),
                new("gpu.lz.driver_version", Category, "Intel 顯示驅動版本（Level Zero）", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "本機沒有 ze_loader.dll——整組不適用"),
            ];

        return
        [
            new("gpu.lz.name", Category, "Intel GPU（Level Zero）",
                string.IsNullOrWhiteSpace(snapshot.Name) ? $"PCI 0x{snapshot.DeviceId:X4}" : snapshot.Name.Trim(), "",
                $"{Source}（未在本機驗證）", FactTrustLevel.Measured, false, at, null),
            new("gpu.lz.memory", Category, "Intel GPU 顯存",
                snapshot.MemoryBytes > 0
                    ? $"{snapshot.MemoryBytes / (1024.0 * 1024 * 1024):0.#} GB（{snapshot.MemoryClockMhz} MHz，{snapshot.BusWidthBits}-bit）"
                    : "—（驅動未回報本地記憶體）", "",
                $"{Source}（未在本機驗證）", FactTrustLevel.Measured, false, at, (long)snapshot.MemoryBytes),
            new("gpu.lz.driver_version", Category, "Intel 顯示驅動版本（Level Zero）",
                snapshot.DriverVersion > 0 ? $"0x{snapshot.DriverVersion:X8}" : "—", "",
                $"{Source}（未在本機驗證）", FactTrustLevel.Measured, false, at, snapshot.DriverVersion),
        ];
    }
}

/// <summary>走真的 ze_loader.dll 的後端。任何 loader 層例外都收斂成 null——三態由事實層統一表達。</summary>
public sealed class LevelZeroBackend : ILevelZeroBackend
{
    public LevelZeroGpuSnapshot? CollectSingleGpu()
    {
        try
        {
            if (LevelZeroInterop.Init(0) != LevelZeroInterop.ZeResultSuccess) return null;

            uint driverCount = 0;
            if (LevelZeroInterop.DriverGet(ref driverCount, null) != LevelZeroInterop.ZeResultSuccess || driverCount == 0)
                return null;
            var drivers = new IntPtr[driverCount];
            if (LevelZeroInterop.DriverGet(ref driverCount, drivers) != LevelZeroInterop.ZeResultSuccess)
                return null;

            foreach (var driver in drivers)
            {
                uint deviceCount = 0;
                if (LevelZeroInterop.DeviceGet(driver, ref deviceCount, null) != LevelZeroInterop.ZeResultSuccess
                    || deviceCount == 0)
                    continue;
                var devices = new IntPtr[deviceCount];
                if (LevelZeroInterop.DeviceGet(driver, ref deviceCount, devices) != LevelZeroInterop.ZeResultSuccess)
                    continue;

                foreach (var device in devices)
                {
                    var props = new LevelZeroInterop.ZeDeviceProperties
                    {
                        StructureType = LevelZeroInterop.StructureTypeDeviceProperties,
                    };
                    if (LevelZeroInterop.DeviceGetProperties(device, ref props) != LevelZeroInterop.ZeResultSuccess
                        || props.Type != LevelZeroInterop.DeviceTypeGpu)
                        continue;

                    ulong memoryBytes = 0;
                    uint clockMhz = 0, busWidth = 0;
                    uint memCount = 0;
                    if (LevelZeroInterop.DeviceGetMemoryProperties(device, ref memCount, null) == LevelZeroInterop.ZeResultSuccess
                        && memCount > 0)
                    {
                        var mems = new LevelZeroInterop.ZeDeviceMemoryProperties[memCount];
                        for (int i = 0; i < memCount; i++)
                            mems[i].StructureType = LevelZeroInterop.StructureTypeDeviceMemoryProperties;
                        if (LevelZeroInterop.DeviceGetMemoryProperties(device, ref memCount, mems) == LevelZeroInterop.ZeResultSuccess)
                        {
                            memoryBytes = mems[0].TotalSize;
                            clockMhz = mems[0].MaxClockRate;
                            busWidth = mems[0].MaxBusWidth;
                        }
                    }

                    var driverProps = new LevelZeroInterop.ZeDriverProperties
                    {
                        StructureType = LevelZeroInterop.StructureTypeDriverProperties,
                    };
                    uint driverVersion = LevelZeroInterop.DriverGetProperties(driver, ref driverProps) == LevelZeroInterop.ZeResultSuccess
                        ? driverProps.DriverVersion
                        : 0;

                    return new LevelZeroGpuSnapshot(props.NameText, props.VendorId, props.DeviceId,
                        memoryBytes, clockMhz, busWidth, driverVersion);
                }
            }
            return null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }
}
