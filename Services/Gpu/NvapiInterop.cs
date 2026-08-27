using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// NVAPI（nvapi64.dll）私有介面的精簡封裝，專用於 NVML 未涵蓋的一項寫入：
/// 核心／顯示記憶體「頻率偏移」(Pstates20 freqDelta)。此為 NVIDIA 未公開文件化的介面，
/// 透過 nvapi_QueryInterface(id) 取得函式指標後呼叫，與 MSI Afterburner／EVGA 等超頻工具同途徑。
/// （溫度上限已改走官方文件化的 NVML nvmlDeviceSetTemperatureThreshold，見 <see cref="NvmlInterop"/>；
///  本機驅動 576.88 的 NVAPI ClientThermalPoliciesSetStatus 介面 id 已被移除，QueryInterface 回 NULL。）
///
/// 安全性：Pstates20 結構帶「版本＋大小」標頭，若本程式佈局與驅動不符，驅動會回傳
/// NVAPI_INCOMPATIBLE_STRUCT_VERSION（-9）而「不寫入任何資料」——佈局有出入只會導致
/// 「無法套用」（誠實回報），不會寫入亂數。寫入前先讀回結構取得該網域 typeId 與合法範圍並夾值，
/// 再以最小結構寫入。所有呼叫檢查回傳碼，失敗即誠實回報。
/// </summary>
internal static class NvapiInterop
{
    private const string Dll = "nvapi64.dll";

