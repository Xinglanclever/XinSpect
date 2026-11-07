// ⚠ 未編譯——需作者機驗證（dotnet build 命令見 patch-25.md）
//
// DriverManager.cs — 完整替換檔（patch-25，P0-14 能力帳單 usermode 端）
// 覆蓋目標：BlueSquadron/DriverManager.cs（原 243 行）
// 配套：patch-25-Common.h（BS_DRIVER_CAPS 契約）、patch-24-Program.cs（"caps" IPC 指令）。
//
// 修復內容（對照原檔）：
//   原版 TryLoad 成功＝IsLoaded=true，之後一切如常——但驅動「載入成功」不等於
//   「四個回呼都註冊成功」。本版新增 QueryCaps()：DeviceIoControl(IOCTL_BS_GET_CAPS)
//   取回 BS_DRIVER_CAPS（Magic＋IoctlVersion 驗證不過就拒用並回 null），把
//   FeatureMask／六步 NTSTATUS／MSR 快照活體健康交給呼叫端（Program.cs 的 caps 指令
//   → 主程式 DriverReadyService 狀態卡）。「部分就緒」如實顯示。
//   其餘逐行同原檔（TryLoad／Unload／SCM 作業／P/Invoke 都不動）。

using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace BlueSquadron;

/// <summary>
/// 核心驅動管理器：載入/卸載 BlueSquadron.sys 並透過 DeviceIoControl 通訊。
/// Phase 1：嘗試載入，失敗則降級為純 user-mode 偵測。
/// Phase 2：完整 IOCTL 介面。
/// </summary>
internal sealed class DriverManager : IDisposable
{
    private const string ServiceName = "BlueSquadron";
    private const string DevicePath = @"\\.\BlueSquadron";
    private const string DriverFileName = "BlueSquadron.sys";

    // IOCTL_BS_GET_CAPS = CTL_CODE(0x8337, 0x807, METHOD_BUFFERED, FILE_READ_DATA)
    // = (0x8337 << 16) | (1 << 14) | (0x807 << 2) | 0
    private const uint IOCTL_BS_GET_CAPS = 0x8337401C;

    private nint _device = -1;
    public bool IsLoaded { get; private set; }
    public string LastError { get; private set; } = "";

    /// <summary>QueryCaps 失敗原因（回 null 時呼叫端讀這個——不讓「查不到」無聲）。</summary>
    public string CapsError { get; private set; } = "";

