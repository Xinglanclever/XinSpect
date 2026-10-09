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
                                       MethodInfo? ReadIoPort, MethodInfo? WriteIoPort,
                                       MethodInfo? WritePciConfig,
                                       MethodInfo? ReadMemoryArray = null);

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

    /// <summary>內嵌資源的邏輯名（XinSpect.csproj 的 EmbedLhm094 目標）。單檔發佈的使用者端沒有 NuGet 快取，這是唯一保證存在的來源。</summary>
    private const string Embedded094 = "XinSpect.embedded.LibreHardwareMonitorLib-0.9.4.dll";

    /// <summary>
    /// 取得 LHM 0.9.4 組件：<b>先內嵌資源，再退回開發機的 NuGet 快取</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 內嵌是必要條件：發佈出去的是單一執行檔，使用者端不會有 NuGet 全域快取裡的 0.9.4。
    /// 先前只讀快取——而且其中一條候選路徑寫死了特定使用者名稱——等於「只有那台開發機能載入驅動」：
    /// 其他機器上 MSR／PCI 設定空間／I/O 埠／MMIO 全數變成讀不到，而 <see cref="DriverReadyService"/>
    /// 卻宣稱來源是「內嵌的 0.9.4」。快取那條保留給開發機（內嵌一時失效時還跑得起來），
    /// 但路徑一律由環境推導，不再假設使用者名稱。
    /// </para>
    /// </remarks>
    private static Assembly? Load094Assembly(out string error)
    {
        error = "";

        // 1) 內嵌資源（正式路徑）
        var stream = typeof(WinRing0Bridge).Assembly.GetManifestResourceStream(Embedded094);
        if (stream is not null)
        {
            try
            {
                // 先讀進記憶體再載入。記憶體資料流刻意不釋放：組件映像留著比省這 0.7 MB 重要，
                // 而載入失敗時它會被 GC 收走，沒有洩漏問題。
                var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                var alc = new AssemblyLoadContext("XinSpect-LHM094", isCollectible: false);
                alc.Resolving += (c, name) => ResolveDependency(c, name, []);
                return alc.LoadFromStream(ms);
            }
            catch (Exception ex)
            {
                error = "內嵌 LHM 0.9.4 載入失敗：" + ex.Message;
            }
        }
        else
        {
            error = "組件內沒有內嵌 LHM 0.9.4（資源名 " + Embedded094 + "）。";
        }

        // 2) 開發機的 NuGet 全域快取（路徑由環境推導）
        var dll = NuGetCacheCandidates().FirstOrDefault(File.Exists);
        if (dll is null) return null;      // error 已帶內嵌那條的原因
        try
        {
            var dir = Path.GetDirectoryName(dll)!;
            var alc = new AssemblyLoadContext("XinSpect-LHM094-disk", isCollectible: false);
            alc.Resolving += (c, name) => ResolveDependency(c, name, [dir]);
            error = "";
            return alc.LoadFromAssemblyPath(dll);
        }
        catch (Exception ex)
        {
            error = error + " 磁碟快取裡的 LHM 0.9.4 也載入失敗：" + ex.Message;
            return null;
        }
    }

    /// <summary>NuGet 全域快取裡的 0.9.4 組件候選路徑（由環境推導，不寫死使用者名稱）。</summary>
    private static IEnumerable<string> NuGetCacheCandidates()
    {
        var rel = Path.Combine("librehardwaremonitorlib", "0.9.4", "lib", "netstandard2.0", "LibreHardwareMonitorLib.dll");
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(root)) yield return Path.Combine(root!, rel);
        }
    }

    /// <summary>
    /// 隔離 ALC 的相依解析：先看指定目錄（磁碟載入時的同層），再交給預設 ALC
    /// （0.9.6 的相依已在行程裡：HidSharp、System.Management 等）。
    /// </summary>
    private static Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName name, string[] probeDirs)
    {
        foreach (var dir in probeDirs)
        {
            var candidate = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
        }
        try { return AssemblyLoadContext.Default.LoadFromAssemblyName(name); }
        catch { return null; }
    }


    /// <summary>把 LHM 0.9.4 載入隔離 ALC 並反射出四個必需方法與四個選用方法；失敗回 null 並填入原因。</summary>
    private static Ring0Methods? Load(out string error)
    {
        // 取得 0.9.4 組件：先內嵌資源（正式路徑）、再退回開發機的 NuGet 快取。
        // 0.9.4 的 Ring0 以內嵌資源帶著 gzip 過的 WinRing0x64.sys，所以「拿得到組件」＝「拿得到驅動」。
        var asm = Load094Assembly(out error);
        if (asm is null) return null;
        try
        {
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
            // 選用：寫 PCI 設定空間。HEDT／伺服器平台的 DIMM SPD 掛在處理器記憶體控制器自己的
            // SMBus 上，而那個控制器的命令暫存器就在 PCI 設定空間裡——發一次讀取也得先寫它。
            var writePci = ty.GetMethod("WritePciConfig", All, new[] { typeof(uint), typeof(uint), typeof(uint) });
            // 選用：實體記憶體讀取（MMIO 的地基）。0.9.4 的 Ring0 提供兩個泛型多載：
            // ReadMemory<T>(UInt64, ref T) 與 ReadMemory<T>(UInt64, ref T[])——驅動端以
            // {位址, UnitSize, Count} 讀回 Count 個元素，位址是 64 位元。這裡精確挑「陣列版」
            // 泛型定義（第二參數是 ByRef 且元素是陣列型別），比對不上就當作沒有——
            // 猜錯簽名的代價是把失敗當成功，把垃圾當 MMIO 內容解讀出去。
            var readMemArray = ty.GetMethods(All).FirstOrDefault(m => m.Name == "ReadMemory"
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(ulong)
                && m.GetParameters()[1].ParameterType.IsByRef
                && m.GetParameters()[1].ParameterType.GetElementType() is { IsArray: true });
            return new Ring0Methods(open, close, read, write, readPci, pciAddr, readIo, writeIo, writePci, readMemArray);
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

    /// <summary>寫入稽核的呼叫者名：沿堆疊往上找第一個不是橋接／WriteGate 的型別。寫入罕見，開銷可接受。</summary>
    private static string CallerOfWrite()
    {
        foreach (var sf in new System.Diagnostics.StackTrace(true).GetFrames())
        {
            var name = sf.GetMethod()?.DeclaringType?.Name;
            if (name is not null && name != nameof(WinRing0Bridge) && name != nameof(WriteGate))
                return name;
        }
        return "（呼叫者未知）";
    }

    /// <summary>寫 MSR（eax＝低 32 位、edx＝高 32 位）。失敗回 false。每次呼叫都進 WriteGate 帳本（§5.3 寫入稽核）。</summary>
    public bool WriteMsrPair(uint index, uint eax, uint edx)
    {
        var args = new object?[] { index, eax, edx };
        if (_m is null || _disposed) return false;
        bool ok;
        try { ok = _m.WriteMsr.Invoke(null, args) is true; }
        catch { ok = false; }
        WriteGate.Record($"MSR 0x{index:X}", CallerOfWrite(), $"EAX=0x{eax:X8} EDX=0x{edx:X8}", ok);
        return ok;
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
        bool ok;
        try { _m.WriteIoPort.Invoke(null, new object?[] { port, value }); ok = true; }
        catch { ok = false; }
        WriteGate.Record($"I/O 0x{port:X}", CallerOfWrite(), $"out byte 0x{value:X2}", ok);
        return ok;
    }

    /// <summary>本機的 Ring0 是否提供 PCI 設定空間<b>寫入</b>。</summary>
    public bool PciWriteAvailable
        => _m?.WritePciConfig is not null && _m.GetPciAddress is not null && !_disposed;

    /// <summary>
    /// 寫 PCI 設定空間的一個 DWORD。不支援或失敗回 false。
    /// </summary>
    /// <remarks>
    /// <b>這是本橋接最危險的一個方法。</b>PCI 設定空間裡有一大堆一寫就會讓機器當場停住的東西
    /// （記憶體控制器時序、電源管理、位址解碼）。它存在的唯一理由是處理器 iMC SMBus 的命令
    /// 暫存器就在設定空間裡，而發起一次 SPD <i>讀取</i>也必須寫那一格。
    /// <para>
    /// 防線不在這裡：<c>ImcSmbusController</c> 只寫自己探測到的那三個暫存器位移，
    /// 而且裝置位址仍受 <see cref="SpdBusAddresses"/> 白名單約束。
    /// </para>
    /// </remarks>
    public bool WritePciConfig(byte bus, byte device, byte function, uint register, uint value)
    {
        if (_m?.WritePciConfig is null || _m.GetPciAddress is null || _disposed) return false;
        bool ok = false;
        try
        {
            if (_m.GetPciAddress.Invoke(null, new object?[] { bus, device, function }) is uint addr)
                ok = _m.WritePciConfig.Invoke(null, new object?[] { addr, register, value }) is true;
        }
        catch { ok = false; }
        WriteGate.Record($"PCI {bus}:{device:X2}.{function}+0x{register:X}", CallerOfWrite(),
            $"dword 0x{value:X8}", ok);
        return ok;
    }

    /// <summary>本機的 Ring0 是否提供實體記憶體讀取（MMIO 的地基；V7 驅動裁決裡 WinRing0 主力路徑的關鍵能力）。</summary>
    public bool MemoryReadAvailable => _m?.ReadMemoryArray is not null && !_disposed;

    /// <summary>
    /// 讀實體位址起 <paramref name="length"/> 位元組。失敗或不支援回 null。
    /// </summary>
    /// <remarks>
    /// <para>底層是 Ring0.ReadMemory&lt;byte&gt;(位址, ref byte[length])——驅動端以
    /// {位址, UnitSize=1, Count=length} 送 IOCTL_OLS_READ_MEMORY，驅動負責映射與複製，
    /// 位址支援 64 位元。回 false 代表驅動端映射或複製失敗，<b>呼叫方必須標三態</b>，
    /// 不得以全 0／全 0xFF 頂替。</para>
    /// <para>⚠ 能力面：這條路徑<b>沒有位址白名單</b>（與 XsRegProbe 的差別就在這）。
    /// 防線在呼叫方：各服務的位址一律來自 PCI BAR／MCFG／MSR 對帳的真實來源，不自造位址、
    /// 不做任意掃描。位址正確但該範圍未映射時驅動會回失敗——那也是誠實的讀不到。</para>
    /// </remarks>
    public byte[]? ReadMemoryBlock(ulong address, int length)
    {
        if (_m?.ReadMemoryArray is null || _disposed || length <= 0) return null;
        try
        {
            var args = new object?[] { address, new byte[length] };
            if (_m.ReadMemoryArray.MakeGenericMethod(typeof(byte)).Invoke(null, args) is not true) return null;
            return (byte[])args[1]!;
        }
        catch { return null; }
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
