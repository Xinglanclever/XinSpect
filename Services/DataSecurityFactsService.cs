using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace XinSpect;

/// <summary>
/// 資料安全深化事實（Vol2 推進第 75–83 項）：DS-003 EFS 憑證盤點、DS-004 Windows Hello 註冊存在性、
/// DS-006 憑證自動註冊、DS-007 自簽憑證、DS-008 DRM／PlayReady、DS-009 TLS 版本停用、
/// DS-010 Smart App Control、DS-011 裝置安裝限制政策、DS-012 資料安全摘要。
/// </summary>
/// <remarks>
/// <para><b>紅線</b>：生物特徵永不讀——只數「有幾筆註冊項」；憑證只取時間與 store，不列主體名稱與指紋（L2）；
/// 政策值原樣列出並附登錄出處，不做「合規」判決。</para>
/// <para><b>DS-001／DS-002 為何不在本批</b>：BitLocker 卷級與 DHA 需要 WMI 磁碟加密提供者與 TPM attest 通路，
/// 本機尚未驗證；列入後續批次而不是假裝做完。</para>
/// </remarks>
public static class DataSecurityFactsService
{
    private const string CatDs = "資料安全深化";

    public const string EfsKey = "ds.efs.count";
    public const string HelloKey = "ds.hello.count";
    public const string AutoKey = "ds.cert.auto";
    public const string SelfSignedKey = "ds.selfsigned.count";
    public const string DrmKey = "ds.drm";
    public const string TlsKey = "ds.tls.summary";
    public const string SacKey = "ds.sac";
    public const string DevPolKey = "ds.devpol.summary";
    public const string SummaryKey = "ds.summary";

    /// <summary>憑證一筆（只取本批需要欄位；不取主體名稱與指紋）。</summary>
    public readonly record struct CertInfo(string Store, DateTime NotAfter, bool IsSelfSigned);

