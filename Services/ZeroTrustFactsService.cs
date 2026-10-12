using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Security.Cryptography.X509Certificates;

namespace XinSpect;

/// <summary>
/// 零信任、資料安全與進階稽核事實（Vol2 推進第 67–74 項）：ZR-001 MDM 註冊、ZR-002 Entra 加入、
/// ZR-007 遠端抹除能力、DS-005 憑證店時間線、SE-005 進階稽核政策、DL-005 行程命令列（L2 opt-in）、
/// HM-005 感測器交叉漂移、MU-008 HDR 影片就緒。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>，且預設只陳述計數與狀態，不搬個人識別資料：UPN／裝置名稱不進值；
/// 憑證只報「store × 時間窗」的數量，不列主體名稱；行程命令列預設<b>不讀</b>（L2 明示同意才讀，
/// 且值標敏感讓匯出預設遮蔽）。</para>
/// <para><b>界線</b>：MDM／Entra 的「沒有」是讀登錄得到的結果，不是「讀不到」；
/// 遠端抹除只聲明「有沒有管理通道」，本工具永不會觸發抹除；稽核政策原樣引用 auditpol 輸出、
/// 不解析在地化文字；感測器交叉是「彼此差多少」，不是「哪一顆準」。</para>
/// </remarks>
public static class ZeroTrustFactsService
{
    private const string CatZr = "零信任";
    private const string CatDs = "資料安全深化";
    private const string CatSe = "進階安全";
    private const string CatDl = "開發者診斷";
    private const string CatHm = "硬體事件史";
    private const string CatMu = "多媒體";

    public const string MdmKey = "zr.mdm";
    public const string AadjKey = "zr.aadj";
    public const string WipeKey = "zr.wipe";
    public const string CertCountKey = "ds.cert.count";
    public const string CertSummaryKey = "ds.cert.summary";
    public const string Audit2Key = "se.audit2.summary";
    public const string CmdCountKey = "dl.cmdline.count";
    public const string CmdSummaryKey = "dl.cmdline.summary";
    public const string SensDriftKey = "hm.sensdrift";
    public const string HdrKey = "mu.hdr";

    // ── 注入接縫 ─────────────────────────────────────────────────────────

    /// <summary>MDM 註冊條目（UPN 刻意不進事實，故不在此型別）。</summary>
    public readonly record struct MdmEnrollment(string ProviderId, int State);

    /// <summary>憑證一筆（只取時間窗需要的欄位，不取主體名稱）。</summary>
    public readonly record struct CertEntry(string Store, DateTime NotBefore, DateTime NotAfter);

