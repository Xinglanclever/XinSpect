using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace XinSpect;

/// <summary>
/// WinRing0 橋接：以<b>隔離 AssemblyLoadContext</b> 載入 LHM 0.9.4 的 LibreHardwareMonitorLib
/// （內嵌簽章驅動 WinRing0x64.sys 提供 MSR 讀寫），與本體使用的 0.9.6（已無 Ring0）並存不衝突。
/// 以反射呼叫其 internal Hardware.Ring0 的 Open／ReadMsr／WriteMsr（值以 EAX/EDX 分離傳遞）。
/// </summary>
/// <remarks>
/// ⚠ 風險聲明（使用者已同意啟用）：WinRing0 是 AV 常標記的舊驅動，介面無權限區分——
/// 驅動載入後到重開機前，同機其他程序理論上也能透過它存取 MSR。用途限 RDT 的
/// PQR_ASSOC／QM_EVTSEL 寫入與計數讀取；驅動本身到重開機才卸載。
/// </remarks>
public sealed class WinRing0Bridge : IDisposable
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private readonly AssemblyLoadContext? _context;
    private readonly Type? _ring0Type;
    private readonly MethodInfo? _open;
    private readonly MethodInfo? _close;
    private readonly MethodInfo? _readMsr;    // bool ReadMsr(uint index, out uint eax, out uint edx)
    private readonly MethodInfo? _writeMsr;   // bool WriteMsr(uint index, uint eax, uint edx)

    public bool Available { get; }
    public string Error { get; }

    private WinRing0Bridge(AssemblyLoadContext? context, Type? ring0Type, string error = "")
    {
        _context = context;
        _ring0Type = ring0Type;
        Error = error;
        if (ring0Type is null) return;
        var ty = ring0Type;
        _open = ty.GetMethod("Open", All, Type.EmptyTypes);
        _close = ty.GetMethod("Close", All, Type.EmptyTypes);
        _readMsr = ty.GetMethod("ReadMsr", All, new[] { typeof(uint), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType() });
        _writeMsr = ty.GetMethod("WriteMsr", All, new[] { typeof(uint), typeof(uint), typeof(uint) });
        Available = _open is not null && _readMsr is not null && _writeMsr is not null;
        if (!Available) Error = "Ring0 缺少 Open／ReadMsr／WriteMsr（版本不符）。";
    }

    public static WinRing0Bridge CreateFailed(string error) => new(null, null, error);

    /// <summary>從 NuGet 快取載入 LHM 0.9.4 並開啟驅動。失敗時回傳 Available=false、Error 帶原因。</summary>
    public static WinRing0Bridge Create()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(userProfile, ".nuget", "packages", "librehardwaremonitorlib", "0.9.4", "lib", "netstandard2.0", "LibreHardwareMonitorLib.dll"),
            Path.Combine(@"C:\Users\Administrator\.nuget\packages\librehardwaremonitorlib\0.9.4\lib\netstandard2.0", "LibreHardwareMonitorLib.dll"),
        };
        var dll = candidates.FirstOrDefault(File.Exists);
        if (dll is null)
            return CreateFailed("找不到 LHM 0.9.4 套件（WinRing0 來源）。");

        var context = new AssemblyLoadContext("XinSpect-LHM094", isCollectible: false);
        context.Resolving += (alc, name) =>
        {
            var p = Path.Combine(Path.GetDirectoryName(dll)!, name.Name + ".dll");
            return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null;
        };
        try
        {
            var asm = context.LoadFromAssemblyPath(dll);
            var ring0Type = asm.GetType("LibreHardwareMonitor.Hardware.Ring0")
                ?? throw new InvalidOperationException("找不到 Hardware.Ring0 類別");
            var bridge = new WinRing0Bridge(context, ring0Type);
            if (!bridge.Available)
            {
                context.Unload();
                return CreateFailed(bridge.Error);
            }
            bridge._open!.Invoke(null, null);   // 內部會 Extract＋建服務＋啟動
            return bridge;
        }
        catch (Exception ex)
        {
            try { context.Unload(); } catch { }
            return CreateFailed("驅動開啟失敗：" + ex.Message);
        }
    }

    /// <summary>讀 MSR（回 EAX 低 32 位與 EDX 高 32 位）。失敗回 false。</summary>
    public bool ReadMsrPair(uint index, out uint eax, out uint edx)
    {
        var args = new object?[] { index, 0u, 0u };
        eax = edx = 0;
        try
        {
            if (_readMsr!.Invoke(null, args) is not true) return false;
            eax = (uint)args[1]!;
            edx = (uint)args[2]!;
            return true;
        }
        catch { return false; }
    }

    /// <summary>讀 MSR：回 64 位組合值；失敗回 null。</summary>
    public ulong? ReadMsrPair64(uint index)
    {
        var args = new object?[] { index, 0u, 0u };
        try
        {
            if (_readMsr!.Invoke(null, args) is not true) return null;
            return (uint)args[1]! | ((ulong)(uint)args[2]! << 32);
        }
        catch { return null; }
    }

    /// <summary>寫 MSR（eax＝低 32 位、edx＝高 32 位）。失敗回 false。</summary>
    public bool WriteMsrPair(uint index, uint eax, uint edx)
    {
        var args = new object?[] { index, eax, edx };
        try { return _writeMsr!.Invoke(null, args) is true; }
        catch { return false; }
    }

    public void Dispose()
    {
        try { _close?.Invoke(null, null); } catch { }
    }
}
