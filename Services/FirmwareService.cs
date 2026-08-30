using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>鍵值列。</summary>
public sealed class FirmwareRow
{
    public FirmwareRow(string key, string value) { Key = key; Value = value; }
    public string Key { get; }
    public string Value { get; }
}

/// <summary>
/// 韌體與開機信任鏈：UEFI 變數直接讀取 Secure Boot 四態（SecureBoot／SetupMode／AuditMode／DeployedMode）
/// 與開機項目數、Hypervisor 簽章偵測（VBS 開啟時 Windows 本身在 Hyper-V 之上，會影響所有 MSR 與計時的可信度——必須明說）、
/// 以及微碼修訂版（CPUID 觸發後自 IA32_BIOS_SIGN_ID 讀取）。
/// </summary>
/// <remarks>
/// 誠實界線：只讀；db/dbx 簽章清單的完整解析（含撤銷筆數）未做。微碼版本是「本核讀值」，
/// 各核通常一致但不保證；讀不到就顯示「—」。
/// </remarks>
public sealed class FirmwareService : ObservableObject
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariableEx(string name, string guid, byte[]? buffer, uint size, ref uint attributes);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previous, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    private const string EfiGlobal = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;

    private bool _loading;
    public bool IsLoading { get => _loading; private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); } }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<FirmwareRow> SecureBoot { get; } = [];
    public ObservableCollection<FirmwareRow> Boot { get; } = [];
    public ObservableCollection<FirmwareRow> Virtualization { get; } = [];
    public ObservableCollection<FirmwareRow> Microcode { get; } = [];

    public void Refresh()
    {
        if (_loading) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "讀取中…";
        SecureBoot.Clear(); Boot.Clear(); Virtualization.Clear(); Microcode.Clear();
        try
        {
            var (sbRows, bootRows) = await Task.Run(ReadUefi);
            foreach (var r in sbRows) SecureBoot.Add(r);
            foreach (var r in bootRows) Boot.Add(r);

            var (virtRows, mcRows) = await Task.Run(ReadVirtualizationAndMicrocode);
            foreach (var r in virtRows) Virtualization.Add(r);
            foreach (var r in mcRows) Microcode.Add(r);

            Status = "讀取完成。";
        }
        catch (Exception ex)
        {
            Status = "讀取失敗：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>啟用 SeSystemEnvironmentPrivilege（讀 UEFI 變數需要）。</summary>
    private static bool EnableFirmwarePrivilege()
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out var token))
                return false;
            if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out long luid))
                return false;
            var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>讀單一位元組 UEFI 變數；不存在或失敗回 null。</summary>
    private static byte? ReadUefiByte(string name)
    {
        var buf = new byte[4];
        uint attr = 0;
        uint ret = GetFirmwareEnvironmentVariableEx(name, EfiGlobal, buf, (uint)buf.Length, ref attr);
        return ret > 0 ? buf[0] : null;
    }

    private (List<FirmwareRow> Sb, List<FirmwareRow> Boot) ReadUefi()
    {
        var sb = new List<FirmwareRow>();
        var boot = new List<FirmwareRow>();
        bool privilegeOk = EnableFirmwarePrivilege();
        if (!privilegeOk)
        {
            sb.Add(new FirmwareRow("Secure Boot", "無法啟用 SeSystemEnvironmentPrivilege（本機不支援 UEFI 變數讀取，可能是 Legacy 開機）"));
            return (sb, boot);
        }

        var secureBoot = ReadUefiByte("SecureBoot");
        var setupMode = ReadUefiByte("SetupMode");
        var auditMode = ReadUefiByte("AuditMode");
        var deployedMode = ReadUefiByte("DeployedMode");

        sb.Add(new FirmwareRow("Secure Boot", secureBoot is null ? "—（變數不存在：可能是 Legacy 開機或 CSM）"
            : secureBoot == 1 ? "開啟" : "關閉"));
        if (setupMode is not null)
            sb.Add(new FirmwareRow("Setup Mode", setupMode == 1 ? "是（金鑰未部署，安全防護不完整）" : "否（金鑰已部署）"));
        if (auditMode is not null)
            sb.Add(new FirmwareRow("Audit Mode", auditMode == 1 ? "是" : "否"));
        if (deployedMode is not null)
            sb.Add(new FirmwareRow("Deployed Mode", deployedMode == 1 ? "是" : "否"));
        if (secureBoot == 1 && setupMode == 0)
            sb.Add(new FirmwareRow("綜合", "Secure Boot 開啟且金鑰已部署（標準防護狀態）；db/dbx 撤銷清單筆數的完整解析未做"));
        else if (secureBoot == 0)
            sb.Add(new FirmwareRow("綜合", "Secure Boot 關閉中——「已開啟」與「撤銷清單有沒有更新」是兩件事，目前兩者皆無防護"));

        // BootOrder：UEFI 變數為 2 位元組一項的陣列
        var orderBuf = new byte[128];
        uint attr = 0;
        uint ret = GetFirmwareEnvironmentVariableEx("BootOrder", EfiGlobal, orderBuf, (uint)orderBuf.Length, ref attr);
        if (ret > 0 && ret % 2 == 0)
            boot.Add(new FirmwareRow("開機項目數（BootOrder）", $"{ret / 2} 項"));
        boot.Add(new FirmwareRow("開機項目明細", "Boot#### 完整枚舉與裝置路徑解析未做"));
        return (sb, boot);
    }

    private (List<FirmwareRow> Virt, List<FirmwareRow> Mc) ReadVirtualizationAndMicrocode()
    {
        var virt = new List<FirmwareRow>();
        var mc = new List<FirmwareRow>();

        // Hypervisor：CPUID 0x1 ECX bit31 ＋ 0x40000000 簽章
        bool hyper = false;
        string signature = "";
        if (System.Runtime.Intrinsics.X86.X86Base.IsSupported)
        {
            var r1 = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
            hyper = ((uint)r1.Ecx & 0x8000_0000) != 0;
            if (hyper)
            {
                var r = System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x40000000), 0);
                signature = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes((uint)r.Ebx)
                    .Concat(BitConverter.GetBytes((uint)r.Ecx))
                    .Concat(BitConverter.GetBytes((uint)r.Edx)).ToArray()).TrimEnd('\0');
            }
        }
        virt.Add(new FirmwareRow("Hypervisor 存在", hyper ? "是" : "否"));
        if (hyper)
        {
            string vendor = signature switch
            {
                "Microsoft Hv" => "Microsoft Hyper-V",
                "KVMKVMKVM" => "KVM",
                "VMwareVMware" => "VMware",
                "XenVMMXenVMM" => "Xen",
                "VBoxVBoxVBox" => "VirtualBox",
                "TCGTCGTCGTCG" => "TCG",
                _ => signature,
            };
            virt.Add(new FirmwareRow("Hypervisor 簽章", vendor));
            virt.Add(new FirmwareRow("⚠ 可信度提示", "VBS／HVCI 開啟時 Windows 本身跑在 Hyper-V 之上——MSR／TSC／PMU 的讀值可能被虛擬化攔截或改寫，對照時請留意。"));
        }
        else
        {
            virt.Add(new FirmwareRow("可信度", "偵測不到 Hypervisor：裸機執行，MSR／計時讀值為原生。"));
        }

        // 微碼修訂版：CPUID leaf 1 觸發後自 IA32_BIOS_SIGN_ID（0x8B）高 32 位讀取。
        // 走 WinRing0Bridge：2026-08-30 實測 PawnIO 的 IntelMsr 在本機對每個 MSR 都回報成功卻回 0，
        // 用它會把「讀不到」誤報成「微碼版本 0」。
        try
        {
            using var bridge = WinRing0Bridge.Create();
            if (!bridge.Available)
            {
                mc.Add(new FirmwareRow("微碼修訂版（0x8B）", "—（MSR 橋接無法初始化：" + bridge.Error + "）"));
            }
            else
            {
                System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);   // 觸發微碼修訂版寫入 0x8B
                ulong? sign = bridge.ReadMsrPair64(0x8B);
                mc.Add(new FirmwareRow("微碼修訂版（0x8B）",
                    sign is null ? "—（MSR 讀取失敗）"
                    : (sign.Value >> 32) == 0 ? "—（高 32 位為 0：本平台未回報微碼版本）"
                    : $"0x{(sign.Value >> 32):X8}（本核讀值）"));
            }
        }
        catch (Exception ex)
        {
            mc.Add(new FirmwareRow("微碼修訂版（0x8B）", "—（" + ex.Message + "）"));
        }
        return (virt, mc);
    }
}