    /// <summary>嘗試載入核心驅動。回傳是否成功。</summary>
    public bool TryLoad()
    {
        // 1. Find the .sys file next to this exe
        string exeDir = AppContext.BaseDirectory;
        string sysPath = Path.Combine(exeDir, DriverFileName);
        if (!File.Exists(sysPath))
        {
            // Also check parent directory / Driver subdirectory
            sysPath = Path.Combine(exeDir, "Driver", DriverFileName);
            if (!File.Exists(sysPath))
            {
                LastError = $"找不到 {DriverFileName}";
                return false;
            }
        }

        // 2. 審計 #39：載入前驗證 .sys 是 PE 映像（MZ 標頭＋合理大小）——
        //    不該把一個隨機檔案或文字檔當核心驅動載入。
        try
        {
            var sysBytes = File.ReadAllBytes(sysPath);
            if (sysBytes.Length < 64 || sysBytes[0] != 0x4D || sysBytes[1] != 0x5A)
            {
                LastError = $"{sysPath} 不是有效的 PE 映像（缺 MZ 標頭）——可能被竄改或下載損毀。";
                return false;
            }
        }
        catch (Exception ex)
        {
            LastError = $"無法讀取驅動檔 {sysPath}：{ex.Message}";
            return false;
        }

        // 3. Try to create/start the service
        try
        {
            if (!ServiceExists())
                CreateService(sysPath);
            StartService();
        }
        catch (Exception ex)
        {
            LastError = $"服務啟動失敗：{ex.Message}";
            // Try opening the device anyway (might already be loaded)
        }

        // 3. Open the device
        try
        {
            _device = CreateFileW(
                DevicePath,
                0xC0000000, // GENERIC_READ | GENERIC_WRITE
                0,          // no sharing
                nint.Zero,
                3,          // OPEN_EXISTING
                0,
                nint.Zero);

            if (_device == -1 || _device == 0)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = $"無法開啟裝置 {DevicePath}（錯誤碼 {err}）";
                _device = -1;
                return false;
            }

            IsLoaded = true;
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    // ── P0-14：能力帳單 ──────────────────────────────────────────────────

    /// <summary>IOCTL_BS_GET_CAPS 的原生回覆版面（對 Common.h 的 BS_DRIVER_CAPS；x64＝48 位元組，12 欄 × 4）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BsDriverCapsNative
    {
        public uint Magic;
        public uint IoctlVersion;
        public uint FeatureMask;
        public int StatusLoadImage;
        public int StatusProcess;
        public int StatusRegistry;
        public int StatusObProtect;
        public int StatusMsrSnapshot;
        public int StatusHashProvider;
        public uint MsrBaseEntries;
        public uint MsrDiffCount;
        public uint ActiveCpus;
    }

    public const uint BsCapsMagic = 0x31435342U;   // BS_CAPS_MAGIC（'BSC1'）
    public const uint BsIoctlVersion = 2;          // BS_IOCTL_VERSION

    /// <summary>能力帳單（ 六步 NTSTATUS 為原始值；NT_SUCCESS ⇒ ≥ 0）。</summary>
    public sealed record BsDriverCaps(
        uint FeatureMask,
        int LoadImageStatus,
        int ProcessStatus,
        int RegistryStatus,
        int ObProtectStatus,
        int MsrSnapshotStatus,
        int HashProviderStatus,
        uint MsrBaseEntries,
        uint MsrDiffCount,
        uint ActiveCpus);

    /// <summary>
    /// 查詢驅動能力帳單。驅動未載入／IOCTL 失敗／Magic 或版本驗證不過 →
    /// 回 null 並設 CapsError（呼叫端三態標示，不猜）。
    /// </summary>
    public BsDriverCaps? QueryCaps()
    {
        if (_device is not (-1) and not 0)
        {
            int size = Marshal.SizeOf<BsDriverCapsNative>();
            nint ptr = Marshal.AllocHGlobal(size);
            try
            {
                if (DeviceIoControl(_device, IOCTL_BS_GET_CAPS, nint.Zero, 0,
                                    ptr, (uint)size, out uint ret, nint.Zero)
                    && ret == (uint)size)
                {
                    var n = Marshal.PtrToStructure<BsDriverCapsNative>(ptr);
                    if (n.Magic != BsCapsMagic || n.IoctlVersion != BsIoctlVersion)
                    {
                        CapsError = $"能力帳單版本不符：Magic=0x{n.Magic:X8} V={n.IoctlVersion}" +
                                    $"（本端預期 Magic=0x{BsCapsMagic:X8} V={BsIoctlVersion}）——驅動與 Bridge 版本不對應，拒用不猜。";
                        return null;
                    }
                    CapsError = "";
                    return new BsDriverCaps(
                        n.FeatureMask, n.StatusLoadImage, n.StatusProcess, n.StatusRegistry,
                        n.StatusObProtect, n.StatusMsrSnapshot, n.StatusHashProvider,
                        n.MsrBaseEntries, n.MsrDiffCount, n.ActiveCpus);
                }
                int err = Marshal.GetLastWin32Error();
                CapsError = $"IOCTL_BS_GET_CAPS 失敗（錯誤碼 {err}）——驅動可能是舊版（無能力帳單契約）。";
                return null;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        CapsError = "驅動未載入（裝置未開啟）";
        return null;
    }

    /// <summary>卸載驅動並清理服務。</summary>
    public void Unload()
    {
        CloseDevice();
        try
        {
            StopService();
            DeleteService();
        }
        catch { }
        IsLoaded = false;
    }

    public void Dispose()
    {
        CloseDevice();
        // Don't unload service on dispose — leave it running for protection
    }

    private void CloseDevice()
    {
        if (_device is not (-1) and not 0)
        {
            CloseHandle(_device);
            _device = -1;
        }
    }

    // ── Service Control Manager operations ────────────────────────────────

    private static bool ServiceExists()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch { return false; }
    }

    private static void CreateService(string sysPath)
    {
        nint scm = OpenSCManagerW(null, null, 0x0002); // SC_MANAGER_CREATE_SERVICE
        if (scm == 0) throw new InvalidOperationException(
            $"無法開啟服務控制管理員（錯誤碼 {Marshal.GetLastWin32Error()}）");
        try
        {
            nint svc = CreateServiceW(
                scm, ServiceName, "Blue Squadron Kernel Module",
                0xF01FF,    // SERVICE_ALL_ACCESS
                1,          // SERVICE_KERNEL_DRIVER
                3,          // SERVICE_DEMAND_START
                1,          // SERVICE_ERROR_NORMAL
                sysPath,
                null, nint.Zero, null, null, null);
            if (svc == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1073) // ERROR_SERVICE_EXISTS
                    throw new InvalidOperationException($"CreateService 失敗（錯誤碼 {err}）");
            }
            else
                CloseServiceHandle(svc);
        }
        finally { CloseServiceHandle(scm); }
    }

    private static void StartService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running) return;
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException ex) when (ex.InnerException?.Message.Contains("1056") == true)
        {
            // ERROR_SERVICE_ALREADY_RUNNING — that's fine
        }
    }

    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
        catch { }
    }

    private static void DeleteService()
    {
        nint scm = OpenSCManagerW(null, null, 0x0002);
        if (scm == 0) return;
        try
        {
            nint svc = OpenServiceW(scm, ServiceName, 0x10000); // DELETE
            if (svc != 0)
            {
                DeleteServiceNative(svc);
                CloseServiceHandle(svc);
            }
        }
        finally { CloseServiceHandle(scm); }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(
        string fileName, uint access, uint share, nint securityAttributes,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        nint device, uint ioControlCode, nint inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateServiceW(
        nint scm, string serviceName, string displayName,
        uint desiredAccess, uint serviceType, uint startType, uint errorControl,
        string binaryPath, string? loadOrderGroup, nint tagId,
        string? dependencies, string? serviceStartName, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenServiceW(nint scm, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "DeleteService")]
    private static extern bool DeleteServiceNative(nint service);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(nint handle);
}
