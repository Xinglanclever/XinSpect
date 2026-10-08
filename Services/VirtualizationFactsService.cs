using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 虛擬化平台狀態的<b>讀取</b>層：把五個各自獨立的觀測值湊成 <see cref="VirtualizationState"/>，
/// 再交給純解碼器 <see cref="VirtualizationJudge"/> 判讀。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要五路都讀：</b>Windows 從不把「Hyper-V 到底在不在跑」放在一個地方。
/// CPUID 說的是「有沒有虛擬層」、選用功能說的是「元件裝了沒」、服務狀態說的是「管理服務起了沒」、
/// WMI 說的是「有沒有定義 VM／交換器」——這四件事可以任意組合，而使用者看到的一律是
/// 「Hyper-V：已啟用／停用」這種單一布林，於是「裝了但沒開」這種狀態永遠被講錯。
/// </para>
/// <para>
/// 全數唯讀：CPUID 指令、服務控制管理員查詢、WMI 列舉。不寫任何設定、不啟停任何服務。
/// 讀不到的欄位一律回 -1 或 false 並在判讀層如實說明，不用預設值冒充。
/// </para>
/// </remarks>
public static class VirtualizationFactsService
{
    private const string Category = "虛擬化";

    /// <summary>收集事實。生產路徑走系統查詢；測試以注入委派取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<VirtualizationState>? probe = null)
    {
        VirtualizationState s;
        try { s = (probe ?? Probe)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact("virt.judge", Category, "虛擬化平台判讀", "", "",
                    "CPUID／服務狀態／選用功能／WMI", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "讀取失敗：" + ex.Message),
            ];
        }

        var v = VirtualizationJudge.Judge(s);
        var list = new List<HardwareFact>
        {
            new("virt.judge", Category, "虛擬化平台判讀",
                v.Headline, "", "CPUID leaf 1 ECX bit 31 ＋ 選用功能 ＋ 服務狀態 ＋ WMI 列舉",
                FactTrustLevel.Measured, false, at, null,
                s.HypervisorLoaded || s.PlatformInstalled || s.VmmsRunning
                    ? FactAvailability.Present : FactAvailability.NotApplicable,
                s.HypervisorLoaded || s.PlatformInstalled || s.VmmsRunning ? null : "本機未啟用任何虛擬化平台"),
        };

        // 逐項陳列，讓使用者自己核對判讀是從哪幾個值來的
        list.Add(Bool("virt.hypervisor", "虛擬層載入（CPUID）", s.HypervisorLoaded, at,
            "CPUID leaf 1 ECX bit 31（Hv#1 存在位）",
            "此位僅表示「有虛擬層在跑」，不代表 Hyper-V 的角色已啟用——兩件事必須分開看"));
        if (s.HypervisorLoaded)
            list.Add(new HardwareFact("virt.vendor", Category, "虛擬層簽章",
                string.IsNullOrWhiteSpace(s.HypervisorVendor) ? "—" : s.HypervisorVendor, "",
                "CPUID leaf 0x40000000", FactTrustLevel.Measured, false, at, null));

        list.Add(Bool("virt.platform", "Hyper-V 平台已安裝", s.PlatformInstalled, at,
            "WMI Win32_OptionalFeature（Microsoft-Hyper-V）",
            "已安裝不等於已載入——hypervisorlaunchtype 為 Off 時裝了也不會跑"));

        list.Add(ServiceFact("virt.vmms", "Hyper-V 虛擬機器管理服務", "vmms", s.VmmsRunning, at));

        list.Add(s.VmCount >= 0
            ? new HardwareFact("virt.vmcount", Category, "已定義的虛擬機器", s.VmCount.ToString(), "台",
                "WMI root\\virtualization\\v2 Msvm_ComputerSystem（虛擬機器以外的項目已排除）",
                FactTrustLevel.Measured, false, at, s.VmCount,
                s.VmCount > 0 ? FactAvailability.Present : FactAvailability.NotApplicable,
                s.VmCount > 0 ? null : "未定義任何虛擬機器")
            : new HardwareFact("virt.vmcount", Category, "已定義的虛擬機器", "", "",
                "WMI root\\virtualization\\v2", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "WMI 查詢失敗——讀不到就是不猜"));

