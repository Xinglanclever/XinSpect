using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// NVIDIA Management Library（NVML，隨顯示卡驅動安裝的 nvml.dll）的精簡 P/Invoke 封裝。
/// 只綁定本模組實際會用到的函式：讀取（型號／溫度／時脈／功耗／風扇），以及
/// 「功耗上限」與「風扇轉速」的寫入——這兩者皆為 NVML 官方文件化、於 Pascal 世代可用的介面。
/// 所有函式回傳 0（NVML_SUCCESS）為成功；其餘為錯誤碼。呼叫端一律檢查回傳值，
/// 失敗即誠實回報，絕不假裝成功。核心／顯示記憶體頻率偏移與溫度上限屬驅動私有介面，見 <see cref="NvapiInterop"/>。
/// </summary>
internal static class NvmlInterop
{
    private const string Dll = "nvml.dll";

    // 時脈類型
    public const int CLOCK_GRAPHICS = 0;
    public const int CLOCK_SM = 1;
    public const int CLOCK_MEM = 2;
    // 溫度感測器
    public const int TEMPERATURE_GPU = 0;
    // 溫度門檻類型（nvmlTemperatureThresholds_t 的實際列舉順序，已用本機驅動實測校正：
    // 0→99°C 關機、1→96°C 降頻、2/3 在 Pascal 上回傳 NOT_SUPPORTED、4/5/6→65/84/93°C）
    public const int THRESHOLD_SHUTDOWN = 0;      // 硬體強制關機溫度（唯讀）
    public const int THRESHOLD_SLOWDOWN = 1;      // 觸發降頻保護的溫度（唯讀）
    public const int THRESHOLD_MEM_MAX = 2;       // 顯示記憶體上限（多數消費卡不支援）
    public const int THRESHOLD_GPU_MAX = 3;       // GPU 上限（多數消費卡不支援）
    public const int THRESHOLD_ACOUSTIC_MIN = 4;  // 可設定的目標溫度下限
    public const int THRESHOLD_ACOUSTIC_CURR = 5; // 目前的目標溫度＝「溫度上限」滑桿真正對應的旋鈕
    public const int THRESHOLD_ACOUSTIC_MAX = 6;  // 可設定的目標溫度上限

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] public static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] public static extern int Shutdown();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")] public static extern int GetCount(out uint count);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandleByIndex(uint index, out IntPtr device);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")]
    private static extern int GetNameRaw(IntPtr device, byte[] name, uint length);
    public static string GetName(IntPtr device)
    {
        var buf = new byte[96];
        return GetNameRaw(device, buf, (uint)buf.Length) == 0
            ? Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ')
            : "";
    }

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] public static extern int GetTemperature(IntPtr device, int sensor, out uint tempC);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperatureThreshold")] public static extern int GetTempThreshold(IntPtr device, int type, out uint tempC);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] public static extern int GetClock(IntPtr device, int type, out uint clockMhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")] public static extern int GetFanSpeed(IntPtr device, out uint percent);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetNumFans")] public static extern int GetNumFans(IntPtr device, out uint numFans);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimit")] public static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")] public static extern int GetPowerLimitDefault(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] public static extern int GetPowerLimitConstraints(IntPtr device, out uint minMw, out uint maxMw);

    // ── 深度靜態資訊（唯讀，用於「顯示卡」分頁的 NVML 硬核規格卡）───────────────────
    public const int CLOCK_VIDEO = 3;   // 影像編解碼時脈網域

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlPciInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] BusIdLegacy;
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;      // (deviceId << 16) | vendorId
        public uint PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] BusId;
    }

    [DllImport(Dll, EntryPoint = "nvmlSystemGetDriverVersion")] private static extern int SysDriverVerRaw(byte[] buf, uint len);
    [DllImport(Dll, EntryPoint = "nvmlSystemGetNVMLVersion")] private static extern int SysNvmlVerRaw(byte[] buf, uint len);
    [DllImport(Dll, EntryPoint = "nvmlSystemGetCudaDriverVersion_v2")] public static extern int SysCudaDriverVer(out int version);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetVbiosVersion")] private static extern int VbiosRaw(IntPtr d, byte[] buf, uint len);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUUID")] private static extern int UuidRaw(IntPtr d, byte[] buf, uint len);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetSerial")] private static extern int SerialRaw(IntPtr d, byte[] buf, uint len);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPciInfo_v3")] public static extern int GetPciInfo(IntPtr d, ref NvmlPciInfo pci);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrPcieLinkGeneration")] public static extern int GetPcieGen(IntPtr d, out int gen);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMaxPcieLinkGeneration")] public static extern int GetPcieGenMax(IntPtr d, out int gen);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrPcieLinkWidth")] public static extern int GetPcieWidth(IntPtr d, out int width);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMaxPcieLinkWidth")] public static extern int GetPcieWidthMax(IntPtr d, out int width);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPcieReplayCounter")] public static extern int GetPcieReplay(IntPtr d, out uint count);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int GetMemoryInfo(IntPtr d, ref NvmlMemory mem);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCudaComputeCapability")] public static extern int GetComputeCap(IntPtr d, out int major, out int minor);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetNumGpuCores")] public static extern int GetNumGpuCores(IntPtr d, out uint cores);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetArchitecture")] public static extern int GetArchitecture(IntPtr d, out uint arch);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetBrand")] public static extern int GetBrand(IntPtr d, out uint brand);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMaxClockInfo")] public static extern int GetMaxClock(IntPtr d, int type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPerformanceState")] public static extern int GetPerfState(IntPtr d, out int pstate);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] public static extern int GetEnforcedPowerLimit(IntPtr d, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] public static extern int GetThrottleReasons(IntPtr d, out ulong reasons);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPersistenceMode")] public static extern int GetPersistenceMode(IntPtr d, out int mode);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetComputeMode")] public static extern int GetComputeMode(IntPtr d, out int mode);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEccMode")] public static extern int GetEccMode(IntPtr d, out int current, out int pending);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEncoderUtilization")] public static extern int GetEncoderUtil(IntPtr d, out uint util, out uint samplingUs);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetDecoderUtilization")] public static extern int GetDecoderUtil(IntPtr d, out uint util, out uint samplingUs);

    /// <summary>把 NVML 以固定緩衝寫入的 ASCII 字串讀成 C# 字串；rc≠0 回 null（誠實省略）。</summary>
    private static string? Str(Func<byte[], uint, int> fn, int cap = 96)
    {
        var buf = new byte[cap];
        return fn(buf, (uint)buf.Length) == 0 ? Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ') is { Length: > 0 } s ? s : null : null;
    }

    public static string? DriverVersion() => Str(SysDriverVerRaw, 80);
    public static string? NvmlVersion() => Str(SysNvmlVerRaw, 80);
    public static string? VbiosVersion(IntPtr d) => Str((b, l) => VbiosRaw(d, b, l), 32);
    public static string? Uuid(IntPtr d) => Str((b, l) => UuidRaw(d, b, l), 96);
    public static string? Serial(IntPtr d) => Str((b, l) => SerialRaw(d, b, l), 32);

    // ── 寫入（需系統管理員權限）──────────────────────────────────────────────
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetPowerManagementLimit")] public static extern int SetPowerLimit(IntPtr device, uint milliwatts);

    [DllImport(Dll, EntryPoint = "nvmlDeviceSetFanSpeed_v2")] public static extern int SetFanSpeed(IntPtr device, uint fan, uint percent);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetDefaultFanSpeed_v2")] public static extern int SetDefaultFanSpeed(IntPtr device, uint fan);

    /// <summary>
    /// 設定溫度門檻（本模組只用 <see cref="THRESHOLD_ACOUSTIC_CURR"/>＝目標溫度）。
    /// 這是 NVML 官方文件化的介面，且在本機 Pascal 卡上實測可寫入並讀回變更；
    /// 驅動會把值夾在 ACOUSTIC_MIN…ACOUSTIC_MAX 之間，並透過同一參數回傳實際採用的值。
    /// </summary>
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetTemperatureThreshold")] public static extern int SetTempThreshold(IntPtr device, int type, ref int tempC);

    /// <summary>讀取溫度門檻；成功回傳攝氏值，失敗回 null（呼叫端據此誠實回報）。</summary>
    public static int? TryGetTempThreshold(IntPtr device, int type)
        => GetTempThreshold(device, type, out var v) == 0 ? (int)v : null;
}
