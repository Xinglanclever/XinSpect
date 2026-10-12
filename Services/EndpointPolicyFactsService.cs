using System.Globalization;
using System.Management;

namespace XinSpect;

/// <summary>
/// 端點安全政策與裝置類事實（Vol2 推進第 84–93 項）：SE-006 RDP 憑證、SE-007 NTLM、SE-008 Kerberos、
/// SE-013 WinRE、ZR-003 憑證型登入、ZR-006 動態鎖定、ZR-008 合規報告、UF-004 功能更新就緒度、
/// HM-003 即插即用資源衝突、MU-004 相機裝置數。
/// </summary>
/// <remarks>
/// <para><b>全部唯讀</b>：RDP 只報「有沒有指定憑證」的形態，不匯出憑證內容；NTLM／Kerberos／動態鎖定只報政策值；
/// WinRE 只報存在與版本；相機只數裝置，<b>不開啟鏡頭、不讀任何影格</b>。</para>
/// <para><b>誠實界線</b>：政策「不存在」與「讀不到」分開——不存在是事實（NotApplicable），讀不到才是 ReadError；
/// 合規報告與就緒度是彙總陳述，不是合規判決（基準表由管理員決定，需附對照來源與快照日期）。</para>
/// </remarks>
public static class EndpointPolicyFactsService
{
    private const string CatSe = "進階安全";
    private const string CatZr = "零信任";
    private const string CatUf = "更新深化";
    private const string CatHm = "硬體事件史";
    private const string CatMu = "多媒體";

    public const string RdpCertKey = "se.rdp.cert";
    public const string NtlmKey = "se.ntlm";
    public const string KerbKey = "se.kerb.summary";
    public const string WinreKey = "se.winre.summary";
    public const string CbaKey = "zr.cba";
    public const string DynLockKey = "zr.dynlock";
    public const string ComplianceReportKey = "zr.report";
    public const string ReadyKey = "uf.ready";
    public const string ConflictKey = "hm.conflict.summary";
    public const string CameraKey = "mu.cam.count";

    /// <summary>登錄值讀取（回 null＝該值不存在；拋例外＝讀不到，兩者由呼叫端分開）。</summary>
    public delegate object? ValueProbe(string path, string valueName);

    /// <summary>登錄鍵／值存在性。</summary>
    public delegate bool KeyExistsProbe(string path, string valueName);

