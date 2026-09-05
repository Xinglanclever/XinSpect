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
/// <para>
/// ⚠ 風險聲明（使用者已同意啟用）：WinRing0 是 AV 常標記的舊驅動，介面無權限區分——
/// 驅動載入後到重開機前，同機其他程序理論上也能透過它存取 MSR。用途限 RDT 的
/// PQR_ASSOC／QM_EVTSEL 寫入與計數讀取；驅動本身到重開機才卸載。
/// </para>
/// <para>
/// <b>全程序共用一個會話（引用計數）。</b>Ring0 的 Open／Close 操作的是同一個<i>具名核心服務</i>，
/// 而 Ring0 的狀態是靜態的——兩份會話同時存在時，先做完的那個 Dispose 會把還在讀的那個的驅動關掉。
/// 這在實機上就是「黏滯位元讀一個 MSR 幾毫秒就回來，MCA 要逐核逐銀行掃好幾秒」這種組合：
/// 快的把驅動收掉，慢的後半段全部讀失敗，畫面顯示「無法讀取」。所以這裡改成載入一次、
/// 引用計數歸零才真正 Close；反射快取永久保留（隔離用的 ALC 不可回收，重載只會多疊一份）。
/// </para>
/// </remarks>
public sealed class WinRing0Bridge : IDisposable
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>反射快取：載入成功後永久保留，之後每次 <see cref="Create"/> 只做 Open／計數。</summary>
    /// <remarks>
    /// PCI 設定空間與 I/O 埠的方法都是<b>選用</b>的（宣告為可為 null）：找不到 PCI 那兩個只影響
    /// 「PCIe 鏈路」一頁，找不到 I/O 埠那兩個只影響 SMBus／SPD 直讀，
    /// 不該讓所有靠 MSR 的頁面一起失效。
    /// </remarks>
    private sealed record Ring0Methods(MethodInfo Open, MethodInfo Close, MethodInfo ReadMsr, MethodInfo WriteMsr,
                                       MethodInfo? ReadPciConfig, MethodInfo? GetPciAddress,
                                       MethodInfo? ReadIoPort, MethodInfo? WriteIoPort);

    private static readonly object Gate = new();
    private static Ring0Methods? _cached;
    private static string _cachedError = "";
    private static int _refs;

    private readonly Ring0Methods? _m;
    private bool _disposed;

    public bool Available { get; }
    public string Error { get; }

    private WinRing0Bridge(Ring0Methods? methods, string error = "")
    {
        _m = methods;
        Error = error;
        Available = methods is not null;
    }

    public static WinRing0Bridge CreateFailed(string error) => new(null, error);

    /// <summary>
    /// 取得一份 MSR 存取權（第一位使用者才真正載入 LHM 0.9.4 並開啟驅動）。
    /// 失敗時回傳 Available=false、Error 帶原因。用完務必 <see cref="Dispose"/>。
    /// </summary>
    public static WinRing0Bridge Create()
    {
        lock (Gate)
        {
            if (_cached is null)
            {
                _cached = Load(out _cachedError);
                if (_cached is null) return CreateFailed(_cachedError);
            }

            if (_refs == 0)
            {
                // 內部會 Extract＋建服務＋啟動；已經開著時重複 Open 是多餘的，所以只在第一位使用者做
                try { _cached.Open.Invoke(null, null); }
                catch (Exception ex) { return CreateFailed("驅動開啟失敗：" + ex.Message); }
            }
            _refs++;
            return new WinRing0Bridge(_cached);
        }
    }

    /// <summary>把 LHM 0.9.4 載入隔離 ALC 並反射出四個必需方法與四個選用方法；失敗回 null 並填入原因。</summary>
    private static Ring0Methods? Load(out string error)
    {
        error = "";
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(userProfile, ".nuget", "packages", "librehardwaremonitorlib", "0.9.4", "lib", "netstandard2.0", "LibreHardwareMonitorLib.dll"),
            Path.Combine(@"C:\Users\Administrator\.nuget\packages\librehardwaremonitorlib\0.9.4\lib\netstandard2.0", "LibreHardwareMonitorLib.dll"),
        };
        var dll = candidates.FirstOrDefault(File.Exists);
        if (dll is null)
        {
            error = "找不到 LHM 0.9.4 套件（WinRing0 來源）。";
            return null;
        }

        var context = new AssemblyLoadContext("XinSpect-LHM094", isCollectible: false);
        context.Resolving += (alc, name) =>
        {
            var p = Path.Combine(Path.GetDirectoryName(dll)!, name.Name + ".dll");
            return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null;
        };
        try
        {
            var asm = context.LoadFromAssemblyPath(dll);
            var ty = asm.GetType("LibreHardwareMonitor.Hardware.Ring0")
                ?? throw new InvalidOperationException("找不到 Hardware.Ring0 類別");
            var open = ty.GetMethod("Open", All, Type.EmptyTypes);
            var close = ty.GetMethod("Close", All, Type.EmptyTypes);
            var read = ty.GetMethod("ReadMsr", All, new[] { typeof(uint), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType() });
            var write = ty.GetMethod("WriteMsr", All, new[] { typeof(uint), typeof(uint), typeof(uint) });
            if (open is null || close is null || read is null || write is null)
            {
                error = "Ring0 缺少 Open／ReadMsr／WriteMsr（版本不符）。";
                return null;
            }
            // 選用：PCI 設定空間（0xCF8／0xCFC）——「PCIe 鏈路」一頁靠它，缺了不影響 MSR 各頁
            var readPci = ty.GetMethod("ReadPciConfig", All, new[] { typeof(uint), typeof(uint), typeof(uint).MakeByRefType() });
            var pciAddr = ty.GetMethod("GetPciAddress", All, new[] { typeof(byte), typeof(byte), typeof(byte) });
            // 選用：I/O 埠 in／out——SMBus 控制器（因而 SPD 直讀）靠它，缺了同樣只影響那一條路徑。
            // 簽章在 0.9.4 上實測為 Byte ReadIoPort(UInt32) 與 Void WriteIoPort(UInt32, Byte)；
            // 這裡精確比對參數形狀，比對不上就當作沒有——不用別的多載硬套，
            // 因為猜錯的代價是把垃圾位元組當成 SPD 內容解讀出去。
            var readIo = ty.GetMethod("ReadIoPort", All, new[] { typeof(uint) });
            var writeIo = ty.GetMethod("WriteIoPort", All, new[] { typeof(uint), typeof(byte) });
            if (readIo is not null && readIo.ReturnType != typeof(byte)) readIo = null;
            return new Ring0Methods(open, close, read, write, readPci, pciAddr, readIo, writeIo);
        }
        catch (Exception ex)
        {
            error = "載入 WinRing0 失敗：" + ex.Message;
            return null;
        }
    }

    /// <summary>讀 MSR（回 EAX 低 32 位與 EDX 高 32 位）。失敗回 false。</summary>
    public bool ReadMsrPair(uint index, out uint eax, out uint edx)
    {
        var args = new object?[] { index, 0u, 0u };
        eax = edx = 0;
        if (_m is null || _disposed) return false;
        try
        {
            if (_m.ReadMsr.Invoke(null, args) is not true) return false;
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
        if (_m is null || _disposed) return null;
        try
        {
            if (_m.ReadMsr.Invoke(null, args) is not true) return null;
            return (uint)args[1]! | ((ulong)(uint)args[2]! << 32);
        }
        catch { return null; }
    }

    /// <summary>寫 MSR（eax＝低 32 位、edx＝高 32 位）。失敗回 false。</summary>
    public bool WriteMsrPair(uint index, uint eax, uint edx)
    {
        var args = new object?[] { index, eax, edx };
        if (_m is null || _disposed) return false;
        try { return _m.WriteMsr.Invoke(null, args) is true; }
        catch { return false; }
    }

    /// <summary>本機的 Ring0 是否提供 PCI 設定空間讀取（LHM 0.9.4 有；缺了就只是這一頁不能用）。</summary>
    public bool PciAvailable => _m?.ReadPciConfig is not null && _m.GetPciAddress is not null && !_disposed;

    /// <summary>
    /// 讀 PCI 設定空間的一個 DWORD（bus／device／function ＋ 暫存器位移，位移須 4 位元組對齊）。
    /// 失敗或不支援回 null——<b>0xFFFFFFFF 代表該功能不存在</b>，這裡照實回傳，由呼叫方判斷。
    /// </summary>
    public uint? ReadPciConfig(byte bus, byte device, byte function, uint register)
    {
        if (_m?.ReadPciConfig is null || _m.GetPciAddress is null || _disposed) return null;
        try
        {
            if (_m.GetPciAddress.Invoke(null, new object?[] { bus, device, function }) is not uint addr) return null;
            var args = new object?[] { addr, register, 0u };
            if (_m.ReadPciConfig.Invoke(null, args) is not true) return null;
            return (uint)args[2]!;
        }
        catch { return null; }
    }

    /// <summary>本機的 Ring0 是否提供 I/O 埠 in／out（SMBus 控制器與 SPD 直讀的唯一入口）。</summary>
    /// <remarks>
    /// 回 false 時呼叫端必須顯示「讀不到（原因）」，<b>不得代之以 0 或 0xFF</b>——
    /// SPD 全 0 會被解讀成「製造於 2000 年第 0 週」，全 0xFF 會變成一堆看似合理的極大值。
    /// </remarks>
    public bool IoPortAvailable => _m?.ReadIoPort is not null && _m.WriteIoPort is not null && !_disposed;

    /// <summary>讀一個 I/O 埠位元組（in）。不支援或失敗回 null。</summary>
    public byte? ReadIoPortByte(uint port)
    {
        if (_m?.ReadIoPort is null || _disposed) return null;
        try { return _m.ReadIoPort.Invoke(null, new object?[] { port }) as byte?; }
        catch { return null; }
    }

    /// <summary>
    /// 寫一個 I/O 埠位元組（out）。不支援或失敗回 false。
    /// </summary>
    /// <remarks>
    /// <b>這是本橋接唯一會改變機器狀態的 I/O 埠操作，呼叫者受嚴格限制。</b>
    /// SMBus 交易在協定上必須寫入控制器的命令／位址／控制暫存器才能發起一次「讀取」，
    /// 所以這個方法不可避免；防線因此不在這裡，而在 <c>SmbusController</c> 的裝置位址白名單
    /// ——只允許 SPD EEPROM 讀取與 DDR4 切頁，寫入保護指令連程式碼路徑都不存在。
    /// </remarks>
    public bool WriteIoPortByte(uint port, byte value)
    {
        if (_m?.WriteIoPort is null || _disposed) return false;
        try { _m.WriteIoPort.Invoke(null, new object?[] { port, value }); return true; }
        catch { return false; }
    }

    /// <summary>交還這一份會話；最後一位使用者離開時才真正 Close 驅動服務。</summary>
    public void Dispose()
    {
        if (_m is null) return;                 // 失敗的橋接沒有計數，也沒有東西要關
        lock (Gate)
        {
            if (_disposed) return;              // 重複 Dispose 不能把別人的計數扣掉
            _disposed = true;
            if (--_refs > 0) return;            // 還有人在讀，驅動留著
            try { _m.Close.Invoke(null, null); } catch { }
        }
    }
}