    public delegate IReadOnlyList<CertInfo>? CertProbe();
    public delegate IReadOnlyList<string>? HelloProbe();
    public delegate bool? FlagProbe(string path, string valueName);
    public delegate bool FileExistsProbe(string path);
    public delegate int? IntProbe(string path, string valueName);
    public delegate IReadOnlyList<(string Name, string State)>? PolicyProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        CertProbe? certs = null,
        HelloProbe? hello = null,
        FlagProbe? flags = null,
        FileExistsProbe? fileExists = null,
        IntProbe? intFlags = null,
        PolicyProbe? policies = null)
    {
        var facts = new List<HardwareFact>(9);
        facts.Add(EfsFact(at, certs ?? ReadCerts));
        facts.Add(HelloFact(at, hello ?? ReadHello));
        facts.Add(AutoEnrollFact(at, flags ?? ReadFlag));
        facts.Add(SelfSignedFact(at, certs ?? ReadCerts));
        facts.Add(DrmFact(at, flags ?? ReadFlag));
        facts.Add(TlsFact(at, flags ?? ReadFlag));
        facts.Add(SacFact(at, fileExists ?? File.Exists, intFlags ?? ReadInt));
        facts.AddRange(DevPolFacts(at, policies ?? ReadDeviceInstallPolicies));
        facts.Add(SummaryFact(at, facts));
        return facts;
    }

    private static HardwareFact EfsFact(DateTimeOffset at, CertProbe probe)
    {
        IReadOnlyList<CertInfo>? certs;
        try { certs = probe(); }
        catch { certs = null; }
        if (certs is null)
            return new HardwareFact(EfsKey, CatDs, "EFS 憑證數", "", "", "X509Store（EFS 1.0／.FileEncryption）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "憑證店列舉失敗——讀不到就是不猜（不是「沒有 EFS 憑證」）");
        int efs = certs.Count(c => c.Store.Contains("EFS", StringComparison.OrdinalIgnoreCase)
                                   || c.Store.Contains("FileEncryption", StringComparison.OrdinalIgnoreCase));
        return new HardwareFact(EfsKey, CatDs, "EFS 憑證數",
            efs.ToString(System.Globalization.CultureInfo.InvariantCulture), "張",
            "X509Store（EFS 1.0／.FileEncryption）", FactTrustLevel.Measured, false, at, efs);
    }

    private static HardwareFact HelloFact(DateTimeOffset at, HelloProbe probe)
    {
        IReadOnlyList<string>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
            return new HardwareFact(HelloKey, CatDs, "Windows Hello 註冊", "", "", @"HKLM\...\WinBio\DatabaseStore",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "Hello 註冊清單讀不到——不假裝「未註冊」也不猜是臉還是指紋");
        return new HardwareFact(HelloKey, CatDs, "Windows Hello 註冊",
            $"{list.Count} 筆註冊項。生物特徵永不讀：本工具不讀臉／指紋樣本、也不碰樣本檔——只看「有幾筆註冊」。",
            "筆", @"HKLM\SOFTWARE\Microsoft\WinBio\DatabaseStore（數量，不是內容）",
            FactTrustLevel.Reported, false, at, list.Count);
    }

    private static HardwareFact AutoEnrollFact(DateTimeOffset at, FlagProbe flags)
    {
        const string path = @"SOFTWARE\Policies\Microsoft\Cryptography\AutoEnrollment";
        bool? auto;
        try { auto = flags(path, "EnableAutoEnrollment"); }
        catch { auto = null; }
        if (auto is null)
            return new HardwareFact(AutoKey, CatDs, "憑證自動註冊", "", "", @"HKLM\" + path,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "AutoEnrollment 政策鍵讀取失敗——不推測");
        return new HardwareFact(AutoKey, CatDs, "憑證自動註冊",
            auto == true
                ? "群組原則已啟用憑證自動註冊（EnableAutoEnrollment=1）——申請與續期交給政策，不表示已成功註冊任何憑證。"
                : "群組原則未啟用憑證自動註冊（或無政策）——「沒有政策」不等於「不會自動註冊」，憑證範本另由 AD／本機管理。",
            "", @"HKLM\" + path + @"\EnableAutoEnrollment", FactTrustLevel.Reported, false, at, auto == true ? 1 : 0);
    }

    private static HardwareFact SelfSignedFact(DateTimeOffset at, CertProbe probe)
    {
        IReadOnlyList<CertInfo>? certs;
        try { certs = probe(); }
        catch { certs = null; }
        if (certs is null)
            return new HardwareFact(SelfSignedKey, CatDs, "自簽憑證數", "", "", "X509Store（IsSelfSigned）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "憑證店列舉失敗——讀不到就是不猜");
        int n = certs.Count(c => c.IsSelfSigned);
        return new HardwareFact(SelfSignedKey, CatDs, "自簽憑證數",
            $"{n} 張自簽憑證（Subject＝Issuer）。界線：自簽是簽章型態的事實、不是危險判決——開發與本機 CA 常態會有；" +
            "要評估的是「出現在錯的 store」而不是「自簽」本身。",
            "張", "X509Certificate2.IsSelfSigned（僅計數，不列主體）", FactTrustLevel.Measured, false, at, n);
    }

    private static HardwareFact DrmFact(DateTimeOffset at, FlagProbe flags)
    {
        const string path = @"SOFTWARE\Microsoft\PlayReady";
        bool? present;
        try { present = flags(path, ""); }
        catch { present = null; }
        if (present is null)
            return new HardwareFact(DrmKey, CatDs, "DRM（PlayReady）", "", "", @"HKLM\" + path,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "PlayReady 登錄讀取失敗——不推測");
        return new HardwareFact(DrmKey, CatDs, "DRM（PlayReady）",
            present == true
                ? "PlayReady 已在本機初始化（登錄有資料）——授權與撤銷狀態屬 DRM 管理，本工具不碰。"
                : "PlayReady 尚未初始化（沒有登錄資料）——串流 DRM 內容時才會建立。",
            "", @"HKLM\" + path + "（鍵存在性）", FactTrustLevel.Reported, false, at, present == true ? 1 : 0);
    }

    private static HardwareFact TlsFact(DateTimeOffset at, FlagProbe flags)
    {
        const string root = @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols";
        var disabled = new List<string>();
        foreach (string version in new[] { "SSL 2.0", "SSL 3.0", "TLS 1.0", "TLS 1.1" })
            foreach (string side in new[] { "Client", "Server" })
                try { if (flags($"{root}\\{version}\\{side}", "Enabled") == false) disabled.Add($"{version} {side}"); }
                catch { }
        if (disabled.Count == 0)
            return new HardwareFact(TlsKey, CatDs, "TLS 版本停用", "", "", @"HKLM\" + root,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                "SCHANNEL Protocols 沒有任何「明確停用」記錄——這是登錄層事實：Windows 10 2004+／11 的預設停用行為" +
                "不寫在這裡，本工具不假裝「已停用」也不假裝「全部啟用」");
        return new HardwareFact(TlsKey, CatDs, "TLS 版本停用",
            $"明確停用的協定／方向：{string.Join("、", disabled)}。界線：登錄只反映「有明文停用」的部分；" +
            "未列的版本不代表啟用（要看 OS 預設），停用是防禦事實、不是安全背書。",
            "項", @"HKLM\" + root + "（Enabled=0）", FactTrustLevel.Reported, false, at, disabled.Count);
    }

    private static HardwareFact SacFact(DateTimeOffset at, FileExistsProbe fileExists, IntProbe flags)
    {
        bool sacExe;
        int? state;
        try { sacExe = fileExists(@"C:\Windows\System32\SmartAppControl.exe"); } catch { sacExe = false; }
        try { state = flags(@"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState"); }
        catch { state = null; }

        if (!sacExe && state is null)
            return new HardwareFact(SacKey, CatDs, "Smart App Control", "", "", "SmartAppControl.exe＋CI\\Policy",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "此 Windows 版本沒有 Smart App Control（22H2 之後才有）——不適用，不是「已停用」");
        string text = state switch
        {
            2 => "SAC 已啟用（VerifiedAndReputablePolicyState=2）——只跑信譽良好的應用程式（判定在微軟雲端，本工具不參與）",
            1 => "SAC 評估模式（=1）——只記錄不阻擋，等待微軟決定是否轉正式",
            0 => "SAC 已關閉（=0）——微軟設計上關閉後需重裝系統才能再開，不是本工具的限制",
            _ => $"SAC 狀態值未收錄（{state}）——如實列出，不猜意義",
        };
        return new HardwareFact(SacKey, CatDs, "Smart App Control", text, "",
            @"SmartAppControl.exe＋HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState",
            FactTrustLevel.Reported, false, at, state is int i ? i : null);
    }

    private static IReadOnlyList<HardwareFact> DevPolFacts(DateTimeOffset at, PolicyProbe probe)
    {
        IReadOnlyList<(string Name, string State)>? list;
        try { list = probe(); }
        catch { list = null; }
        if (list is null)
        {
            return
            [
                new HardwareFact(DevPolKey, CatDs, "裝置安裝限制政策", "", "",
                    @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "裝置安裝政策列舉失敗——讀不到就是不猜"),
            ];
        }
        string text = list.Count == 0
            ? "沒有裝置安裝限制政策（0 是值、不是缺席）——沒有政策約束裝置安裝"
            : string.Join("；", list.Select(x => $"{x.Name}：{x.State}"));
        return
        [
            new HardwareFact(DevPolKey, CatDs, "裝置安裝限制政策",
                text + "。界線：只陳述政策存在與值，不推斷「防得住誰」。",
                "條", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions",
                FactTrustLevel.Reported, false, at, list.Count),
        ];
    }

    private static HardwareFact SummaryFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        var ds = facts.Where(f => f.Category == CatDs).ToList();
        int present = ds.Count(f => f.Availability == FactAvailability.Present);
        int na = ds.Count(f => f.Availability == FactAvailability.NotApplicable);
        int other = ds.Count - present - na;
        return new HardwareFact(SummaryKey, CatDs, "資料安全摘要",
            $"資料安全面 {ds.Count} 項：可陳述 {present}、不適用 {na}、其他 {other}。" +
            "界線：這是可用性計數、不是安全分數——每項的判定理由在各自的事實裡。",
            "項", "本組事實的可用性（輸入不含自己）", FactTrustLevel.Derived, false, at, present);
    }

    // ── 真實通路 ────────────────────────────────────────────────────────

    private static IReadOnlyList<CertInfo>? ReadCerts()
    {
        try
        {
            var list = new List<CertInfo>();
            foreach (var loc in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
                foreach (string name in new[] { "Root", "CA", "My", "Disallowed", "TrustedPeople", "EFS 1.0", ".FileEncryption" })
                {
                    try
                    {
                        using var store = new X509Store(name, loc);
                        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                        foreach (var c in store.Certificates)
                            list.Add(new CertInfo($"{loc}/{name}", c.NotAfter, c.SubjectName.Name == c.IssuerName.Name));
                    }
                    catch { }
                }
            return list;
        }
        catch { return null; }
    }

    private static IReadOnlyList<string>? ReadHello()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WinBio\DatabaseStore");
            if (root is null) return [];
            var list = new List<string>();
            void Walk(Microsoft.Win32.RegistryKey key, string prefix)
            {
                foreach (string name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub is null) continue;
                    list.Add(prefix + name);
                    Walk(sub, prefix + name + "\\");
                }
            }
            Walk(root, "");
            return list;
        }
        catch { return null; }
    }

    private static int? ReadInt(string path, string valueName)
    {
        try
        {
            if (path.StartsWith(@"HKLM", StringComparison.OrdinalIgnoreCase)) path = path[5..];
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return null;
            object? v = key.GetValue(valueName);
            return v is int i ? i : null;
        }
        catch { return null; }
    }

    private static bool? ReadFlag(string path, string valueName)
    {
        try
        {
            if (path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)) path = path[5..];
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return false;
            if (string.IsNullOrEmpty(valueName)) return key.SubKeyCount > 0 || key.GetValueNames().Length > 0;
            object? v = key.GetValue(valueName);
            return v switch { int i => i != 0, null => false, _ => true };
        }
        catch { return null; }
    }

    private static IReadOnlyList<(string, string)>? ReadDeviceInstallPolicies()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions");
            if (root is null) return [];
            var list = new List<(string, string)>();
            foreach (string name in root.GetValueNames())
            {
                object? v = root.GetValue(name);
                list.Add((name, v switch { int i => i.ToString(), string s => s, _ => "(未收錄型別)" }));
            }
            foreach (string name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                if (sub is null) continue;
                foreach (string vn in sub.GetValueNames())
                {
                    object? v = sub.GetValue(vn);
                    list.Add(($"{name}\\{vn}", v switch { int i => i.ToString(), string s => s, _ => "(未收錄型別)" }));
                }
            }
            return list;
        }
        catch { return null; }
    }
}