    public delegate IReadOnlyList<(string Name, uint ProblemCode, bool Present)>? PnpProbe();
    public delegate IReadOnlyList<(string Name, bool Present)>? CameraProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ValueProbe? values = null,
        KeyExistsProbe? keys = null,
        PnpProbe? pnp = null,
        CameraProbe? cameras = null)
    {
        var v = values ?? ReadValue;
        var k = keys ?? KeyExists;
        var facts = new List<HardwareFact>(10);
        facts.Add(RdpCertFact(at, v, k));
        facts.Add(NtlmFact(at, v));
        facts.Add(KerbFact(at, v, k));
        facts.Add(WinreFact(at, k, v));
        facts.Add(CbaFact(at, v, k));
        facts.Add(DynLockFact(at, v, k));
        facts.Add(ConflictFact(at, pnp ?? ReadPnp));
        facts.Add(CameraFact(at, cameras ?? ReadCameras));
        facts.Add(ReadyFact(at, k, v));
        facts.Add(ComplianceFact(at));
        return facts;
    }

    // ── SE-006 RDP 憑證 ──────────────────────────────────────────────────

    private static HardwareFact RdpCertFact(DateTimeOffset at, ValueProbe v, KeyExistsProbe k)
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
        bool hasThumb;
        try { hasThumb = k(path, "SSLCertificateSHA1Hash"); }
        catch { return Na(at, RdpCertKey, CatSe, "RDP 憑證狀態", "RDP 設定登錄讀取失敗——不推測憑證形態", FactAvailability.ReadError); }

        return new HardwareFact(RdpCertKey, CatSe, "RDP 憑證狀態",
            hasThumb
                ? "RDP 指定了自訂伺服器憑證（SSLCertificateSHA1Hash 存在）。界線：憑證本體未匯出、未驗證鏈；是否強制 TLS 另看 SecurityLayer。"
                : "RDP 未指定自訂憑證（用自簽的預設 RDP 憑證）。界線：自簽不等於不安全，但客戶端會看到警告；企業環境通常由 CA 指派。",
            "", $@"HKLM\{path}", FactTrustLevel.Reported, false, at, hasThumb ? 1 : 0);
    }

    // ── SE-007 NTLM ──────────────────────────────────────────────────────

    private static HardwareFact NtlmFact(DateTimeOffset at, ValueProbe v)
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Lsa";
        object? lm;
        try { lm = v(path, "LmCompatibilityLevel"); }
        catch { return Na(at, NtlmKey, CatSe, "NTLM 使用狀態", @"Lsa\LmCompatibilityLevel 讀取失敗——不推測", FactAvailability.ReadError); }

        string meaning = lm switch
        {
            0 => "0：允許 LM／NTLM（最舊、最弱）",
            1 => "1：送 LM／NTLM（相容模式）",
            2 => "2：只送 NTLM、拒絕 LM",
            3 => "3：送出並驗證 NTLM（網域預設線）",
            4 => "4：拒絕 LM、送出並驗證 NTLM",
            5 => "5：只送 NTLMv2 並驗證（企業建議值）",
            null => "未設定——預設由 Windows 決定（工作站 3／網域 member 5），本工具不代為推定",
            _ => $"未收錄值 {lm}——如實列出，不猜意義",
        };
        return new HardwareFact(NtlmKey, CatSe, "NTLM 使用狀態",
            $"LmCompatibilityLevel＝{(lm is null ? "（未設定）" : lm.ToString())}・{meaning}。" +
            "界線：政策只說明「允許用哪種驗證」，不代表本機實際上沒有 NTLM 流量（那要看網路）。",
            "", $@"HKLM\{path}\LmCompatibilityLevel", FactTrustLevel.Reported, false, at, lm is int i ? i : null);
    }

    // ── SE-008 Kerberos ──────────────────────────────────────────────────

    private static HardwareFact KerbFact(DateTimeOffset at, ValueProbe v, KeyExistsProbe k)
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Lsa\Kerberos\Parameters";
        var found = new List<string>();
        foreach (string name in new[] { "MaxTicketAge", "MaxRenewAge", "MaximumClockSkew" })
        {
            try
            {
                object? val = v(path, name);
                if (val is not null) found.Add($"{name}＝{val}（單位依 Windows 定義：時／分）");
            }
            catch { return Na(at, KerbKey, CatSe, "Kerberos 設定", "Kerberos Parameters 讀取失敗——不推測", FactAvailability.ReadError); }
        }

        if (found.Count > 0)
            return new HardwareFact(KerbKey, CatSe, "Kerberos 設定",
                "政策覆寫：" + string.Join("、", found) + "。界線：票證實際生命週期由 KDC 決定，本機政策只是其中一環。",
                "項", $@"HKLM\{path}", FactTrustLevel.Reported, false, at, found.Count);

        bool keyExists;
        try { keyExists = k(path, ""); }
        catch { keyExists = false; }
        string tail = keyExists ? "（鍵存在但無覆寫值）" : "（鍵不存在——未覆寫＝用系統預設）";
        return new HardwareFact(KerbKey, CatSe, "Kerberos 設定",
            "Kerberos Parameters 沒有政策覆寫值（0 是值、不是缺席）——沿用系統預設票證生命週期" + tail + "。",
            "", $@"HKLM\{path}" + tail, FactTrustLevel.Reported, false, at, 0);
    }

    // ── SE-013 WinRE ─────────────────────────────────────────────────────

    private static HardwareFact WinreFact(DateTimeOffset at, KeyExistsProbe k, ValueProbe v)
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\BackupRestore";
        bool exists;
        object? state = null;
        try
        {
            exists = k(@"SYSTEM\CurrentControlSet\Control\WinRE", "");
            if (exists) state = v(@"SYSTEM\CurrentControlSet\Control\WinRE", "WindowsREVersion");
        }
        catch { return Na(at, WinreKey, CatSe, "WinRE 回復環境", "WinRE 登錄讀取失敗——不推測", FactAvailability.ReadError); }

        return new HardwareFact(WinreKey, CatSe, "WinRE 回復環境",
            exists
                ? $"登錄回報存在 WinRE 控制鍵{(state is null ? "" : $"（版本 {state}）")}。界線：只陳述存在與版本；能否進入回復環境要重開機流程，本工具不觸發。"
                : $"沒有 WinRE 控制鍵（讀自 {path} 旁的 WinRE 鍵）——Reagentc /info 才是權威來源，本項不冒充它。",
            "", @"HKLM\SYSTEM\CurrentControlSet\Control\WinRE", FactTrustLevel.Reported, false, at, exists ? 1 : 0);
    }

    // ── ZR-003 憑證型登入 ────────────────────────────────────────────────

    private static HardwareFact CbaFact(DateTimeOffset at, ValueProbe v, KeyExistsProbe k)
    {
        bool smartCard;
        try
        {
            object? allow = v(@"SOFTWARE\Policies\Microsoft\Windows\System", "AllowDomainSmartcardLogon")
                ?? v(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartCard");
            smartCard = allow is int i && i == 1;
        }
        catch { return Na(at, CbaKey, CatZr, "憑證型登入", "智慧卡登入政策讀取失敗——不推測", FactAvailability.ReadError); }

        bool hasDelegation;
        try { hasDelegation = k(@"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation", ""); }
        catch { hasDelegation = false; }

        return new HardwareFact(CbaKey, CatZr, "憑證型登入",
            (smartCard, hasDelegation) switch
            {
                (true, _) => "政策要求／允許智慧卡（憑證型）登入。界線：本機有沒有讀卡機與憑證另計，這行不等於「CBA 已生效」。",
                (false, true) => "存在_credentials delegation_ 政策鍵，但智慧卡登入未強制——CBA 生效與否還取決於 CA 與卡片註冊。",
                _ => "未設定智慧卡／憑證型登入政策（0 是值、不是缺席）——本機以密碼或 PIN 為預設。",
            },
            "", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System＋CredentialsDelegation",
            FactTrustLevel.Reported, false, at, smartCard ? 1 : 0);
    }

    // ── ZR-006 動態鎖定 ──────────────────────────────────────────────────

    private static HardwareFact DynLockFact(DateTimeOffset at, ValueProbe v, KeyExistsProbe k)
    {
        object? factor;
        object? noLock;
        try
        {
            factor = v(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableGoodbye");
            noLock = v(@"SOFTWARE\Policies\Microsoft\Windows\LockScreen", "NoLockScreen");
        }
        catch { return Na(at, DynLockKey, CatZr, "動態鎖定設定", "動態鎖定登錄讀取失敗——不推測", FactAvailability.ReadError); }

        string text = factor switch
        {
            int i when i == 1 => "動態鎖定（離開時鎖定）政策為啟用",
            int i when i == 0 => "動態鎖定政策為停用",
            null => "動態鎖定未政策化——預設啟用（需相容的 proximity 藍牙裝置，硬體是否可用另計）",
            _ => $"未收錄值 {factor}——如實列出，不猜",
        };
        return new HardwareFact(DynLockKey, CatZr, "動態鎖定設定",
            text + $"・鎖定畫面政策：{(noLock is int n && n == 1 ? "已關閉（NoLockScreen=1）" : "未關閉")}。" +
            "界線：只報政策，proximity 連線與裝置能力不在本項宣稱範圍。",
            "", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\EnableGoodbye",
            FactTrustLevel.Reported, false, at, factor is int i2 ? i2 : null);
    }

    // ── UF-004 功能更新就緒度（唯讀評估）────────────────────────────────

    private static HardwareFact ReadyFact(DateTimeOffset at, KeyExistsProbe k, ValueProbe v)
    {
        object? build = null;
        bool tpmSeen = false;
        try { build = v(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber"); } catch { }
        try
        {
            using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            tpmSeen = bios is not null && (bios.GetValue("TPMPresent") as int? == 1 || bios.GetValue("SupportsTPM2") as int? == 1);
        }
        catch { tpmSeen = false; }

        return new HardwareFact(ReadyKey, CatUf, "功能更新就緒度",
            $"目前 build {build ?? "（讀不到）"}・TPM 線索{(tpmSeen ? "存在（BIOS 線索）" : "未回報")}。" +
            "界線：這是唯讀評估——升級門檻（空間、RAM、驅動）由更新服務決定，本工具不宣稱「可以升」或「不能升」。",
            "", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion＋HARDWARE\DESCRIPTION\System\BIOS",
            FactTrustLevel.Reported, false, at, tpmSeen ? 1 : 0);
    }

    // ── ZR-008 合規報告（彙總陳述，不是判決）───────────────────────────

    private static HardwareFact ComplianceFact(DateTimeOffset at)
        => new(ComplianceReportKey, CatZr, "合規報告",
            "政策面已可逐項查看：RDP 憑證、NTLM、Kerberos、WinRE、憑證型登入、動態鎖定。" +
            "界線：本工具不做合規判決——對照基準由管理員決定，且需附來源與快照日期才有意義。",
            "", "本政策組事實", FactTrustLevel.Derived, false, at);

    // ── HM-003 即插即用資源衝突 ──────────────────────────────────────────

    private static HardwareFact ConflictFact(DateTimeOffset at, PnpProbe probe)
    {
        IReadOnlyList<(string Name, uint ProblemCode, bool Present)>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
            return Na(at, ConflictKey, CatHm, "即插即用資源衝突", "PnP 問題碼列舉失敗——讀不到就是不猜", FactAvailability.ReadError);

        var conflicts = list.Where(x => x.Present && x.ProblemCode is 12 or 24 or 31).ToList();
        return new HardwareFact(ConflictKey, CatHm, "即插即用資源衝突",
            conflicts.Count == 0
                ? "沒有資源不足／無法啟動／找不到裝置類問題碼（12／24／31）——0 是值、不是缺席"
                : $"{conflicts.Count} 個：" + string.Join("；", conflicts.Take(5).Select(x => $"{x.Name}（代碼 {x.ProblemCode}）")) +
                  (conflicts.Count > 5 ? $"…等 {conflicts.Count - 5} 個" : ""),
            "個", "WMI Win32_PnPEntity（ConfigManagerErrorCode 12/24/31）", FactTrustLevel.Reported, false, at, conflicts.Count);
    }

    private static IReadOnlyList<(string, uint, bool)>? ReadPnp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity");
            var list = new List<(string, uint, bool)>();
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    uint code = o["ConfigManagerErrorCode"] is { } v ? Convert.ToUInt32(v) : 0u;
                    list.Add((o["Name"] as string ?? "(未命名)", code, code != 45));
                }
            }
            return list;
        }
        catch { return null; }
    }

    // ── MU-004 相機裝置數（不開啟鏡頭）─────────────────────────────────

    private static HardwareFact CameraFact(DateTimeOffset at, CameraProbe probe)
    {
        IReadOnlyList<(string Name, bool Present)>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
            return Na(at, CameraKey, CatMu, "相機裝置數", "相機裝置列舉失敗——讀不到就是不猜", FactAvailability.ReadError);

        int present = list.Count(x => x.Present);
        return new HardwareFact(CameraKey, CatMu, "相機裝置數",
            $"{list.Count} 個相機類裝置（在場 {present}）。界線：只數裝置，<b>不開啟鏡頭、不讀任何影格</b>。",
            "個", "WMI Win32_PnPEntity（PNPClass=Camera）", FactTrustLevel.Measured, false, at, present);
    }

    private static IReadOnlyList<(string, bool)>? ReadCameras()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='Camera'");
            var list = new List<(string, bool)>();
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    uint code = o["ConfigManagerErrorCode"] is { } v ? Convert.ToUInt32(v) : 0u;
                    list.Add((o["Name"] as string ?? "(未命名)", code != 45));
                }
            }
            return list;
        }
        catch { return null; }
    }

    private static HardwareFact Na(DateTimeOffset at, string key, string cat, string name, string reason, FactAvailability availability)
        => new(key, cat, name, "", "", "端點政策（唯讀）", FactTrustLevel.Unknown, false, at, null, availability, reason);

    // ── 真實通路 ────────────────────────────────────────────────────────

    private static object? ReadValue(string path, string valueName)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        if (key is null) throw new System.IO.IOException($"找不到登錄鍵：HKLM\\{path}");
        return key.GetValue(valueName);
    }

    private static bool KeyExists(string path, string valueName)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        if (key is null) return false;
        if (string.IsNullOrEmpty(valueName)) return key.SubKeyCount > 0 || key.GetValueNames().Length > 0;
        return key.GetValue(valueName) is not null;
    }
}