    [DllImport(Dll, EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    // 公開時脈網域
    public const uint CLOCK_GRAPHICS = 0;   // 核心
    public const uint CLOCK_MEMORY = 4;     // 顯示記憶體

    // ── 函式指標委派 ─────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Fn0();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnEnum([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnGpuBuf(IntPtr gpu, IntPtr pInfo);

    private static Fn0? _init, _unload;
    private static FnEnum? _enum;
    private static FnGpuBuf? _getPstates20, _setPstates20;

    private static T? Get<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public static bool Available { get; private set; }
    private static IntPtr _gpu;

    /// <summary>初始化 NVAPI 並取得第一張實體 GPU 控制代碼。回傳是否可用。</summary>
    public static bool Initialize()
    {
        try
        {
            _init = Get<Fn0>(0x0150E828);              // NvAPI_Initialize
            _unload = Get<Fn0>(0xD22BDD7E);            // NvAPI_Unload
            _enum = Get<FnEnum>(0xE5AC921F);           // NvAPI_EnumPhysicalGPUs
            _getPstates20 = Get<FnGpuBuf>(0x6FF81213); // NvAPI_GPU_GetPstates20
            _setPstates20 = Get<FnGpuBuf>(0x0F4DAE6B); // NvAPI_GPU_SetPstates20

            if (_init is null || _enum is null) return false;
            if (_init() != 0) return false;

            var handles = new IntPtr[64];
            if (_enum(handles, out int count) != 0 || count <= 0) return false;
            _gpu = handles[0];
            Available = _gpu != IntPtr.Zero;
            return Available;
        }
        catch { return false; }   // nvapi64.dll 不存在或介面缺失
    }

    public static void Shutdown()
    {
        try { _unload?.Invoke(); } catch { }
        Available = false;
    }

    private static uint MakeVersion(int structSize, int ver) => (uint)structSize | ((uint)ver << 16);

    // ══ Pstates20：核心／顯示記憶體頻率偏移 ═══════════════════════════════════════════
    // 佈局（NV_GPU_PERF_PSTATES20_INFO_V2，總長 7416 位元組）：
    //   [0]version [4]flags [8]numPstates [12]numClocks [16]numBaseVoltages
    //   [20]pstates[16]，每個 pstate 456 位元組：
    //       +0 pstateId  +4 flags  +8 clocks[8]（每筆 44）  +360 baseVoltages[4]（每筆 24）
    //   每筆 clock：+0 domainId +4 typeId +8 flags +12 freqDelta.value +16 min +20 max +24 data(20)
    private const int PS20_SIZE = 7416;
    private const int PS_STRIDE = 456;
    private const int CLK_STRIDE = 44;

    /// <summary>
    /// 設定指定網域（核心／顯示記憶體）的頻率偏移（kHz）。
    /// 先以 GET 讀回結構取得該網域的 typeId 與合法範圍並夾值，再以「最小結構」
    /// （numPstates=1、numClocks=1、僅 P0 該網域一筆 freqDelta）呼叫 Set。
    /// 本機實測：帶回整個 7416 位元組讀回結構的 Set 會回傳 -104 NOT_SUPPORTED；
    /// 最小結構的 Set 回傳 0 且回讀確實改變（-50MHz 降頻→回讀 -50000kHz→還原 0）。
    /// </summary>
    public static int SetClockOffset(uint domain, int deltaKHz)
    {
        if (!Available || _getPstates20 is null || _setPstates20 is null) return -1;

        // 先讀回目前結構，取得該網域的 typeId 與合法範圍
        uint typeId = 1;
        int min = 0, max = 0;
        var gbuf = new byte[PS20_SIZE];
        var gspan = gbuf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(gspan, MakeVersion(PS20_SIZE, 2));
        IntPtr gp = Marshal.AllocHGlobal(PS20_SIZE);
        try
        {
            Marshal.Copy(gbuf, 0, gp, PS20_SIZE);
            int grc = _getPstates20(_gpu, gp);
            if (grc != 0) return grc;
            Marshal.Copy(gp, gbuf, 0, PS20_SIZE);
        }
        finally { Marshal.FreeHGlobal(gp); }

        uint numPstates = BinaryPrimitives.ReadUInt32LittleEndian(gspan.Slice(8));
        uint numClocks = BinaryPrimitives.ReadUInt32LittleEndian(gspan.Slice(12));
        if (numPstates == 0 || numClocks == 0) return -2;

        bool found = false;
        for (int i = 0; i < numPstates && i < 16 && !found; i++)
        {
            int pbase = 20 + i * PS_STRIDE;
            if (BinaryPrimitives.ReadUInt32LittleEndian(gspan.Slice(pbase)) != 0) continue; // 只認 P0
            for (int j = 0; j < numClocks && j < 8; j++)
            {
                int cbase = pbase + 8 + j * CLK_STRIDE;
                if (BinaryPrimitives.ReadUInt32LittleEndian(gspan.Slice(cbase)) != domain) continue;
                typeId = BinaryPrimitives.ReadUInt32LittleEndian(gspan.Slice(cbase + 4));
                min = BinaryPrimitives.ReadInt32LittleEndian(gspan.Slice(cbase + 16));
                max = BinaryPrimitives.ReadInt32LittleEndian(gspan.Slice(cbase + 20));
                found = true;
                break;
            }
        }
        if (!found) return -3;   // 找不到相符網域
        if (max != 0 || min != 0) deltaKHz = Math.Clamp(deltaKHz, min, max);

        // 以最小結構寫入：只描述 P0 該網域一筆 clock 的 freqDelta
        var sbuf = new byte[PS20_SIZE];
        var sspan = sbuf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(sspan, MakeVersion(PS20_SIZE, 2)); // [0] version
        BinaryPrimitives.WriteUInt32LittleEndian(sspan.Slice(8), 1);   // numPstates = 1
        BinaryPrimitives.WriteUInt32LittleEndian(sspan.Slice(12), 1);  // numClocks  = 1
        BinaryPrimitives.WriteUInt32LittleEndian(sspan.Slice(20), 0);  // pstates[0].pstateId = P0
        int c0 = 20 + 8;                                               // pstates[0].clocks[0]
        BinaryPrimitives.WriteUInt32LittleEndian(sspan.Slice(c0), domain);       // +0 domainId
        BinaryPrimitives.WriteUInt32LittleEndian(sspan.Slice(c0 + 4), typeId);   // +4 typeId
        BinaryPrimitives.WriteInt32LittleEndian(sspan.Slice(c0 + 12), deltaKHz); // +12 freqDelta.value

        IntPtr sp = Marshal.AllocHGlobal(PS20_SIZE);
        try
        {
            Marshal.Copy(sbuf, 0, sp, PS20_SIZE);
            return _setPstates20(_gpu, sp);
        }
        finally { Marshal.FreeHGlobal(sp); }
    }

    /// <summary>讀回指定網域 P0 目前的頻率偏移（kHz）；失敗回 null。</summary>
    public static int? GetClockOffset(uint domain)
    {
        if (!Available || _getPstates20 is null) return null;
        var buf = new byte[PS20_SIZE];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, MakeVersion(PS20_SIZE, 2));
        IntPtr p = Marshal.AllocHGlobal(PS20_SIZE);
        try
        {
            Marshal.Copy(buf, 0, p, PS20_SIZE);
            if (_getPstates20(_gpu, p) != 0) return null;
            Marshal.Copy(p, buf, 0, PS20_SIZE);
            uint numPstates = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
            uint numClocks = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
            for (int i = 0; i < numPstates && i < 16; i++)
            {
                int pbase = 20 + i * PS_STRIDE;
                if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pbase)) != 0) continue;
                for (int j = 0; j < numClocks && j < 8; j++)
                {
                    int cbase = pbase + 8 + j * CLK_STRIDE;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cbase)) == domain)
                        return BinaryPrimitives.ReadInt32LittleEndian(span.Slice(cbase + 12));
                }
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>讀回指定網域 P0 的合法頻率偏移範圍（kHz）；失敗或範圍為 0 回 null。</summary>
    public static (int min, int max)? GetClockOffsetRange(uint domain)
    {
        if (!Available || _getPstates20 is null) return null;
        var buf = new byte[PS20_SIZE];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, MakeVersion(PS20_SIZE, 2));
        IntPtr p = Marshal.AllocHGlobal(PS20_SIZE);
        try
        {
            Marshal.Copy(buf, 0, p, PS20_SIZE);
            if (_getPstates20(_gpu, p) != 0) return null;
            Marshal.Copy(p, buf, 0, PS20_SIZE);
            uint numPstates = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
            uint numClocks = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
            for (int i = 0; i < numPstates && i < 16; i++)
            {
                int pbase = 20 + i * PS_STRIDE;
                if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pbase)) != 0) continue;
                for (int j = 0; j < numClocks && j < 8; j++)
                {
                    int cbase = pbase + 8 + j * CLK_STRIDE;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cbase)) != domain) continue;
                    int min = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(cbase + 16));
                    int max = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(cbase + 20));
                    return (min == 0 && max == 0) ? null : (min, max);
                }
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(p); }
    }
}
