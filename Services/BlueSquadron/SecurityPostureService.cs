using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 安全態勢服務——聚合現有安全服務的讀值，加上額外的 WMI/登錄檔查詢，
/// 餌給純函式評分引擎得到完整態勢。
/// </summary>
/// <remarks>
/// Phase 2: 大幅擴展——填入所有 null 偵測 + 新增「系統攻擊面」第六防線。
/// CPUID 走 X86Base.CpuId，服務狀態走 WMI Win32_Service，BitLocker 走
/// root\CIMV2\Security\MicrosoftVolumeEncryption，TPM 走
/// root\CIMV2\Security\MicrosoftTpm。
/// </remarks>
public sealed class SecurityPostureService : ObservableObject
{
    private bool _loading;
    public bool IsLoading
    {
        get => _loading;
        private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }
    public bool CanRefresh => !_loading;

    private string _status = "尚未評估。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private SecurityPosture? _posture;
    public SecurityPosture? Posture
    {
        get => _posture;
        private set
        {
            if (SetProperty(ref _posture, value))
            {
                OnPropertyChanged(nameof(TotalScore));
                OnPropertyChanged(nameof(ScoreText));
            }
        }
    }

    public int TotalScore => _posture?.TotalScore ?? 0;
    public string ScoreText => _posture is null ? "—" : $"{_posture.TotalScore} / 100";

    public void Refresh(MainViewModel vm)
    {
        if (_loading) return;
        _ = RefreshAsync(vm);
    }

    private async Task RefreshAsync(MainViewModel vm)
    {
        IsLoading = true;
        Status = "評估中…";
        try
        {
            var posture = await Task.Run(() => EvaluateFromVm(vm));
            Posture = posture;
            vm.BlueSquadron.UpdateDefenseLines(posture);
            Status = "評估完成（全部唯讀偵測）。";
        }
        catch (Exception ex)
        {
            Status = "評估失敗：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── 蓒集事實 ────────────────────────────────────────────────────────────────────

    private static SecurityPosture EvaluateFromVm(MainViewModel vm)
    {
        var dma = CollectDmaFacts(vm);
        var fw = CollectFirmwareFacts(vm);
        var cpu = CollectCpuFacts(vm);
        var stor = CollectStorageFacts(vm);
        var drv = CollectDriverFacts(vm);
        var surface = CollectSurfaceFacts();

        return SecurityScoreEngine.Evaluate(dma, fw, cpu, stor, drv, surface);
    }

    // ══════ DMA 事實 ══════════════════════════════════════════════════════════════

    private static SecurityScoreEngine.DmaFacts CollectDmaFacts(MainViewModel vm)
    {
        bool? hvci = null, vbs = null, iommu = null, dma = null, credGuard = null;
        bool? dmaEnforced = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard");
            foreach (ManagementObject m in searcher.Get())
            {
                uint? vbsStatus = ToUInt(m["VirtualizationBasedSecurityStatus"]);
                vbs = vbsStatus == 2;

                var running = ToUIntArray(m["SecurityServicesRunning"]);
                hvci = running.Contains(2u);
                credGuard = running.Contains(1u);

                var available = ToUIntArray(m["AvailableSecurityProperties"]);
                // 屬性 1 是「hypervisor 支援（VT-x/AMD-V）」，不是 IOMMU/VT-d——那是兩件不同的硬體。
                // VT-d 的有無要看 ACPI 的 DMA 重對映表，見 ReadIommuPresent()。
                iommu = ReadIommuPresent();
                dma = available.Contains(3u);
                break;
            }
        }
        catch { }

        // DMA 強制執行：DmaSecurity 機碼在沒開這個功能的機器上整個不存在，
        // 讀不到就要回 null（未知），不能用 false 假裝「已確認未啟用」。
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DmaSecurity");
            var val = key?.GetValue("DmaRemappingEnabled");
            dmaEnforced = val is null ? null : Convert.ToInt32(val) != 0;
        }
        catch { }

        return new SecurityScoreEngine.DmaFacts(
            HvciEnabled: hvci,
            VbsRunning: vbs,
            IommuAvailable: iommu,
            DmaProtection: dma,
            CredentialGuard: credGuard,
            BitLockerEnabled: ReadBitLockerSystemDrive(),
            // 不可拿 DmaRemappingEnabled 冒充 BitLocker 的 DMA 防護：兩者毫無關係。
            // 這一份事實讀不到就留 null（誠實顯示未評估），不要用 BitLocker 開不開來代替。
            BitLockerDmaProtection: null,
            ThunderboltSecurityLevel: ReadThunderboltSecurity(),
            DmaEnforced: dmaEnforced
        );
    }