        list.Add(s.VirtualSwitchCount >= 0
            ? new HardwareFact("virt.vswitch", Category, "虛擬交換器", s.VirtualSwitchCount.ToString(), "個",
                "WMI root\\virtualization\\v2 Msvm_VirtualEthernetSwitch",
                FactTrustLevel.Measured, false, at, s.VirtualSwitchCount)
            : new HardwareFact("virt.vswitch", Category, "虛擬交換器", "", "",
                "WMI root\\virtualization\\v2", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "WMI 查詢失敗——讀不到就是不猜"));

        list.Add(Bool("virt.hns", "主機網路服務（hns）執行中", s.HnsRunning, at,
            "服務控制管理員（hns 服務）",
            "容器與 WSL2 會用到 Hyper-V 平台，即使沒有開 Hyper-V 角色"));

        list.Add(new HardwareFact("virt.vbs", Category, "VBS／記憶體完整性",
            s.VbsEnabled switch
            {
                0 => "關閉",
                1 => "已啟用但未執行",
                2 => "執行中",
                _ => "—（讀不到）",
            }, "", "Win32_DeviceGuard.VirtualizationBasedSecurityStatus",
            FactTrustLevel.Reported, false, at, s.VbsEnabled >= 0 ? s.VbsEnabled : null,
            s.VbsEnabled >= 0 ? FactAvailability.Present : FactAvailability.ReadError,
            s.VbsEnabled >= 0 ? null : "Device Guard WMI 查詢失敗"));

        // VBS 存在位：MSR／TSC 可信度取決於它
        list.Add(new HardwareFact("virt.msr", Category, "MSR／TSC 讀值是否原生",
            v.MsrReadable ? "是（無虛擬層）" : "否（虛擬層可能攔截或改寫）", "",
            "由虛擬層存在位推得（見來源欄）", FactTrustLevel.Derived, false, at, null,
            FactAvailability.Present,
            v.MsrReadable ? null : "本程式所有 MSR 類卡片（Top-down、頻率真相、RDT、MCA、安全位元）的可信度都受此影響"));

