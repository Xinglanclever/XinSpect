using System.Management;
using Microsoft.Win32;

namespace BlueSquadron;

/// <summary>
/// 以 WMI + 登錄檔蒐集安全事實——全部唯讀。
/// 回傳扁平的 <see cref="SecurityFacts"/> 結構供 Bridge 回報給 XinSpect 評分引擎。
/// </summary>
internal sealed class WmiSecurityReader
{
    public SecurityFacts ReadAll()
    {
        var f = new SecurityFacts();
        ReadDeviceGuard(f);
        ReadTestSigning(f);
        ReadSecureBoot(f);
        ReadDrivers(f);
        ReadThunderbolt(f);
        ReadSpecMitigations(f);
        ReadDriverBlocklist(f);
        return f;
    }

    private static void ReadDeviceGuard(SecurityFacts f)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard");
            foreach (ManagementObject m in searcher.Get())
            {
                uint? vbsStatus = ToUInt(m["VirtualizationBasedSecurityStatus"]);
                f.VbsRunning = vbsStatus == 2;

                var running = ToUIntArray(m["SecurityServicesRunning"]);
                f.HvciRunning = running.Contains(2u);
                f.CredentialGuardRunning = running.Contains(1u);
                f.CetEnabled = running.Contains(5u);

                var available = ToUIntArray(m["AvailableSecurityProperties"]);
                f.IommuAvailable = available.Contains(1u);
                f.DmaProtection = available.Contains(3u);
                break;
            }
        }
        catch { }
    }

    private static void ReadTestSigning(SecurityFacts f)
    {
        try
        {
            // NtQuerySystemInformation(103) CodeIntegrityOptions
            nint buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
            try
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, 0, 8);
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, 4, 0);
                int rc = NtQuerySystemInformation(103, buf, 8, out _);
                if (rc == 0)
                {
                    uint opts = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(buf, 4);
                    f.TestSigningEnabled = (opts & 0x0002) != 0;
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, nint buffer, int length, out int returned);

    private static void ReadSecureBoot(SecurityFacts f)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int v)
                f.SecureBootEnabled = v != 0;
        }
        catch { }
    }

    private static void ReadDrivers(SecurityFacts f)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, IsSigned FROM Win32_PnPSignedDriver");
            int total = 0, unsigned = 0;
            foreach (ManagementObject m in searcher.Get())
            {
                total++;
                if (m["IsSigned"] is bool signed && !signed) unsigned++;
            }
            f.TotalDriverCount = total;
            f.UnsignedDriverCount = unsigned;
        }
        catch { }
    }

    private static void ReadThunderbolt(SecurityFacts f)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\thunderbolt_controller\Parameters");
            if (key?.GetValue("SecurityLevel") is int level)
                f.ThunderboltSecurityLevel = level;
        }
        catch { }
    }

    private static void ReadSpecMitigations(SecurityFacts f)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var featureOverride = key?.GetValue("FeatureSettingsOverride");
            // If FeatureSettingsOverride is 0 or not present, mitigations are active
            f.SpecMitigationsActive = featureOverride is null or 0;
        }
        catch { }
    }

    private static void ReadDriverBlocklist(SecurityFacts f)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "CodeIntegrity", "driversipolicy.p7b");
            f.VulnerableDriverBlocklistPresent = File.Exists(path);
        }
        catch { }
    }

    private static uint? ToUInt(object? v)
    {
        try { return v is null ? null : Convert.ToUInt32(v); }
        catch { return null; }
    }

    private static uint[] ToUIntArray(object? v)
    {
        if (v is null) return [];
        try
        {
            if (v is uint[] u) return u;
            if (v is int[] i) return i.Select(x => (uint)x).ToArray();
            if (v is Array a) return a.Cast<object>().Select(Convert.ToUInt32).ToArray();
        }
        catch { }
        return [];
    }
}

/// <summary>由 Bridge 蒐集的扁平安全事實。</summary>
internal sealed class SecurityFacts
{
    // DMA / Memory
    public bool? HvciRunning { get; set; }
    public bool? VbsRunning { get; set; }
    public bool? IommuAvailable { get; set; }
    public bool? DmaProtection { get; set; }
    public bool? CredentialGuardRunning { get; set; }
    public bool? CetEnabled { get; set; }

    // Firmware
    public bool? SecureBootEnabled { get; set; }
    public bool? TestSigningEnabled { get; set; }

    // CPU
    public bool? SpecMitigationsActive { get; set; }

    // Drivers
    public int TotalDriverCount { get; set; }
    public int UnsignedDriverCount { get; set; }
    public bool? VulnerableDriverBlocklistPresent { get; set; }

    // Thunderbolt
    public int? ThunderboltSecurityLevel { get; set; }
}