    // ══════ Firmware 事實 ═════════════════════════════════════════════════════════

    private static SecurityScoreEngine.FirmwareFacts CollectFirmwareFacts(MainViewModel vm)
    {
        bool? secBoot = null, setupMode = null;
        foreach (var row in vm.Firmware.SecureBoot)
        {
            if (row.Key == "Secure Boot")
            {
                if (row.Value.Contains("開啟")) secBoot = true;
                else if (row.Value.Contains("關閉")) secBoot = false;
            }
            if (row.Key == "Setup Mode")
            {
                setupMode = row.Value.Contains("是");
            }
        }

        bool? testSign = ReadTestSigning();
        bool? tpmPresent = null;
        string? tpmVersion = null;
        ReadTpm(out tpmPresent, out tpmVersion);

        bool? uefiBoot = ReadUefiBoot();
        bool? biosPassword = ReadBiosPassword();

        return new SecurityScoreEngine.FirmwareFacts(
            SecureBootEnabled: secBoot,
            SecureBootInSetupMode: setupMode,
            TestSigningEnabled: testSign,
            SpiWriteProtected: null,
            MeManufacturingMode: null,
            MeasuredBootPresent: null,
            MeVersion: ParseMeVersion(vm),
            TpmPresent: tpmPresent,
            TpmVersion: tpmVersion,
            UefiBoot: uefiBoot,
            BiosPasswordSet: biosPassword
        );
    }

    // ══════ CPU 事實 ══════════════════════════════════════════════════════════════

    private static SecurityScoreEngine.CpuFacts CollectCpuFacts(MainViewModel vm)
    {
        bool? specMit = null;
        foreach (var row in vm.CpuSecurity.Rows)
        {
            if (row.Key.Contains("IBRS") && row.Value == "是")
                specMit = true;
        }
        if (specMit is null && vm.CpuSecurity.Rows.Count > 0)
            specMit = false;

        bool? dep = ReadDepPolicy();
        bool? aslr = ReadAslrHighEntropy();

        // CET from PlatformTrust rows
        bool? cet = null;
        foreach (var row in vm.PlatformTrust.Rows)
        {
            if (row.Value.Contains("核心模式硬體強制堆疊保護"))
                cet = true;
        }

        // NX bit: CPUID 0x80000001 EDX bit 20
        bool? nxEnabled = ReadNxBit();

        // CFG: registry MitigationOptions or current process PE check
        bool? cfgEnabled = ReadCfg();

        // SMEP: CPUID leaf 7 EBX bit 7
        bool? smep = ReadSmep();

        // SMAP: CPUID leaf 7 EBX bit 20
        bool? smap = ReadSmap();

        // LSASS PPL
        bool? lsassPpl = ReadLsassPpl();

        // Speculative execution registry overrides
        bool? specOverrideSafe = ReadSpecOverrideSafe();

        // Kernel CET (Shadow Stack)
        bool? kernelCet = ReadKernelCet();
        if (kernelCet is null && cet == true) kernelCet = true;

        return new SecurityScoreEngine.CpuFacts(
            SpecMitigationsEnabled: specMit,
            NxEnabled: nxEnabled,
            DepEnabled: dep,
            AslrHighEntropy: aslr,
            CfgEnabled: cfgEnabled,
            CetEnabled: kernelCet,
            SmepEnabled: smep,
            SmapEnabled: smap,
            LsassPplEnabled: lsassPpl,
            SpecOverrideSafe: specOverrideSafe
        );
    }

    // ══════ Storage 事實 ═══════════════════════════════════════════════════════════

    private static SecurityScoreEngine.StorageFacts CollectStorageFacts(MainViewModel vm)
    {
        bool? bitlocker = ReadBitLockerSystemDrive();
        bool? escrow = ReadBitLockerEscrow();
        string? encMethod = ReadBitLockerEncryptionMethod();

        return new SecurityScoreEngine.StorageFacts(
            BitLockerEnabled: bitlocker,
            NvmeFirmwareBaseline: null,
            SmartHealthy: ReadSmartHealthy(),
            BitLockerEscrow: escrow,
            EncryptionMethod: encMethod
        );
    }

