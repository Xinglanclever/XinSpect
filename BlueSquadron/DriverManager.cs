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

    private nint _device = -1;
    public bool IsLoaded { get; private set; }
    public string LastError { get; private set; } = "";

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

        // 2. Try to create/start the service
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
