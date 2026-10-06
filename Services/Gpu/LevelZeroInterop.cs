using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// oneAPI Level Zero loader（ze_loader.dll，隨 Intel 顯示卡驅動安裝）的精簡 P/Invoke 封裝。
/// 只綁定本模組實際會用到的函式：列舉驅動／裝置、讀裝置屬性（型號、PCI ID）、記憶體屬性、驅動版本。
/// 全部唯讀。回傳 0（ZE_RESULT_SUCCESS）為成功；失敗即誠實回報，絕不假裝成功。
/// <b>未在本機驗證</b>：本機沒有 Intel 顯示卡；結構佈局與常數照 Level Zero spec 標頭
/// （oneapi-src/level-zero include/ze_api.h，2026-10-07 抓取）逐欄定義。
/// </summary>
/// <remarks>
/// 佈局細節：ze_device_properties_t / ze_device_memory_properties_t / ze_driver_properties_t
/// 的欄位順序與對齊照 spec 標頭（Pack=8 自然對齊，名稱欄 char[ZE_MAX_DEVICE_NAME=256]）。
/// stype 使用 0x1（DRIVER_PROPERTIES）、0x3（DEVICE_PROPERTIES）、0x7（DEVICE_MEMORY_PROPERTIES）。
/// </remarks>
internal static class LevelZeroInterop
{
    private const string Dll = "ze_loader.dll";

    public const int ZeResultSuccess = 0;
    public const uint StructureTypeDriverProperties = 0x1;
    public const uint StructureTypeDeviceProperties = 0x3;
    public const uint StructureTypeDeviceMemoryProperties = 0x7;
    public const int DeviceTypeGpu = 1;
    public const int MaxDeviceName = 256;

    [DllImport(Dll, EntryPoint = "zeInit")] public static extern int Init(uint flags);
    [DllImport(Dll, EntryPoint = "zeDriverGet")] public static extern int DriverGet(ref uint count, IntPtr[]? drivers);
    [DllImport(Dll, EntryPoint = "zeDeviceGet")] public static extern int DeviceGet(IntPtr driver, ref uint count, IntPtr[]? devices);

    [DllImport(Dll, EntryPoint = "zeDriverGetProperties")]
    public static extern int DriverGetProperties(IntPtr driver, ref ZeDriverProperties properties);

    [DllImport(Dll, EntryPoint = "zeDeviceGetProperties")]
    public static extern int DeviceGetProperties(IntPtr device, ref ZeDeviceProperties properties);

    [DllImport(Dll, EntryPoint = "zeDeviceGetMemoryProperties")]
    public static extern int DeviceGetMemoryProperties(IntPtr device, ref uint count, ZeDeviceMemoryProperties[]? properties);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct ZeDriverProperties
    {
        public uint StructureType;      // stype（進場前填 0x1）
        private IntPtr _pNext;
        public ulong DriverUuid0;
        public ulong DriverUuid1;
        public uint DriverVersion;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public unsafe struct ZeDeviceProperties
    {
        public uint StructureType;      // stype（進場前填 0x3）
        private IntPtr _pNext;
        public int Type;                // ze_device_type_t
        public uint VendorId;
        public uint DeviceId;
        public uint Flags;
        public uint SubdeviceId;
        public uint CoreClockRate;
        public ulong MaxMemAllocSize;
        public uint MaxHardwareContexts;
        public uint MaxCommandQueuePriority;
        public uint NumThreadsPerEU;
        public uint PhysicalEUSimdWidth;
        public uint NumEUsPerSubslice;
        public uint NumSubslicesPerSlice;
        public uint NumSlices;
        public ulong TimerResolution;
        public uint TimestampValidBits;
        public uint KernelTimestampValidBits;
        public ulong Uuid0;
        public ulong Uuid1;
        public fixed byte Name[MaxDeviceName];

        public readonly string NameText
        {
            get
            {
                fixed (byte* p = Name)
                {
                    int n = 0;
                    while (n < MaxDeviceName && p[n] != 0) n++;
                    return Encoding.ASCII.GetString(p, n);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public unsafe struct ZeDeviceMemoryProperties
    {
        public uint StructureType;      // stype（進場前填 0x7）
        private IntPtr _pNext;
        public uint Flags;
        public uint MaxClockRate;       // MHz
        public uint MaxBusWidth;        // bits
        public ulong TotalSize;         // bytes
        public fixed byte Name[MaxDeviceName];
    }
}
