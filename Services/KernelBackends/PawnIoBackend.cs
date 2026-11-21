using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LibreHardwareMonitor.Hardware;

namespace XinSpect;

/// <summary>Read-only Intel MSR access through an existing PawnIO installation.</summary>
public sealed class PawnIoBackend : IKernelBackend
{
    internal const uint ProbeMsr = 0xCE; // MSR_PLATFORM_INFO; maximum non-turbo ratio must be nonzero.
    private const string ModuleResource = "LibreHardwareMonitor.Resources.PawnIo.IntelMSR.bin";
    private const string ModuleSha256 = "ED41A0D0DE082F4668A9CACCB9681EBBAD97A8ED3452C41918DDA1EB454B77A9";
    private readonly object _gate = new();
    private IMsrSession? _session;

    internal interface IMsrSession : IDisposable
    {
        ulong? Read(uint index);
    }

    public string Id => "pawnio";
    public string DisplayName => "PawnIO（沙箱）";
    public KernelTrustTier Tier => KernelTrustTier.Sandboxed;
    public string? UnavailableReason { get; }
    public KernelCapability Capabilities
    {
        get { lock (_gate) return _session is null ? KernelCapability.None : KernelCapability.MsrRead; }
    }

    private PawnIoBackend(IMsrSession? session, string? reason) => (_session, UnavailableReason) = (session, reason);

    internal static PawnIoBackend Create() => Create(
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 && IsIntel(),
        OpenInstalledSession);

    private static bool IsIntel()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return false;
        var (_, b, c, d) = System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0);
        return b == 0x756E6547 && d == 0x49656E69 && c == 0x6C65746E;
    }

    internal static PawnIoBackend Create(bool supported, Func<(IMsrSession? Session, string? Error)> open)
    {
        if (!supported) return new(null, "PawnIO 共用路由目前只支援 Windows x64 Intel CPU。");
        var (session, error) = open();
        if (session is null) return new(null, error ?? "PawnIO 無法開啟。");
        bool accepted = false;
        try
        {
            if (session.Read(ProbeMsr) is not ulong value || !IsValidPlatformInfo(value))
                return new(null, "PawnIO 的 PLATFORM_INFO 試讀失敗或倍頻為零；未啟用共用 MSR 路由。");
            accepted = true;
            return new(session, null);
        }
        finally
        {
            if (!accepted) session.Dispose();
        }
    }

    private static bool IsValidPlatformInfo(ulong value) => ((value >> 8) & 0xFF) is > 0 and < 128;

    public ulong? ReadMsr(uint index)
    {
        // Only these telemetry registers are exposed; never dispatch arbitrary module functions.
        if (index is not (ProbeMsr or 0x198 or 0x19C or 0x1A2 or 0x606 or 0x611)) return null;
        lock (_gate)
        {
            var value = _session?.Read(index);
            return index == ProbeMsr && value is ulong raw && !IsValidPlatformInfo(raw) ? null : value;
        }
    }

    public KernelWriteOutcome WriteMsr(uint index, ulong value, string caller) => ReadOnly();
    public uint? ReadPciConfig(byte bus, byte device, byte function, uint register) => null;
    public KernelWriteOutcome WritePciConfig(byte bus, byte device, byte function, uint register, uint value, string caller) => ReadOnly();
    public byte? ReadIoPortByte(uint port) => null;
    public KernelWriteOutcome WriteIoPortByte(uint port, byte value, string caller) => ReadOnly();
    public byte[]? ReadMemoryBlock(ulong address, int length) => null;
    private static KernelWriteOutcome ReadOnly() => new(false, "PawnIO 共用路由只支援白名單 MSR 讀取。");

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private static (IMsrSession? Session, string? Error) OpenInstalledSession()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) return (null, "無法取得 Program Files 路徑。");
        string path = Path.Combine(programFiles, "PawnIO", "PawnIOLib.dll");
        if (!File.Exists(path)) return (null, "找不到已安裝的 PawnIOLib.dll。");
        using var module = typeof(Computer).Assembly.GetManifestResourceStream(ModuleResource);
        if (module is null) return (null, "找不到 LibreHardwareMonitor 的 IntelMSR 模組。");
        using var buffer = new MemoryStream();
        module.CopyTo(buffer);
        byte[] blob = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(blob)) != ModuleSha256)
            return (null, "IntelMSR 模組 SHA-256 不符；未載入。");

        nint library = 0, handle = 0;
        Close? close = null;
        try
        {
            library = NativeLibrary.Load(path, typeof(PawnIoBackend).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            var open = Bind<Open>(library, "pawnio_open");
            var load = Bind<Load>(library, "pawnio_load");
            var execute = Bind<Execute>(library, "pawnio_execute");
            close = Bind<Close>(library, "pawnio_close");
            int hr = open(out handle);
            if (hr < 0 || handle == 0 || handle == -1)
                return (null, $"PawnIO 服務無法開啟（HRESULT 0x{hr:X8}）。");
            hr = load(handle, blob, (nuint)blob.Length);
            if (hr < 0) return (null, $"PawnIO 無法載入 IntelMSR 模組（HRESULT 0x{hr:X8}）。");
            var session = new NativeSession(library, handle, execute, close);
            library = handle = 0;
            return (session, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return (null, $"PawnIOLib 無法使用：{ex.Message}");
        }
        finally
        {
            if (handle != 0 && handle != -1) close?.Invoke(handle);
            if (library != 0) NativeLibrary.Free(library);
        }
    }

    private static T Bind<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Open(out nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Load(nint handle, byte[] blob, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Execute(nint handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inputCount, [Out] ulong[] output, nuint outputCount, out nuint written);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Close(nint handle);

    private sealed class NativeSession(nint library, nint handle, Execute execute, Close close) : IMsrSession
    {
        public ulong? Read(uint index)
        {
            ulong[] output = new ulong[1];
            int hr = execute(handle, "ioctl_read_msr", [index], 1, output, 1, out nuint written);
            return hr >= 0 && written == 1 ? output[0] : null;
        }

        public void Dispose()
        {
            try { close(handle); }
            finally { NativeLibrary.Free(library); }
        }
    }
}