        return list;
    }

    private static HardwareFact Bool(string key, string name, bool value, DateTimeOffset at,
        string source, string? note = null) =>
        new(key, Category, name, value ? "是" : "否", "", source,
            FactTrustLevel.Measured, false, at, value ? 1 : 0,
            value ? FactAvailability.Present : FactAvailability.NotApplicable,
            value ? null : note);

    private static HardwareFact ServiceFact(string key, string name, string service, bool running,
        DateTimeOffset at) =>
        new(key, Category, name, running ? "執行中" : "未執行", "",
            $"服務控制管理員（{service} 服務）", FactTrustLevel.Reported, false, at, running ? 1 : 0,
            running ? FactAvailability.Present : FactAvailability.NotApplicable,
            running ? null : "服務未執行");

    // ── 生產探測（唯讀）──

    /// <summary>把五路查詢湊成一個狀態。個別查詢失敗以 -1／false 表達，不讓整批失敗。</summary>
    internal static VirtualizationState Probe()
    {
        bool hyper = false;
        string vendor = "";
        try
        {
            if (System.Runtime.Intrinsics.X86.X86Base.IsSupported)
            {
                var r1 = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
                hyper = ((uint)r1.Ecx & 0x8000_0000) != 0;
                if (hyper)
                {
                    var r = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x40000000), 0);
                    vendor = System.Text.Encoding.ASCII.GetString(
                        BitConverter.GetBytes((uint)r.Ebx)
                            .Concat(BitConverter.GetBytes((uint)r.Ecx))
                            .Concat(BitConverter.GetBytes((uint)r.Edx)).ToArray()).TrimEnd('\0');
                }
            }
        }
        catch (Exception ex) { Diag.Swallow("CPUID 虛擬層偵測", ex, "視為無虛擬層"); }

        bool platform = OptionalFeatureState("Microsoft-Hyper-V") == 1;
        bool containers = OptionalFeatureState("Containers") == 1;
        bool wsl = OptionalFeatureState("Microsoft-Windows-Subsystem-Linux") == 1;

        return new VirtualizationState(
            hyper, vendor, platform,
            ServiceRunning("vmms"),
            CountVms(), CountVirtualSwitches(), ServiceRunning("hns"),
            containers, wsl,
            VbsStatus(), -1, -1);
    }

    /// <summary>選用功能狀態：1＝啟用、2＝停用、3＝不存在、-1＝查不到。</summary>
    internal static int OptionalFeatureState(string feature)
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2",
                $"SELECT InstallState FROM Win32_OptionalFeature WHERE Name='{feature}'");
            foreach (ManagementObject o in s.Get())
                using (o) return o["InstallState"] is { } v ? Convert.ToInt32(v) : -1;
            return -1;
        }
        catch (Exception ex)
        {
            Diag.Swallow("選用功能查詢", ex, "該功能狀態視為未知");
            return -1;
        }
    }

    /// <summary>VBS 狀態：0＝關、1＝啟用未執行、2＝執行中、-1＝查不到。</summary>
    internal static int VbsStatus()
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\Microsoft\\Windows\\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard");
            foreach (ManagementObject o in s.Get())
                using (o) return o["VirtualizationBasedSecurityStatus"] is { } v ? Convert.ToInt32(v) : -1;
            return -1;
        }
        catch (Exception ex)
        {
            Diag.Swallow("VBS 狀態查詢", ex, "視為未知，不假設關閉");
            return -1;
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ScStatusRunning = 0x0004;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out SvcStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct SvcStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted,
                    Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    internal static bool ServiceRunning(string name)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenServiceW(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return false;
            try { return QueryServiceStatus(svc, out var s) && s.CurrentState == ScStatusRunning; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>
    /// 已定義的虛擬機器數。<c>Msvm_ComputerSystem</c> 同時包含「主機本身」那一筆
    /// （<c>Caption</c> 為 <c>Hosting Computer System</c>、<c>Description</c> 含 <c>Hosting Computer System</c>），
    /// 必須排除，否則沒有任何 VM 的機器也會顯示「1 台」。
    /// </summary>
    internal static int CountVms()
    {
        try
        {
            int n = 0;
            using var s = new ManagementObjectSearcher("root\\virtualization\\v2",
                "SELECT Caption, Description FROM Msvm_ComputerSystem");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    string caption = o["Caption"] as string ?? "";
                    string desc = o["Description"] as string ?? "";
                    if (IsHostingComputerSystem(caption) || IsHostingComputerSystem(desc)) continue;
                    n++;
                }
            }
            return n;
        }
        catch (Exception ex)
        {
            Diag.Swallow("虛擬機器列舉", ex, "VM 數視為讀不到");
            return -1;
        }
    }

    /// <summary>
    /// 辨識「主機本身」那一筆。<b>繁中系統上的地雷：</b>那一筆的 Caption 是「主機電腦系統」、
    /// Description 是「Microsoft 主機電腦系統」——<b>含「主機」不含「Hosting」</b>。
    /// 只比對英文字串的話，在中文 Windows 上會把主機自己算成一台虛擬機器、回報「已定義 1 台 VM」，
    /// 而事實是 0 台；本機（Windows Server 2025 繁中）正是這個情形。
    /// </summary>
    internal static bool IsHostingComputerSystem(string text) =>
        text.Contains("Hosting Computer System", StringComparison.OrdinalIgnoreCase)
        || text.Contains("主機電腦系統", StringComparison.Ordinal)
        || text.Contains("主机电脑系统", StringComparison.Ordinal);

    internal static int CountVirtualSwitches()
    {
        try
        {
            int n = 0;
            using var s = new ManagementObjectSearcher("root\\virtualization\\v2",
                "SELECT Name FROM Msvm_VirtualEthernetSwitch");
            foreach (ManagementObject o in s.Get()) { using (o) n++; }
            return n;
        }
        catch (Exception ex)
        {
            Diag.Swallow("虛擬交換器列舉", ex, "交換器數視為讀不到");
            return -1;
        }
    }
}