    /// <summary>
    /// 磁碟健康狀態，來自儲存命名空間的 <c>MSFT_PhysicalDisk.HealthStatus</c>。
    /// </summary>
    /// <remarks>
    /// 不可用「磁碟清單非空」代替：那只是「偵測到磁碟」，與健康無關。
    /// （本機 StorageSmartService.Drives 的每一列只有 Index 與 Label，沒有任何健康欄位。）
    /// 0=Healthy、1=Warning、2=Unhealthy；查不到就回 null，讓評分顯示「無法評估」。
    /// </remarks>
    private static bool? ReadSmartHealthy()
    {
        try
        {
            var scope = new ManagementScope(@"\\localhost\ROOT\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT FriendlyName, HealthStatus FROM MSFT_PhysicalDisk"));
            bool any = false;
            foreach (ManagementObject d in searcher.Get())
            {
                using (d)
                {
                    any = true;
                    if (Convert.ToInt32(d["HealthStatus"] ?? 0) != 0) return false;
                }
            }
            return any ? true : null;
        }
        catch { return null; }
    }

    // ══════ Driver 事實 ════════════════════════════════════════════════════════════

    /// <summary>
    /// 已知被 BYOVD（自帶漏洞驅動）濫用的核心驅動檔名。
    /// </summary>
    /// <remarks>
    /// 名單取自公開的漏洞驅動彙整（LOLDrivers 一類）。這不是完整清單、也不是「有它就中毒」——
    /// 這些驅動多半是廠商的正常工具（超頻、燈效、診斷），問題在於它們的任意讀寫能力可被拿來
    /// 關掉核心保護、覆寫核心記憶體。列出來是要讓使用者知道自己開著哪些攻擊面。
    /// 真正的阻擋要靠 Windows 內建的「弱點驅動程式封鎖清單」，不是靠這張表。
    /// </remarks>
    private static readonly string[] KnownVulnerableDriverFiles =
    [
        "truesight.sys",     // Adlice TrueSight（2026-08 本機確實被以此手法癱瘓防毒）
        "gdrv.sys", "gdrv2.sys",             // GIGABYTE
        "rtcore64.sys",                       // MSI Afterburner
        "dbutil_2_3.sys",                     // Dell BIOS Utility
        "mhyprot2.sys", "mhyprot3.sys",       // miHoYo 反作弊
        "asio3.sys", "asio2.sys", "asio.sys", // ASUS
        "iqvw64e.sys",                        // Intel 網路診斷
        "ene.sys", "eneio64.sys",             // ASUS Aura
        "winio64.sys", "winio32.sys",         // WinIO
        "winring0.sys", "winring0x64.sys",    // WinRing0（本程式的 MSR 讀取也靠它，屬雙用途）
        "kprocesshacker.sys", "iqvw32.sys",
        "cpuz141.sys", "cpuz.sys",
        "speedfan.sys", "physmem.sys", "pcidrv.sys",
        "nvflash.sys", "nvflasher.sys",
    ];

    /// <summary>已載入的核心驅動裡，有幾個檔名落在已知濫用清單上。</summary>
    private static int CountKnownVulnerableDrivers()
    {
        try
        {
            var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2", "SELECT Name, PathName FROM Win32_SystemDriver WHERE State='Running'");
            foreach (ManagementObject d in searcher.Get())
            {
                using (d)
                {
                    string path = (d["PathName"] as string) ?? "";
                    string file = Path.GetFileName(path.Replace('/', '\\'));
                    if (file.Length == 0) continue;
                    if (KnownVulnerableDriverFiles.Contains(file)) hits.Add(file);
                }
            }
            return hits.Count;
        }
        catch { return 0; }
    }

    private static SecurityScoreEngine.DriverFacts CollectDriverFacts(MainViewModel vm)
    {
        var audit = vm.DriverAudit;
        int total = audit.AllRows.Count;
        int unsigned = 0;
        foreach (var r in audit.AllRows)
            if (r.Severity == 2) unsigned++;

        return new SecurityScoreEngine.DriverFacts(
            TotalDrivers: total,
            UnsignedDrivers: unsigned,
            KnownVulnerableDrivers: CountKnownVulnerableDrivers(),
            VulnerableDriverBlocklistEnabled: CheckDriverBlocklist(),
            LsassProtected: ReadLsassPpl(),
            WdacActive: ReadWdacActive(),
            KernelDebugOff: ReadKernelDebugOff(),
            CoInstallersDisabled: ReadCoInstallersDisabled(),
            StaleDriverCertsClean: ReadStaleDriverCertsClean()
        );
    }

    // ══════ Surface (攻擊面) 事實 ═══════════════════════════════════════════════════

    private static SecurityScoreEngine.SurfaceFacts CollectSurfaceFacts()
    {
        return new SecurityScoreEngine.SurfaceFacts(
            DefenderRealtimeOn: ReadDefenderRealtime(),
            FirewallDomain: ReadFirewallProfile("DomainProfile"),
            FirewallPublic: ReadFirewallProfile("PublicProfile"),
            FirewallPrivate: ReadFirewallProfile("StandardProfile"),
            RdpDisabled: ReadRdpDisabled(),
            AutoLogonOff: ReadAutoLogonOff(),
            GuestDisabled: ReadGuestDisabled(),
            AdminSafe: ReadAdminSafe(),
            PsRestricted: ReadPsRestricted(),
            WshDisabled: ReadWshDisabled(),
            RdpNlaRequired: ReadRdpNla(),
            SmbV1Disabled: ReadSmbV1Disabled(),
            WinRmStopped: !IsServiceRunning("WinRM"),
            PrintSpoolerStopped: !IsServiceRunning("Spooler"),
            DaysSinceUpdate: ReadDaysSinceUpdate()
        );
    }

    // ── 新增偵測器 ──────────────────────────────────────────────────────────────────

    // ---- CPUID helpers (X86Base.CpuId) ----

    private static bool? ReadNxBit()
    {
        try
        {
            if (!X86Base.IsSupported) return null;
            var (_, _, _, edx) = X86Base.CpuId(unchecked((int)0x80000001), 0);
            bool cpuNx = (edx & (1 << 20)) != 0;
            if (!cpuNx) return false;
            // Also check OS DEP is not forced off
            bool depOff = false;
            try
            {
                var bc = RunBcdedit("/enum {current}");
                if (bc is not null)
                {
                    var m = Regex.Match(bc, @"nx\s+(\w+)", RegexOptions.IgnoreCase);
                    if (m.Success) depOff = m.Groups[1].Value.Equals("AlwaysOff", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return cpuNx && !depOff;
        }
        catch { return null; }
    }

    private static bool? ReadSmep()
    {
        try
        {
            if (!X86Base.IsSupported) return null;
            var (_, ebx, _, _) = X86Base.CpuId(7, 0);
            return (ebx & (1 << 7)) != 0;
        }
        catch { return null; }
    }

    private static bool? ReadSmap()
    {
        try
        {
            if (!X86Base.IsSupported) return null;
            var (_, ebx, _, _) = X86Base.CpuId(7, 0);
            return (ebx & (1 << 20)) != 0;
        }
        catch { return null; }
    }

    private static bool? ReadCfg()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
            var opts = key?.GetValue("MitigationOptions") as byte[];
            if (opts is { Length: > 1 })
            {
                int nibble = opts[1] & 0x0F;
                if ((nibble & 0x01) != 0) return true;
            }
            // Fallback: check current process PE for IMAGE_DLLCHARACTERISTICS_GUARD_CF
            return IsCurrentProcessCfg();
        }
        catch { return null; }
    }

    private static bool? ReadLsassPpl()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Lsa");
            var val = key?.GetValue("RunAsPPL");
            return val is int v && v != 0;
        }
        catch { return null; }
    }

    private static bool? ReadSpecOverrideSafe()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var fso = key?.GetValue("FeatureSettingsOverride");
            var fsom = key?.GetValue("FeatureSettingsOverrideMask");
            if (fso is null && fsom is null) return true; // absent = OS-managed = good
            if (fso is int ov && fsom is int mask)
            {
                bool disabled = (ov & 0x03) == 0x03 && (mask & 0x03) == 0x03;
                return !disabled;
            }
            return true;
        }
        catch { return null; }
    }

    private static bool? ReadKernelCet()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\KernelShadowStacks");
            var val = key?.GetValue("Enabled");
            return val is int v && v == 1;
        }
        catch { return null; }
    }

    // ---- TPM ----

    private static void ReadTpm(out bool? present, out string? version)
    {
        present = null;
        version = null;
        try
        {
            var scope = new ManagementScope(@"\\localhost\root\CIMV2\Security\MicrosoftTpm");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm"));
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                present = true;
                var spec = obj["SpecVersion"]?.ToString();
                if (spec is not null)
                    version = spec.Split(',')[0].Trim();
                return;
            }
            present = false;
        }
        catch { }
    }

    // ---- UEFI boot mode ----

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);

    private static bool? ReadUefiBoot()
    {
        try
        {
            if (GetFirmwareType(out int ft)) return ft == 2; // FirmwareTypeUefi
            return null;
        }
        catch { return null; }
    }

    // ---- BIOS password (vendor-specific, best effort) ----

    private static bool? ReadBiosPassword()
    {
        try
        {
            // Dell
            try
            {
                var scope = new ManagementScope(@"\\localhost\root\DCIM\SYSMAN\BIOS");
                scope.Connect();
                using var s = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT IsSet FROM DCIM_BIOSPassword WHERE AttributeName='AdminPwd'"));
                using var r = s.Get();
                foreach (ManagementObject o in r)
                    if (o["IsSet"] is bool b) return b;
            }
            catch { }
            // Lenovo
            try
            {
                var scope = new ManagementScope(@"\\localhost\root\WMI");
                scope.Connect();
                using var s = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT CurrentSetting FROM Lenovo_BiosSetting WHERE CurrentSetting LIKE '%AdminPassword%'"));
                using var r = s.Get();
                foreach (ManagementObject o in r)
                {
                    var setting = o["CurrentSetting"]?.ToString() ?? "";
                    return setting.Contains("Enable", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return null;
        }
        catch { return null; }
    }

    // ---- BitLocker ----

    private static bool? ReadBitLockerSystemDrive()
    {
        try
        {
            var scope = new ManagementScope(
                @"\\localhost\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='C:'"));
            using var results = searcher.Get();
            foreach (ManagementObject vol in results)
            {
                var status = vol["ProtectionStatus"];
                return status is uint s && s == 1;
            }
            return false;
        }
        catch { return null; }
    }

    private static bool? ReadBitLockerEscrow()
    {
        try
        {
            var scope = new ManagementScope(
                @"\\localhost\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='C:'"));
            using var results = searcher.Get();
            foreach (ManagementObject vol in results)
            {
                var inP = vol.GetMethodParameters("GetKeyProtectors");
                inP["KeyProtectorType"] = (uint)3;
                var outP = vol.InvokeMethod("GetKeyProtectors", inP, null);
                var ids = outP?["VolumeKeyProtectorID"] as string[];
                return ids is { Length: > 0 };
            }
            return null;
        }
        catch { return null; }
    }

    private static string? ReadBitLockerEncryptionMethod()
    {
        try
        {
            var scope = new ManagementScope(
                @"\\localhost\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='C:'"));
            using var results = searcher.Get();
            foreach (ManagementObject vol in results)
            {
                var inP = vol.GetMethodParameters("GetEncryptionMethod");
                var outP = vol.InvokeMethod("GetEncryptionMethod", inP, null);
                var method = outP?["EncryptionMethod"];
                // WMI out 參數是 uint32，不是 int——用 is int 會恆為 false（同檔 ProtectionStatus 就用 is uint）。
                if (method is not null && long.TryParse(method.ToString(), out long mv))
                {
                    int m = (int)mv;
                    return m switch
                    {
                        0 => "None",
                        1 => "AES-128-Diffuser",
                        2 => "AES-256-Diffuser",
                        3 => "AES-128",
                        4 => "AES-256",
                        5 => "Hardware",
                        6 => "XTS-AES-128",
                        7 => "XTS-AES-256",
                        _ => $"Unknown({m})"
                    };
                }
            }
            return null;
        }
        catch { return null; }
    }

    // ---- Driver additions ----

    private static bool? ReadWdacActive()
    {
        try
        {
            // 不可以「機碼存在 = WDAC 已部署」：Control\CI\Policy 在所有 Windows 上都有，
            // 它只是政策設定的容器。真正的證據是使用中的政策檔。
            string dir = Path.Combine(Environment.SystemDirectory, "CodeIntegrity", "CiPolicies", "Active");
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.cip").Any())
                return true;

            // 沒有 .cip 時退回單一政策檔（舊版 Windows 的 SIPolicy.p7b）
            string legacy = Path.Combine(Environment.SystemDirectory, "CodeIntegrity", "SIPolicy.p7b");
            return File.Exists(legacy);
        }
        catch { return null; }
    }

    private static bool? ReadKernelDebugOff()
    {
        try
        {
            var output = RunBcdedit("/enum {current}");
            if (output is null) return null;
            var match = Regex.Match(output, @"debug\s+(Yes|No)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value.Equals("No", StringComparison.OrdinalIgnoreCase);
            return true; // if "debug" line absent, it's off
        }
        catch { return null; }
    }

    private static bool? ReadCoInstallersDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer");
            var val = key?.GetValue("DisableCoInstallers");
            return val is int v && v == 1;
        }
        catch { return null; }
    }

    /// <summary>
    /// 韌體是否提供了 DMA 重對映表：Intel 的 DMAR、AMD 的 IVRS。
    /// </summary>
    /// <remarks>
    /// 這才是 IOMMU / VT-d / AMD-Vi 存在的直接證據。先前用
    /// <c>Win32_DeviceGuard.AvailableSecurityProperties</c> 的屬性 1 代替是錯的——
    /// 那個位元代表「hypervisor 支援（VT-x/AMD-V）」，與 DMA 重對映是兩套不同的硬體功能，
    /// 讀不到就回 null，不要用具名 IOMMU 的結論去誤導使用者。
    /// </remarks>
    private static bool? ReadIommuPresent()
    {
        try
        {
            int size = EnumSystemFirmwareTables(AcpiSignature, null, 0);
            if (size <= 0) return null;

            var table = new byte[size];
            if (EnumSystemFirmwareTables(AcpiSignature, table, size) <= 0) return null;

            for (int i = 0; i + 4 <= size; i += 4)
            {
                uint sig = BitConverter.ToUInt32(table, i);
                if (sig == DmarSignature || sig == IvrsSignature) return true;
            }
            return false;
        }
        catch { return null; }
    }

    private const uint AcpiSignature = 0x41435049;  // 'ACPI'
    private const uint DmarSignature = 0x52414D44;  // 'DMAR'
    private const uint IvrsSignature = 0x53525649;  // 'IVRS'

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern int EnumSystemFirmwareTables(uint firmwareTableProviderSignature, byte[]? buffer, int bufferSize);

    private static bool? ReadStaleDriverCertsClean()
    {
        // 原本讀 Control\CI\StaleDriverPackages，但那個值名在 Windows 上不存在
        // （實測：機碼在、值不在）→ 恆回 true，等於憑空給一個「沒有過期憑證」的假通過。
        // 專案對驅動年紀的既定立場是不以老舊逕行判斷（見 DriverAuditService 的誠實界線），
        // 沒有一個可靠的來源可以回答這一題，所以回 null 讓它顯示「無法評估」。
        return null;
    }

    // ---- Surface (attack surface) detectors ----

    private static bool? ReadDefenderRealtime()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection");
            var val = key?.GetValue("DisableRealtimeMonitoring");
            return val is not int v || v == 0;
        }
        catch { return null; }
    }

    private static bool? ReadFirewallProfile(string profileRegName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profileRegName}");
            var val = key?.GetValue("EnableFirewall");
            return val is int v && v == 1;
        }
        catch { return null; }
    }

    private static bool? ReadRdpDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server");
            var val = key?.GetValue("fDenyTSConnections");
            return val is int v && v == 1;
        }
        catch { return null; }
    }

    private static bool? ReadAutoLogonOff()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            var val = key?.GetValue("AutoAdminLogon");
            return val?.ToString() != "1";
        }
        catch { return null; }
    }

    private static bool? ReadGuestDisabled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Disabled FROM Win32_UserAccount WHERE Name='Guest' AND LocalAccount=TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject user in results)
                return user["Disabled"] is bool d && d;
            return true; // no guest account = safe
        }
        catch { return null; }
    }

    private static bool? ReadAdminSafe()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Disabled, PasswordRequired FROM Win32_UserAccount WHERE Name='Administrator' AND LocalAccount=TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject user in results)
            {
                if (user["Disabled"] is bool d && d) return true;
                return user["PasswordRequired"] is bool p && p;
            }
            return true;
        }
        catch { return null; }
    }

    private static bool? ReadPsRestricted()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell");
            var policy = key?.GetValue("ExecutionPolicy")?.ToString();
            if (policy is null) return true; // default = Restricted on client
            return policy.Equals("Restricted", StringComparison.OrdinalIgnoreCase)
                || policy.Equals("AllSigned", StringComparison.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    private static bool? ReadWshDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Script Host\Settings");
            var val = key?.GetValue("Enabled");
            return val is int v && v == 0;
        }
        catch { return null; }
    }

    private static bool? ReadRdpNla()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
            var val = key?.GetValue("UserAuthentication");
            return val is int v && v == 1;
        }
        catch { return null; }
    }

    private static bool? ReadSmbV1Disabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
            var val = key?.GetValue("SMB1");
            if (val is int v) return v == 0;
            // Absent: check if mrxsmb10 service exists (if not, SMBv1 removed)
            using var featKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\mrxsmb10");
            return featKey is null;
        }
        catch { return null; }
    }

    private static bool IsServiceRunning(string serviceName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT State FROM Win32_Service WHERE Name='{serviceName}'");
            using var results = searcher.Get();
            foreach (ManagementObject svc in results)
                return svc["State"]?.ToString()?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
            return false;
        }
        catch { return false; }
    }

    private static int? ReadDaysSinceUpdate()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install");
            var val = key?.GetValue("LastSuccessTime")?.ToString();
            if (val is not null && DateTime.TryParse(val, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var lastUpdate))
                return (int)(DateTime.Now - lastUpdate).TotalDays;
            return null;
        }
        catch { return null; }
    }

    // ── 原有輔助讀取 ─────────────────────────────────────────────────────────────────

    private static int? ReadThunderboltSecurity()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\thunderbolt_controller\Parameters");
            if (key?.GetValue("SecurityLevel") is int level) return level;
        }
        catch { }
        return null;
    }

    private static bool? ReadTestSigning()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CI");
            if (key?.GetValue("TestSigning") is int v) return v != 0;
        }
        catch { }
        return null;
    }

    private static bool? ReadDepPolicy()
    {
        try
        {
            // 64-bit Windows = DEP always on for kernel; check bcdedit nx
            var bc = RunBcdedit("/enum {current}");
            if (bc is not null)
            {
                var m = Regex.Match(bc, @"nx\s+(\w+)", RegexOptions.IgnoreCase);
                if (m.Success)
                    return !m.Groups[1].Value.Equals("AlwaysOff", StringComparison.OrdinalIgnoreCase);
            }
            return true; // 64-bit default = DEP on
        }
        catch { return null; }
    }

    private static bool? ReadAslrHighEntropy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var val = key?.GetValue("MoveImages");
            if (val is int v) return v == unchecked((int)0xFFFFFFFF);
            return null;
        }
        catch { return null; }
    }

    private static bool? CheckDriverBlocklist()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "CodeIntegrity", "driversipolicy.p7b");
            return File.Exists(path);
        }
        catch { return null; }
    }

    private static string? ParseMeVersion(MainViewModel vm)
    {
        foreach (var row in vm.BiosMe.Me)
            if (row.Key.Contains("ME") && !string.IsNullOrEmpty(row.Value) && row.Value != "—")
                return row.Value;
        return null;
    }

    private static string? RunBcdedit(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch { return null; }
    }

    private static bool IsCurrentProcessCfg()
    {
        try
        {
            var mainMod = Process.GetCurrentProcess().MainModule;
            if (mainMod?.FileName is null) return false;
            using var fs = File.OpenRead(mainMod.FileName);
            using var reader = new BinaryReader(fs);
            fs.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = reader.ReadInt32();
            fs.Seek(peOffset + 4 + 20 + 70, SeekOrigin.Begin);
            ushort dllChars = reader.ReadUInt16();
            return (dllChars & 0x4000) != 0;
        }
        catch { return false; }
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
