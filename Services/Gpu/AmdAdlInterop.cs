using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// AMD Display Library（ADL，atiadlxx.dll，隨 Radeon 驅動安裝）的精簡 P/Invoke 封裝——<b>唯讀</b>遙測：
/// 溫度、風扇轉速、功耗、核心時脈。沒有任何 Set/Write 匯出被綁定。
/// 呼叫慣例與單位換算照 OpenHardwareMonitor 的長期實作交叉核對（2026-10-07 抓取
/// openhardwaremonitor Hardware/ATI/ADL.cs＋ATIGPU.cs）：Cdecl、ADL2 帶 context、
/// PMLog 感測器表優先、Overdrive5/6/N 依代數後備；溫度 milli-°C→÷1000、OD6 功率→÷255。
/// <b>未在本機驗證</b>：本機是 NVIDIA；DLL 不存在時 TryLoad 回 null，事實層統一 NotApplicable。
/// </summary>
internal sealed class AmdAdlInterop : IDisposable
{
    public const int AdlOk = 0;
    public const int AmdVendorId = 0x1002;
    public const int AdlMaxPath = 256;
    public const int AdlPmlogMaxSensors = 256;

    /// <summary>PMLog 感測器索引（ADLSensorType，OHM 同表）。</summary>
    public const int PmlogClkGfxclk = 1;
    public const int PmlogTemperatureEdge = 8;
    public const int PmlogFanRpm = 14;
    public const int PmlogAsicPower = 23;
    public const int PmlogGfxPower = 30;

    public const int OdnTempCore = 1;        // ADLODNTemperatureType.CORE
    public const int Od6PowerTotal = 0;      // ADLODNCurrentPowerType.TOTAL_POWER
    public const int FanSpeedTypeRpm = 2;    // ADL_DL_FANCTRL_SPEED_TYPE_RPM

    /// <summary>ADLAdapterInfo 單筆大小與關鍵欄位位移（ANSI ByValTStr 256；含尾端未用欄位的完整佈局）。</summary>
    public const int AdapterInfoSize = 1572;
    public const int OffAdapterIndex = 4, OffUdid = 8, OffBus = 264, OffDevice = 268, OffFunction = 272,
                    OffVendorId = 276, OffAdapterName = 280;

    private delegate IntPtr AdlMainMallocCallback(int size);
    private delegate int AdlMainControlCreate(AdlMainMallocCallback callback, int enumConnectedAdapters, out IntPtr context);
    private delegate int AdlMainControlDestroy(IntPtr context);
    private delegate int AdlAdapterNumberGet(IntPtr context, ref int count);
    private delegate int AdlAdapterInfoGet(IntPtr context, IntPtr info, int size);
    private delegate int AdlAdapterActiveGet(IntPtr context, int adapterIndex, out int status);
    private delegate int AdlOdnTemperatureGet(IntPtr context, int adapterIndex, int temperatureType, out int temperature);
    private delegate int AdlOd6PowerGet(IntPtr context, int adapterIndex, int powerType, out int value);
    private delegate int AdlOd5TemperatureGet(IntPtr context, int adapterIndex, int thermalControllerIndex, ref AdlTemperature temperature);
    private delegate int AdlOd5FanSpeedGet(IntPtr context, int adapterIndex, int thermalControllerIndex, ref AdlFanSpeedValue fanSpeed);
    private delegate int AdlPmlogGet(IntPtr context, int adapterIndex, IntPtr dataOutput);

