using System.IO;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// WP15 系統與軟體層：稽核政策（LSA，usermode 唯讀）與機器原則檔事實。
/// <para>
/// 稽核政策經 <c>LsaQueryInformationPolicy(PolicyAuditEventsInformation)</c>——Windows 自己
/// 記的設定，可信度 Reported。九個舊制類別的等級（0 未設定／1 成功／2 失敗／3 成功＋失敗）
/// 逐類描述、非規範等級如實標「等級 N」不猜。LSA 通路極薄（失敗回 null 標三態），
/// 描述邏輯是純函式、注入探測釘值測試。
/// </para>
/// <para>
/// 「群組原則」面：本工具不解析 Registry.pol 二進位，只如實回報機器原則檔的存在與最後寫入
/// 時間（這是「原則有沒有下到這台機器」的可量測指紋）；沒有檔＝NotSupported，不是錯誤。
/// </para>
/// </summary>
public static class AuditPolicyService
{
    private const string Category = "系統與軟體";
    private const int PolicyAuditEventsInformation = 2;
    private const uint PolicyViewLocalInformation = 0x00000001;

    private static readonly string[] CategoryNames =
    [
        "系統事件", "登入/登出", "物件存取", "特殊權限使用", "程序追蹤",
        "原則變更", "帳戶管理", "目錄服務存取", "帳戶登入",
    ];

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int LsaOpenPolicy(ref LsaUnicodeString systemName, ref LsaObjectAttributes attributes,
        uint desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int LsaQueryInformationPolicy(IntPtr policyHandle, int informationClass,
        out IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int LsaClose(IntPtr objectHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int LsaFreeMemory(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public nint RootDirectory;
    }

    /// <summary>稽核描述的純函式：總開關＋九類別等級 → 繁中摘要。等級 0 不列（未設定不是值）。</summary>
    public static string Describe(bool auditingMode, IReadOnlyList<int> options)
    {
        var parts = new List<string?>();   // 0＝未設定 的分支本來就是 null；下方 Where 過濾
        for (int i = 0; i < CategoryNames.Length && i < options.Count; i++)
        {
            parts.Add(options[i] switch
            {
                1 => $"{CategoryNames[i]}＝成功",
                2 => $"{CategoryNames[i]}＝失敗",
                3 => $"{CategoryNames[i]}＝成功＋失敗",
                0 => null,
                var other => $"{CategoryNames[i]}＝等級 {other}（規範外，不解讀）",
            });
        }
        var present = parts.Where(p => p is not null).Cast<string>().ToList();
        return $"稽核模式：{(auditingMode ? "開啟" : "關閉")}" +
               (present.Count > 0 ? "；" + string.Join("、", present) : "；未設定任何類別");
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<(bool Mode, int[] Options)?>? probe = null, string? registryPolPath = null)
    {
        registryPolPath ??= Path.Combine(Environment.SystemDirectory, "GroupPolicy", "Machine", "Registry.pol");
        var auditFact = AuditFact(at, (probe ?? QueryLsaAuditPolicy)());

        var gpFact = File.Exists(registryPolPath)
            ? new HardwareFact("gp.registrypol", Category, "機器原則檔（Registry.pol）",
                $"存在（最後寫入 {File.GetLastWriteTimeUtc(registryPolPath):yyyy-MM-dd HH:mm} UTC）", "",
                "C:\\Windows\\System32\\GroupPolicy\\Machine\\Registry.pol 的檔案時間戳——只指紋不解析二進位",
                FactTrustLevel.Reported, false, at, null)
            : new HardwareFact("gp.registrypol", Category, "機器原則檔（Registry.pol）", "", "",
                "C:\\Windows\\System32\\GroupPolicy\\Machine\\Registry.pol",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "沒有機器原則檔：此機器可能從未被網域或本機群組原則下過設定");

        return [auditFact, gpFact];
    }

    private static HardwareFact AuditFact(DateTimeOffset at, (bool Mode, int[] Options)? audit)
    {
        const string source = "LSA LsaQueryInformationPolicy（PolicyAuditEventsInformation，唯讀）";
        if (audit is not { } a)
            return new HardwareFact("audit.mode", Category, "稽核政策", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "LSA 查詢失敗——稽核政策讀不到就是不猜");
        return new HardwareFact("audit.mode", Category, "稽核政策", Describe(a.Mode, a.Options), "", source,
            FactTrustLevel.Reported, false, at, null);
    }

    /// <summary>LSA 通路（極薄）：查稽核總開關與九類別等級；任何失敗回 null 標三態。</summary>
    public static (bool Mode, int[] Options)? QueryLsaAuditPolicy()
    {
        IntPtr policy = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            var systemName = new LsaUnicodeString();
            var attributes = new LsaObjectAttributes();
            int rc = LsaOpenPolicy(ref systemName, ref attributes, PolicyViewLocalInformation, out policy);
            if (rc != 0) return null;
            rc = LsaQueryInformationPolicy(policy, PolicyAuditEventsInformation, out buffer);
            if (rc != 0) return null;

            // POLICY_AUDIT_EVENTS_INFO（x64）：AuditingMode ULONG @0、EventAuditOptions 指標 @8、
            // MaximumAuditEventCount ULONG @16——指標欄對齊 8，手算偏移不靠 struct layout。
            uint mode = (uint)Marshal.ReadInt32(buffer, 0);
            IntPtr optionsPtr = Marshal.ReadIntPtr(buffer, 8);
            int count = Marshal.ReadInt32(buffer, 16);
            if (optionsPtr == IntPtr.Zero || count is < 0 or > 64) return null;

            var options = new int[count];
            for (int i = 0; i < count; i++)
                options[i] = Marshal.ReadInt32(optionsPtr, i * 4);
            return (mode != 0, options);
        }
        catch { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) LsaFreeMemory(buffer);
            if (policy != IntPtr.Zero) LsaClose(policy);
        }
    }
}