    public delegate IReadOnlyList<MdmEnrollment>? MdmProbe();
    public delegate bool? RegistryFlagProbe(string path, string valueName);
    public delegate IReadOnlyList<CertEntry>? CertProbe();
    public delegate string? CommandRunner(string[] args);
    public delegate IReadOnlyList<(string Process, string CommandLine)>? CmdlineProbe();
    public delegate IReadOnlyList<(string Name, double Celsius)>? TempProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        MdmProbe? mdm = null,
        RegistryFlagProbe? flags = null,
        CertProbe? certs = null,
        CommandRunner? runner = null,
        CmdlineProbe? cmdlines = null,
        TempProbe? temps = null,
        bool cmdlineOptedIn = false)
    {
        var facts = new List<HardwareFact>(9);
        var (mdmFact, enrolled) = MdmFact(at, mdm ?? ReadMdm);
        facts.Add(mdmFact);
        facts.Add(AadjFact(at, flags ?? ReadRegistryFlag));
        facts.Add(WipeFact(at, enrolled, flags ?? ReadRegistryFlag));
        facts.AddRange(CertTimelineFacts(at, certs ?? ReadCerts));
        facts.Add(AuditAdvancedFact(at, runner ?? RunAuditPol));
        facts.AddRange(CmdlineFacts(at, cmdlineOptedIn, cmdlines ?? ReadCmdlines));
        facts.Add(SensDriftFact(at, temps ?? ReadTemps));
        facts.Add(HdrFact(at, flags ?? ReadRegistryFlag));
        return facts;
    }

    // ── ZR-001／002／007 ────────────────────────────────────────────────

    private static (HardwareFact Fact, bool Enrolled) MdmFact(DateTimeOffset at, MdmProbe probe)
    {
        IReadOnlyList<MdmEnrollment>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
            return (new HardwareFact(MdmKey, CatZr, "MDM 註冊狀態", "", "", @"HKLM\SOFTWARE\Microsoft\Enrollments",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "MDM 註冊登錄讀不到——不假裝「未註冊」也不假裝「已註冊」"), false);

        int enrolled = list.Count(e => e.State == 3);
        string providers = list.Count == 0
            ? "沒有註冊條目"
            : string.Join("、", list.GroupBy(e => e.ProviderId).Select(g => $"{g.Key}×{g.Count()}"));
        return (new HardwareFact(MdmKey, CatZr, "MDM 註冊狀態",
            $"EnrollmentState=3（已註冊）的有 {enrolled} 筆；條目 {list.Count} 筆・提供者：{providers}。" +
            "界線：只看註冊狀態，不含使用者識別（UPN 不進值）；已註冊不等於政策都套用成功。",
            "筆", @"HKLM\SOFTWARE\Microsoft\Enrollments\*（EnrollmentState／ProviderID）",
            FactTrustLevel.Reported, false, at, enrolled), enrolled > 0);
    }

    private static HardwareFact AadjFact(DateTimeOffset at, RegistryFlagProbe flags)
    {
        const string aadPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AAD";
        const string wpjPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WorkplaceJoin";
        bool? aad;
        bool? wpj;
        try { aad = flags(aadPath, "JoinInfo"); } catch { aad = null; }
        try { wpj = flags(wpjPath, "JoinInfo"); } catch { wpj = null; }

        if (aad is null && wpj is null)
            return new HardwareFact(AadjKey, CatZr, "Entra 加入狀態", "", "", @"HKLM\...\AAD＋WorkplaceJoin",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "AAD／WorkplaceJoin 登錄讀不到——不推測加入狀態");

        string text = (aad == true, wpj == true) switch
        {
            (true, _) => "Entra（Azure AD）裝置已加入（AAD\\JoinInfo 存在）",
            (false, true) => "未做 Entra 裝置加入，但有 Workplace Join（工作場所加入）紀錄",
            _ => "未加入 Entra，也沒有 Workplace Join 紀錄（本機為獨立／僅網域環境）",
        };
        return new HardwareFact(AadjKey, CatZr, "Entra 加入狀態",
            text + "。界線：只陳述加入狀態；租用戶 ID、使用者 UPN 等識別資訊刻意不讀。",
            "", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AAD ／ WorkplaceJoin",
            FactTrustLevel.Reported, false, at);
    }

    private static HardwareFact WipeFact(DateTimeOffset at, bool mdmEnrolled, RegistryFlagProbe flags)
    {
        bool? findMyDevice;
        try { findMyDevice = flags(@"SOFTWARE\Microsoft\Windows\CurrentVersion\FindMyDevice", "Enabled"); }
        catch { findMyDevice = null; }

        string text;
        if (mdmEnrolled)
            text = "有 MDM 管理通道——企業端可經管理服務下達遠端抹除（能力在管理服務，不在本工具）";
        else if (findMyDevice == true)
            text = "沒有 MDM，但 Find My Device 已啟用（Microsoft 帳戶可定位／鎖定／抹除）";
        else if (findMyDevice is null)
            text = "沒有 MDM；Find My Device 設定讀不到——遠端抹除能力無法判定（不推測）";
        else
            text = "沒有 MDM、Find My Device 未啟用——本機目前沒有雲端遠端抹除通道";

        return new HardwareFact(WipeKey, CatZr, "遠端抹除能力",
            text + "。界線：<b>本工具永不觸發抹除</b>；這一行只聲明「誰有能力這麼做」。",
            "", @"HKLM\...\Enrollments（MDM）＋ HKLM\...\FindMyDevice（Enabled）",
            FactTrustLevel.Reported, false, at);
    }

    private static IReadOnlyList<MdmEnrollment>? ReadMdm()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
            if (root is null) return [];
            var list = new List<MdmEnrollment>();
            foreach (string name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                object? provider = sub?.GetValue("ProviderID");
                object? state = sub?.GetValue("EnrollmentState");
                if (state is int s)
                    list.Add(new MdmEnrollment(provider as string ?? "(未回報)", s));
            }
            return list;
        }
        catch { return null; }
    }

    private static bool? ReadRegistryFlag(string path, string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path.Replace(@"HKLM\", "").Replace(@"SOFTWARE\", @"SOFTWARE\"));
            if (key is null) return false;
            object? v = key.GetValue(valueName);
            return v switch { int i => i != 0, null => key.SubKeyCount > 0, _ => true };
        }
        catch { return null; }
    }

    // ── DS-005 憑證店時間線 ─────────────────────────────────────────────

    private static IReadOnlyList<HardwareFact> CertTimelineFacts(DateTimeOffset at, CertProbe probe)
    {
        IReadOnlyList<CertEntry>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
        {
            const string why = "憑證店列舉失敗——讀不到就是不猜";
            return
            [
                new HardwareFact(CertCountKey, CatDs, "憑證數", "", "", "X509Store", FactTrustLevel.Unknown, false, at,
                    null, FactAvailability.ReadError, why),
                new HardwareFact(CertSummaryKey, CatDs, "憑證時間線", "", "", "X509Store", FactTrustLevel.Unknown, false, at,
                    null, FactAvailability.ReadError, why),
            ];
        }

        var now = at.LocalDateTime;
        var expiring = list.Count(c => c.NotAfter > now && c.NotAfter <= now.AddDays(90));
        var expired = list.Count(c => c.NotAfter <= now);
        var added = list.Count(c => c.NotBefore >= now.AddDays(-90) && c.NotBefore <= now && c.NotAfter > now);
        string stores = string.Join("、", list.GroupBy(c => c.Store).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}×{g.Count()}"));
        return
        [
            new HardwareFact(CertCountKey, CatDs, "憑證數",
                list.Count.ToString(CultureInfo.InvariantCulture), "張", "X509Store（本機與目前使用者）",
                FactTrustLevel.Measured, false, at, list.Count),
            new HardwareFact(CertSummaryKey, CatDs, "憑證時間線",
                $"共 {list.Count} 張（{stores}）・90 天內到期 {expiring}・已過期 {expired}・90 天內新增 {added}。" +
                "界線：只報「store × 時間窗」的數量，不列主體名稱與指紋（L2）；到期是時間事實，不是安全判決。",
                "張", "X509Store（Root／CA／My／Disallowed；只取時間欄位）",
                FactTrustLevel.Measured, false, at, list.Count),
        ];
    }

    private static IReadOnlyList<CertEntry>? ReadCerts()
    {
        try
        {
            var list = new List<CertEntry>();
            foreach (var loc in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
                foreach (var name in new[] { "Root", "CA", "My", "Disallowed" })
                {
                    try
                    {
                        using var store = new X509Store(name, loc);
                        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                        foreach (var c in store.Certificates)
                            list.Add(new CertEntry($"{loc}/{name}", c.NotBefore, c.NotAfter));
                    }
                    catch { /* 單一 store 讀不到不影響其他 store */ }
                }
            return list;
        }
        catch { return null; }
    }

    // ── SE-005 進階稽核政策（原樣輸出，不解析在地化）────────────────────

    private static HardwareFact AuditAdvancedFact(DateTimeOffset at, CommandRunner runner)
    {
        string[] args = ["/get", "/category:*"];
        string? output = runner(args);
        if (output is null)
            return new HardwareFact(Audit2Key, CatSe, "進階稽核政策", "", "", "auditpol.exe /get /category:*",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "auditpol.exe 無法執行（找不到命令或逾時）——讀不到就是不猜");

        string text = output.Trim();
        if (text.Length == 0) text = "（沒有輸出）";
        int lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return new HardwareFact(Audit2Key, CatSe, "進階稽核政策",
            $"auditpol /get /category:* 原樣輸出（{lines} 行）：" + (text.Length > 500 ? text[..500] + "…" : text) +
            "。界線：文字随系統語言翻譯，本工具不解析在地化文字——命令照抄，使用者可自己再跑一次核對。",
            "行", "auditpol.exe /get /category:*（OS 內建命令）", FactTrustLevel.Reported, false, at, lines);
    }

    private static string? RunAuditPol(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("auditpol.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 不指定 Encoding：讓 .NET 用系統字碼頁（與 auditpol 輸出一致）
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return null; }
            return string.IsNullOrWhiteSpace(stdout) ? "" : stdout;
        }
        catch { return null; }
    }

    // ── DL-005 行程命令列（L2 opt-in）───────────────────────────────────

    private static IReadOnlyList<HardwareFact> CmdlineFacts(DateTimeOffset at, bool optedIn, CmdlineProbe probe)
    {
        if (!optedIn)
        {
            const string why = "未明示同意（L2）——行程命令列可能含檔案路徑、參數與權杖；預設不讀";
            return
            [
                new HardwareFact(CmdCountKey, CatDl, "行程命令列數", "", "", "Process／WMI 命令列", FactTrustLevel.Unknown,
                    false, at, null, FactAvailability.NotApplicable, why),
                new HardwareFact(CmdSummaryKey, CatDl, "行程命令列清單", "", "", "Process／WMI 命令列", FactTrustLevel.Unknown,
                    false, at, null, FactAvailability.NotApplicable, why),
            ];
        }

        IReadOnlyList<(string Process, string CommandLine)>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
        {
            const string why = "已同意但命令列列舉失敗——讀不到就是不猜";
            return
            [
                new HardwareFact(CmdCountKey, CatDl, "行程命令列數", "", "", "Process／WMI 命令列",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
                new HardwareFact(CmdSummaryKey, CatDl, "行程命令列清單", "", "", "Process／WMI 命令列",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, why),
            ];
        }

        var ordered = list.Where(x => !string.IsNullOrWhiteSpace(x.Process)).OrderBy(x => x.Process, StringComparer.OrdinalIgnoreCase).ToList();
        return
        [
            new HardwareFact(CmdCountKey, CatDl, "行程命令列數",
                ordered.Count.ToString(CultureInfo.InvariantCulture), "條", "Process／WMI 命令列（明示同意後）",
                FactTrustLevel.Reported, false, at, ordered.Count),
            // 明示同意才讀，且屬 L2：值標敏感，匯出預設遮蔽
            new HardwareFact(CmdSummaryKey, CatDl, "行程命令列清單",
                ordered.Count == 0 ? "（沒有可讀的命令列）"
                    : string.Join("；", ordered.Take(20).Select(x => $"{x.Process}：{x.CommandLine}")) +
                      (ordered.Count > 20 ? $"…等 {ordered.Count - 20} 條" : ""),
                "", "Process／WMI 命令列（明示同意後；L2）", FactTrustLevel.Reported, true, at, ordered.Count),
        ];
    }

    private static IReadOnlyList<(string, string)>? ReadCmdlines()
    {
        try
        {
            var list = new List<(string, string)>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try { list.Add((p.ProcessName, p.MainModule?.FileName ?? "(無主模組)")); }
                    catch { list.Add((p.ProcessName, "(讀不到)")); }
                }
            }
            return list;
        }
        catch { return null; }
    }

    // ── HM-005 溫度感測器交叉 ───────────────────────────────────────────

    private static HardwareFact SensDriftFact(DateTimeOffset at, TempProbe probe)
    {
        IReadOnlyList<(string Name, double Celsius)>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
            return new HardwareFact(SensDriftKey, CatHm, "感測器校準漂移", "", "", "MSAcpi_ThermalZoneTemperature",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "溫度感測點讀不到——讀不到就是不猜");
        if (list.Count < 2)
            return new HardwareFact(SensDriftKey, CatHm, "感測器校準漂移", "", "", "MSAcpi_ThermalZoneTemperature",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                $"只有 {list.Count} 個溫度感測點——少於兩個就無法交叉比對（不適用不是錯誤）");

        double min = list.Min(x => x.Celsius), max = list.Max(x => x.Celsius);
        var sorted = list.OrderBy(x => x.Celsius).ToList();
        return new HardwareFact(SensDriftKey, CatHm, "感測器校準漂移",
            $"{list.Count} 個感測點：{string.Join("、", sorted.Select(x => $"{x.Name} {x.Celsius:F1}°C"))}；" +
            $"最低 {min:F1}°C、最高 {max:F1}°C、跨幅 {max - min:F1}°C。" +
            "界線：跨幅大是<b>事實</b>不是判決——不同位置的感測點本來就會不同，本項不推斷哪一顆失準。",
            "°C", "MSAcpi_ThermalZoneTemperature（ACPI 熱區）", FactTrustLevel.Measured, false, at, max - min);
    }

    private static IReadOnlyList<(string Name, double Celsius)>? ReadTemps()
    {
        try
        {
            var list = new List<(string, double)>();
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    if (o["CurrentTemperature"] is not { } v) continue;
                    double tenthKelvin = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    list.Add((o["InstanceName"] as string ?? "熱區", tenthKelvin / 10.0 - 273.15));
                }
            }
            return list;
        }
        catch { return null; }
    }

    // ── MU-008 HDR 影片就緒 ─────────────────────────────────────────────

    private static HardwareFact HdrFact(DateTimeOffset at, RegistryFlagProbe flags)
    {
        bool? enabled;
        try { enabled = flags(@"SOFTWARE\Microsoft\Windows\CurrentVersion\VideoSettings", "EnableHDRForPlayback"); }
        catch { enabled = null; }
        if (enabled is null)
            return new HardwareFact(HdrKey, CatMu, "HDR 影片就緒", "", "", @"HKLM\...\VideoSettings",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "VideoSettings\\EnableHDRForPlayback 讀不到——此平台未回報 HDR 影片設定（不推測）");
        return new HardwareFact(HdrKey, CatMu, "HDR 影片就緒",
            enabled == true
                ? "HDR 影片播放已啟用（EnableHDRForPlayback=1）；顯示器與 GPU 是否隨時可用仍要看當下輸出，本項只陳述設定。"
                : "HDR 影片播放未啟用（EnableHDRForPlayback=0 或不存在）。",
            "", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\VideoSettings\EnableHDRForPlayback",
            FactTrustLevel.Reported, false, at, enabled == true ? 1 : 0);
    }
}