    [StructLayout(LayoutKind.Sequential)]
    public struct AdlTemperature
    {
        public int Size;
        public int Temperature;     // milli-°C
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdlFanSpeedValue
    {
        public int Size;
        public int SpeedType;
        public int FanSpeed;        // RPM（SpeedType＝2 時）
        public int Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    private static T? GetExport<T>(IntPtr module, string name) where T : class, Delegate
        => GetProcAddress(module, name) is { } addr && addr != IntPtr.Zero
            ? Marshal.GetDelegateForFunctionPointer<T>(addr)
            : null;

    private readonly IntPtr _module;
    private readonly AdlMainControlCreate _mainControlCreate;
    private readonly AdlMainControlDestroy _mainControlDestroy;
    private readonly AdlAdapterNumberGet _adapterNumberGet;
    private readonly AdlAdapterInfoGet _adapterInfoGet;
    private readonly AdlAdapterActiveGet _adapterActiveGet;
    private readonly AdlOdnTemperatureGet? _odnTemperatureGet;
    private readonly AdlOd6PowerGet? _od6PowerGet;
    private readonly AdlOd5TemperatureGet? _od5TemperatureGet;
    private readonly AdlOd5FanSpeedGet? _od5FanSpeedGet;
    private readonly AdlPmlogGet? _pmlogGet;

    private static IntPtr Alloc(int size) => Marshal.AllocHGlobal(size);

    private AmdAdlInterop(IntPtr module)
    {
        _module = module;
        _mainControlCreate = GetExport<AdlMainControlCreate>(module, "ADL2_Main_Control_Create")!;
        _mainControlDestroy = GetExport<AdlMainControlDestroy>(module, "ADL2_Main_Control_Destroy")!;
        _adapterNumberGet = GetExport<AdlAdapterNumberGet>(module, "ADL2_Adapter_NumberOfAdapters_Get")!;
        _adapterInfoGet = GetExport<AdlAdapterInfoGet>(module, "ADL2_Adapter_AdapterInfo_Get")!;
        _adapterActiveGet = GetExport<AdlAdapterActiveGet>(module, "ADL2_Adapter_Active_Get")!;
        _odnTemperatureGet = GetExport<AdlOdnTemperatureGet>(module, "ADL2_OverdriveN_Temperature_Get");
        _od6PowerGet = GetExport<AdlOd6PowerGet>(module, "ADL2_Overdrive6_CurrentPower_Get");
        _od5TemperatureGet = GetExport<AdlOd5TemperatureGet>(module, "ADL2_Overdrive5_Temperature_Get");
        _od5FanSpeedGet = GetExport<AdlOd5FanSpeedGet>(module, "ADL2_Overdrive5_FanSpeed_Get");
        _pmlogGet = GetExport<AdlPmlogGet>(module, "ADL2_New_QueryPMLogData_Get");
    }

    /// <summary>載入 atiadlxx.dll（後備 atiadlxy.dll）；缺 DLL 或缺必要匯出回 null。</summary>
    public static AmdAdlInterop? TryLoad()
    {
        foreach (string name in new[] { "atiadlxx.dll", "atiadlxy.dll" })
        {
            IntPtr module = LoadLibrary(name);
            if (module == IntPtr.Zero) continue;
            var api = new AmdAdlInterop(module);
            if (api._mainControlCreate is null || api._adapterNumberGet is null)
            {
                FreeLibrary(module);
                continue;
            }
            return api;
        }
        return null;
    }

    public bool MainControlCreate(out IntPtr context)
    {
        context = IntPtr.Zero;
        return _mainControlCreate(Alloc, 1, out context) == AdlOk;
    }

    public bool MainControlDestroy(IntPtr context) => _mainControlDestroy(context) == AdlOk;

    public bool AdapterNumberGet(IntPtr context, ref int count) => _adapterNumberGet(context, ref count) == AdlOk;
    public bool AdapterActiveGet(IntPtr context, int adapterIndex, out int status) => _adapterActiveGet(context, adapterIndex, out status) == AdlOk;
    public bool OverdriveNTemperature(IntPtr context, int adapterIndex, int type, out int temperature)
    {
        temperature = 0;
        return _odnTemperatureGet is { } f && f(context, adapterIndex, type, out temperature) == AdlOk;
    }
    public bool Overdrive6Power(IntPtr context, int adapterIndex, int type, out int value)
    {
        value = 0;
        return _od6PowerGet is { } f && f(context, adapterIndex, type, out value) == AdlOk;
    }
    public bool Overdrive5Temperature(IntPtr context, int adapterIndex, ref AdlTemperature temperature)
        => _od5TemperatureGet is { } f && f(context, adapterIndex, 0, ref temperature) == AdlOk;
    public bool Overdrive5FanSpeed(IntPtr context, int adapterIndex, ref AdlFanSpeedValue fanSpeed)
        => _od5FanSpeedGet is { } f && f(context, adapterIndex, 0, ref fanSpeed) == AdlOk;

    /// <summary>一次取回全部 AdapterInfo（ADL2 語意：info 是 count 筆連續結構的陣列）。讀取失敗回 null。</summary>
    public AdapterInfoEntry[]? ReadAdapterInfos(IntPtr context, int count)
    {
        if (count <= 0) return [];
        IntPtr buffer = Marshal.AllocHGlobal(AdapterInfoSize * count);
        try
        {
            for (int i = 0; i < count; i++)
                Marshal.WriteInt32(buffer, i * AdapterInfoSize, AdapterInfoSize); // info.Size 要先填
            if (_adapterInfoGet(context, buffer, AdapterInfoSize * count) != AdlOk) return null;

            var entries = new AdapterInfoEntry[count];
            for (int i = 0; i < count; i++)
            {
                int baseOffset = i * AdapterInfoSize;
                entries[i] = new AdapterInfoEntry(
                    AdapterIndex: Marshal.ReadInt32(buffer, baseOffset + OffAdapterIndex),
                    BusNumber: Marshal.ReadInt32(buffer, baseOffset + OffBus),
                    DeviceNumber: Marshal.ReadInt32(buffer, baseOffset + OffDevice),
                    FunctionNumber: Marshal.ReadInt32(buffer, baseOffset + OffFunction),
                    VendorId: Marshal.ReadInt32(buffer, baseOffset + OffVendorId),
                    AdapterName: ReadAscii(buffer, baseOffset + OffAdapterName));
            }
            return entries;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadAscii(IntPtr buffer, int offset)
    {
        var bytes = new List<byte>(AdlMaxPath);
        for (int i = 0; i < AdlMaxPath; i++)
        {
            byte b = Marshal.ReadByte(buffer, offset + i);
            if (b == 0) break;
            bytes.Add(b);
        }
        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }

    /// <summary>讀 PMLog 感測器表。回 256 格陣列，<see cref="PmlogUnsupported"/> 標「不支援」；呼叫失敗回 null。</summary>
    public const int PmlogUnsupported = int.MinValue;

    public int[]? ReadPmlog(IntPtr context, int adapterIndex)
    {
        int bufferSize = 4 + AdlPmlogMaxSensors * 8;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, bufferSize);
            if (_pmlogGet is null || _pmlogGet(context, adapterIndex, buffer) != AdlOk) return null;
            int reported = Marshal.ReadInt32(buffer, 0);
            if (reported < bufferSize) return null;

            var values = new int[AdlPmlogMaxSensors];
            for (int i = 0; i < AdlPmlogMaxSensors; i++)
                values[i] = Marshal.ReadInt32(buffer, 4 + i * 8) != 0
                    ? Marshal.ReadInt32(buffer, 4 + i * 8 + 4)
                    : PmlogUnsupported;
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => FreeLibrary(_module);
}

/// <summary>AdapterInfo 裡本模組需要的欄位。</summary>
public readonly record struct AdapterInfoEntry(
    int AdapterIndex, int BusNumber, int DeviceNumber, int FunctionNumber, int VendorId, string AdapterName);
