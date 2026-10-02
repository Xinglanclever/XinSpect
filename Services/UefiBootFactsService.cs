using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// UEFI 開機設定三態事實（V7 WP6／A10 的 UEFI 變數面）：直接問韌體（GetFirmwareEnvironmentVariableEx）
/// 取 SecureBoot／SetupMode／AuditMode／DeployedMode 與 BootOrder 筆數。價值在於它是
/// <c>platform.secure_boot</c>（登錄檔）之外的<b>第二個獨立來源</b>——兩邊對不上就是矛盾（交對帳引擎）。
/// 需要 SeSystemEnvironmentPrivilege；拿不到（或 Legacy 開機）如實三態，不推測。
/// </summary>
public static class UefiBootFactsService
{
    private const string Category = "韌體安全";
    private const string EfiGlobal = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableEx(string name, string guid, byte[]? buffer, uint size, ref uint attributes);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<string, byte?>? readByte = null, Func<uint?>? readBootOrderCount = null, bool? privilegeOk = null)
    {
        // 注入探測＝測試路徑（跳過特權啟用）；否則走真實路徑，特權拿不到就整組如實三態。
        bool hasInjected = readByte is not null || readBootOrderCount is not null || privilegeOk is not null;
        bool privileged = privilegeOk ?? (hasInjected || EnableFirmwarePrivilege());

        var read = readByte ?? (name => ReadUefiByte(name));
        var readOrder = readBootOrderCount ?? ReadBootOrderCount;

        var facts = new List<HardwareFact>();
        facts.Add(ByteFact("uefi.secure_boot", "Secure Boot（UEFI 變數）", read("SecureBoot"), at,
            privileged, optional: false));
        var setup = read("SetupMode");
        if (privileged && setup is not null)
            facts.Add(ByteFact("uefi.setup_mode", "Setup Mode（金鑰部署狀態）", setup, at, privileged,
                optional: true, onText: "金鑰未部署（Setup Mode 開啟）", offText: "金鑰已部署（Setup Mode 關閉）"));
        AddOptional(facts, "uefi.audit_mode", "Audit Mode（稽核模式）", read("AuditMode"), at, privileged);
        AddOptional(facts, "uefi.deployed_mode", "Deployed Mode（部署模式）", read("DeployedMode"), at, privileged);
        facts.Add(BootOrderFact(readOrder(), at, privileged));
        return facts;
    }

    private static HardwareFact ByteFact(string key, string name, byte? value, DateTimeOffset at,
        bool privileged, bool optional, string onText = "是", string offText = "否")
    {
        const string source = "UEFI 變數（GetFirmwareEnvironmentVariableEx）";
        if (!privileged)
            return new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Reported, false, at, null,
                FactAvailability.InsufficientPrivilege, "無法啟用 SeSystemEnvironmentPrivilege（需管理員；或本機為 Legacy 開機）");
        return value switch
        {
            null => optional
                ? new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Reported, false, at, null,
                    FactAvailability.NotApplicable, "變數不存在（本平台未提供）")
                : new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Reported, false, at, null,
                    FactAvailability.NotSupported, "變數不存在：可能是 Legacy 開機或 CSM 模式"),
            1 => new HardwareFact(key, Category, name, onText, "", source, FactTrustLevel.Reported, false, at, 1),
            0 => new HardwareFact(key, Category, name, offText, "", source, FactTrustLevel.Reported, false, at, 0),
            _ => new HardwareFact(key, Category, name, $"未知值 {value}", "", source, FactTrustLevel.Reported, false, at),
        };
    }

    private static void AddOptional(List<HardwareFact> facts, string key, string name, byte? value,
        DateTimeOffset at, bool privileged)
    {
        if (privileged && value is not null) facts.Add(ByteFact(key, name, value, at, privileged, optional: true));
    }

    private static HardwareFact BootOrderFact(uint? count, DateTimeOffset at, bool privileged)
    {
        const string key = "uefi.boot_order_count", name = "開機項目數（BootOrder）",
            source = "UEFI 變數 BootOrder（2 位元組一項）";
        if (!privileged)
            return new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Reported, false, at, null,
                FactAvailability.InsufficientPrivilege, "無法啟用 SeSystemEnvironmentPrivilege（需管理員；或本機為 Legacy 開機）");
        return count is null
            ? new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Reported, false, at, null,
                FactAvailability.NotSupported, "BootOrder 讀取失敗或不存在")
            : new HardwareFact(key, Category, name, count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "項", source, FactTrustLevel.Reported, false, at, count.Value);
    }

    private static byte? ReadUefiByte(string name)
    {
        try
        {
            var buf = new byte[4];
            uint attr = 0;
            uint ret = GetFirmwareEnvironmentVariableEx(name, EfiGlobal, buf, (uint)buf.Length, ref attr);
            return ret > 0 ? buf[0] : null;
        }
        catch { return null; }
    }

    private static uint? ReadBootOrderCount()
    {
        try
        {
            var buf = new byte[128];
            uint attr = 0;
            uint ret = GetFirmwareEnvironmentVariableEx("BootOrder", EfiGlobal, buf, (uint)buf.Length, ref attr);
            return ret > 0 && ret % 2 == 0 ? ret / 2 : null;
        }
        catch { return null; }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges newState,
        uint bufferLength, nint previousState, nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008, SePrivilegeEnabled = 0x0002;

    private struct Luid { public uint LowPart; public int HighPart; }

    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    /// <summary>啟用 SeSystemEnvironmentPrivilege。未持有該權限時 AdjustTokenPrivileges 仍回 true 但 LastError=1300——必須查，否則假成功。</summary>
    private static bool EnableFirmwarePrivilege()
    {
        try
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                TokenAdjustPrivileges | TokenQuery, out nint token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out Luid luid))
                    return false;
                var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                return AdjustTokenPrivileges(token, false, ref tp, 0, nint.Zero, nint.Zero)
                       && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }
}
