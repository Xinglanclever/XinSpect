using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace XinSpect;

/// <summary>目前行程 token 的一眼快照（IN-004）。<b>讀不到時整個物件是 null</b>——不以 false／0 冒充。</summary>
public sealed record TokenPrivilegeSnapshot(
    bool Elevated, string IntegrityLevel, int TotalPrivileges, IReadOnlyList<string> EnabledPrivileges);

/// <summary>
/// 自我完整性的全部輸入。<b>每一項都可注入</b>：預設值走真實來源，測試餵假件——
/// 這一組事實碰的是「執行檔本身、設定檔、token、已載入模組」，在單元測試裡讀真的那些
/// 會讓結果取決於跑測試的機器。
/// </summary>
/// <param name="LoadAudit">回 null＝審計日誌讀不到（讀不到不等於「沒有基線」）。</param>
public sealed record SelfIntegrityInputs(
    string BinaryPath,
    Func<string, string?> HashFile,
    Func<string, long> SizeOf,
    string ConfigPath,
    Func<string, string?> ReadText,
    Func<IReadOnlyList<string>> LoadedModulePaths,
    Func<string, (bool? Ok, string Note)> VerifySignature,
    Func<IReadOnlyList<AuditEntry>?> LoadAudit,
    Func<TokenPrivilegeSnapshot?> ReadPrivileges)
{
    /// <summary>真實來源。設定檔與審計日誌路徑可換（CLI 與測試共用同一組預設）。</summary>
    public static SelfIntegrityInputs Real(string? configPath = null, string? auditPath = null) => new(
        Environment.ProcessPath ?? "",
        path => { try { using var s = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(s)); } catch { return null; } },
        path => { try { return new FileInfo(path).Length; } catch { return -1; } },
        configPath ?? SettingsService.FilePath,
        path => { try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; } },
        ProcessModulePaths,
        KernelModuleService.VerifyGenericAuthenticode,
        () => { try { return AuditLogService.Load(auditPath ?? AuditLogService.DefaultPath); } catch { return null; } },
        TokenPrivilegeReader.Read);

    /// <summary>本行程已載入模組的檔案路徑（單一模組讀不到只略過它，不補假值）。</summary>
    public static IReadOnlyList<string> ProcessModulePaths()
    {
        var list = new List<string>();
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                try { if (!string.IsNullOrWhiteSpace(m.FileName)) list.Add(m.FileName!); }
                catch { /* 單一模組的資訊讀不到不影響整份清單 */ }
            }
        }
        catch { /* 模組列舉失敗＝空清單；呼叫端以 0 筆如實呈現 */ }
        return list;
    }
}

/// <summary>
/// 自我完整性事實組（Vol 2 批次 A／IN-001、IN-002、IN-004、IN-007、IN-010）。
/// <para>
/// <b>為什麼要做這一組：</b>一個用來證明別人東西沒被動過的工具，自己是不是同一份檔案、
/// 設定檔有沒有被改、拿的是什麼權限、載入了哪些 DLL——這些問題在一支「工業級」工具上是
/// 門面，也是事實。四項全部<b>唯讀</b>；唯一的寫入是使用者明示的 <see cref="RecordBaseline"/>
/// （把雜湊記進既有審計日誌：append-only、雜湊鏈可驗，不新增第二套基線儲存）。
/// </para>
/// <para>
/// <b>界線（寫在程式碼裡，也寫在畫面的值裡）：</b>雜湊回答的是「這份檔案現在是什麼」，
/// <b>不是防篡改保證</b>——有權限的人可以改檔再重算雜湊，也可以直接改審計日誌。
/// 側載候選<b>不是惡意判決</b>：外掛、第三方函式庫、驅動工具的 DLL 常態就在使用者目錄下。
/// </para>
/// </summary>
public static class SelfIntegrityFactsService
{
    public const string Category = "自我完整性";

    public const string SelfKey = "in.self";
    public const string ConfigKey = "in.cfg.integrity";
    public const string PrivsKey = "in.privs";
    public const string DepsKey = "in.deps";
    public const string ReportKey = "in.report";

    /// <summary>清單類值裡最多列幾項（其餘以「等 N 項」收束）。</summary>
    private const int ListedMax = 5;

    /// <summary>審計日誌裡「自身二進位雜湊」的範圍標記。</summary>
    public const string SelfAuditScope = "self-binary";
    /// <summary>審計日誌裡「設定檔雜湊」的範圍標記。</summary>
    public const string ConfigAuditScope = "settings.json";

    private const string Source = "本行程（SHA-256／Authenticode／token）＋審計日誌基線";
    private const string HashBoundary =
        "雜湊證明的是「這份檔案現在是什麼」，不是防篡改保證——有權限的人可以改檔重算。";

    // ── 對外：收集 ────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, SelfIntegrityInputs? inputs = null)
    {
        var i = inputs ?? SelfIntegrityInputs.Real();
        var facts = new List<HardwareFact>(8);

        string? selfHash = Hash(i.HashFile, i.BinaryPath);
        long selfSize = Size(i.SizeOf, i.BinaryPath);
        var audit = Audit(i.LoadAudit);
        bool auditReadable = audit is not null;

        // ── IN-001 自身二進位自檢（雜湊對帳＋審計）──
        string? selfBaseline = LastHash(audit, SelfAuditScope);
        facts.Add(SelfFact(at, i.BinaryPath, selfHash, selfSize, selfBaseline, auditReadable));

        // ── IN-002 設定檔反篡改（同一條基線機制；損毀不靜默）──
        string? configHash = Hash(i.HashFile, i.ConfigPath);
        string? configText = Text(i.ReadText, i.ConfigPath);
        facts.Add(ConfigFact(at, i.ConfigPath, configHash, configText is not null,
            configText is not null && !IsJson(configText), LastHash(audit, ConfigAuditScope), auditReadable));

        // ── IN-004 token 特權報告 ──
        facts.Add(PrivilegeFact(at, Privileges(i.ReadPrivileges)));

        // ── IN-007 依賴完整性（DLL 側載偵測）──
        facts.AddRange(DependencyFacts(at, i));

        // ── IN-010 自我完整性報告頁 ──
        facts.Add(ReportFact(at, facts, auditReadable));
        return facts;
    }

    /// <summary>把自身二進位與設定檔的雜湊記進審計日誌（唯一會寫入的路徑，需使用者明示觸發）。</summary>
    public static (bool Written, string Summary, string? FailureReason) RecordBaseline(
        SelfIntegrityInputs? inputs = null, string? auditPath = null)
    {
        var i = inputs ?? SelfIntegrityInputs.Real();
        string? selfHash = Hash(i.HashFile, i.BinaryPath);
        string? cfgHash = Hash(i.HashFile, i.ConfigPath);
        if (selfHash is null && cfgHash is null)
            return (false, "", "自身二進位與設定檔都讀不到雜湊——沒有東西可以記。");

        string path = auditPath ?? AuditLogService.DefaultPath;
        try
        {
            var log = AuditLogService.Load(path);
            var at = DateTimeOffset.UtcNow;
            if (selfHash is not null)
                log.Add(AuditLogService.Append(log, AuditLogService.CurrentOperator(), "self", "自我完整性基線",
                    SelfAuditScope, $"自身二進位 SHA-256（{BaseName(i.BinaryPath)}）", selfHash, at));
            if (cfgHash is not null)
                log.Add(AuditLogService.Append(log, AuditLogService.CurrentOperator(), "self", "自我完整性基線",
                    ConfigAuditScope, "設定檔 SHA-256", cfgHash, at.AddMilliseconds(1)));
            AuditLogService.Save(path, log);
            return (true,
                $"已把基線記進審計日誌（第 {log.Count} 筆，鏈雜湊可驗）：自身二進位 {Short(selfHash)}、" +
                $"設定檔 {(cfgHash is null ? "讀不到，未記" : Short(cfgHash))}。",
                null);
        }
        catch (Exception ex)
        {
            return (false, "", $"審計日誌寫入失敗（{ex.GetType().Name}：{ex.Message}）——基線沒建立，不是已建立。");
        }
    }

    // ── 逐項事實 ──────────────────────────────────────────────────────────

    private static HardwareFact SelfFact(DateTimeOffset at, string path, string? hash, long size,
        string? baseline, bool auditReadable)
    {
        if (hash is null)
            return Unavailable(at, SelfKey, "自身二進位", FactAvailability.ReadError,
                $"讀不到「{path}」的內容（路徑可能不存在，或本行程沒有讀取權限）——讀不到就是不猜");

        string sizeText = size >= 0 ? $"{size} 位元組" : "大小讀不到";
        string baselineText = baseline is null
            ? auditReadable
                ? "審計日誌裡還沒有這條基線（尚未記錄，可用 --integrity-baseline 建立）"
                : "審計日誌讀不到——無法對帳（不是『相符』，也不是『沒有基線』）"
            : baseline == hash ? "與審計基線相符" : $"與審計基線不符（基線 {Short(baseline)}）";

        return new HardwareFact(SelfKey, Category, "自身二進位",
            $"SHA-256 {Short(hash)}・{sizeText}・{baselineText}。" +
            (baseline is not null && baseline != hash
                ? "不符代表執行檔換過版本或被改動，需人工判斷（更新後的執行檔本來就會不同）。"
                : "") + HashBoundary,
            "", $"{Source}（{BaseName(path)}）", FactTrustLevel.Measured, false, at,
            size >= 0 ? size : null);
    }

    private static HardwareFact ConfigFact(DateTimeOffset at, string path, string? hash,
        bool exists, bool corrupt, string? baseline, bool auditReadable)
    {
        if (!exists)
            return Unavailable(at, ConfigKey, "設定檔完整性", FactAvailability.NotApplicable,
                $"設定檔「{path}」不存在（尚未寫過設定，全程使用預設值）——不適用，不是錯誤");

        if (hash is null)
            return Unavailable(at, ConfigKey, "設定檔完整性", FactAvailability.ReadError,
                "設定檔存在但讀不到內容（權限）——讀不到就是不猜");

        if (corrupt)
            return new HardwareFact(ConfigKey, Category, "設定檔完整性",
                $"內容不是合法 JSON（損毀）・目前 SHA-256 {Short(hash)}・" +
                "建議：先備份原檔再重建（不靜默略過、不自動覆蓋）",
                "", Source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "設定檔 JSON 解析失敗（損毀）——值不予採信");

        string baselineText = baseline is null
            ? auditReadable ? "審計日誌裡還沒有設定檔基線" : "審計日誌讀不到——無法對帳"
            : baseline == hash ? "與審計基線相符" : $"與審計基線不符（基線 {Short(baseline)}）";
        return new HardwareFact(ConfigKey, Category, "設定檔完整性",
            $"SHA-256 {Short(hash)}・{baselineText}。" +
            "設定檔被改動是常態（換版面、改門檻就會寫檔）；本項只陳述事實並保留可比對的基線。",
            "", Source, FactTrustLevel.Measured, false, at, null);
    }

    private static HardwareFact PrivilegeFact(DateTimeOffset at, TokenPrivilegeSnapshot? privs)
    {
        if (privs is null)
            return Unavailable(at, PrivsKey, "執行權限", FactAvailability.ReadError,
                "GetTokenInformation 讀不到本行程 token——讀不到就是不猜（不以「未提權」冒充）");

        string enabled = privs.EnabledPrivileges.Count == 0
            ? "沒有啟用中的特權"
            : $"啟用 {privs.TotalPrivileges} 項中的 {privs.EnabledPrivileges.Count} 項：" +
              string.Join("、", privs.EnabledPrivileges.Take(6)) +
              (privs.EnabledPrivileges.Count > 6 ? " 等" : "");
        return new HardwareFact(PrivsKey, Category, "執行權限",
            $"提權：{(privs.Elevated ? "是（高完整性）" : "否")}・完整性層級 {privs.IntegrityLevel}・{enabled}。" +
            "權限是事實：需要提權的功能能不能用，取決於這一行，不取決於猜測。",
            "", "GetTokenInformation（TokenElevation／TokenIntegrityLevel／TokenPrivileges）",
            FactTrustLevel.Reported, false, at, privs.TotalPrivileges);
    }

    private static IEnumerable<HardwareFact> DependencyFacts(DateTimeOffset at, SelfIntegrityInputs i)
    {
        IReadOnlyList<string> modules = Modules(i.LoadedModulePaths);
        if (modules.Count == 0)
        {
            yield return Unavailable(at, DepsKey, "已載入模組", FactAvailability.ReadError,
                "模組列舉回空清單（Process.Modules 失敗）——讀不到就是不猜");
            yield break;
        }

        var unique = modules.Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var nonSystem = unique.Where(p => !KernelModuleService.IsWindowsDirectory(p)).ToList();
        int unsigned = 0, sideloadCount = 0;
        var flagged = new List<string>();
        foreach (string path in nonSystem)
        {
            var (ok, note) = Verify(i.VerifySignature, path);
            bool isSideload = IsSideloadCandidate(path);
            if (ok == false) unsigned++;
            if (isSideload) sideloadCount++;
            // 簽章通過又不在可寫位置的模組不列名：列出來的要是「值得看的那幾條」
            // （注意條件是「ok == true」——ok 為 null 代表無法驗證，那也要列名）
            if (ok == true && !isSideload) continue;

            var reasons = new List<string>(3);
            if (ok == false) reasons.Add($"未通過簽章 {note}");
            else if (ok is null) reasons.Add($"簽章無法驗證（{note}）");
            if (isSideload) reasons.Add("側載候選");
            flagged.Add($"{BaseName(path)}（{string.Join("、", reasons)}）");
        }

        // 逐項明細串在同一個值裡（不另立鍵）：這一版刻意不新增動態家族。
        string detail = flagged.Count == 0
            ? "沒有值得列名的模組"
            : "值得看：" + string.Join("；", flagged.Take(ListedMax)) +
              (flagged.Count > ListedMax ? $"；等 {flagged.Count - ListedMax} 項" : "");

        yield return new HardwareFact(DepsKey, Category, "已載入模組",
            $"模組 {unique.Count} 個・非系統目錄 {nonSystem.Count} 個・未通過簽章 {unsigned} 個・側載候選 {sideloadCount} 個・{detail}。" +
            "側載候選不是惡意判決，未通過簽章也不是（外掛與第三方函式庫常態就在使用者目錄下）——只陳述事實。",
            "個", "Process.Modules ＋ Authenticode ＋ 載入路徑", FactTrustLevel.Reported, false, at,
            unique.Count);
    }

    private static HardwareFact ReportFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts, bool auditReadable)
    {
        int counted = 0, present = 0, unverified = 0;
        foreach (var f in facts)
        {
            if (f.Key is not (SelfKey or ConfigKey or PrivsKey or DepsKey)) continue;
            counted++;
            if (f.Availability == FactAvailability.Present) present++;
            else unverified++;
        }
        string auditText = auditReadable ? "審計基線可讀" : "審計日誌讀不到（無法對帳——不是相符）";
        return new HardwareFact(ReportKey, Category, "自我完整性報告",
            $"已檢 {counted} 項（二進位／設定檔／權限／載入模組）：可採信 {present} 項、待確認 {unverified} 項・{auditText}。" +
            "本報告只陳述「這一刻觀察到什麼」，沒有防篡改保證。",
            "", Source, FactTrustLevel.Derived, false, at, present,
            unverified > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            unverified > 0 ? $"{unverified} 項待確認（原因見各列）——不讀成全部通過" : null);
    }

    // ── 判定小工具 ────────────────────────────────────────────────────────

    /// <summary>審計日誌裡最後一筆指定範圍的雜湊（沒有＝null）。</summary>
    private static string? LastHash(IReadOnlyList<AuditEntry>? audit, string scope)
    {
        if (audit is null) return null;
        for (int k = audit.Count - 1; k >= 0; k--)
            if (audit[k].Scope == scope && !string.IsNullOrWhiteSpace(audit[k].ResultHash) && audit[k].ResultHash != "—")
                return audit[k].ResultHash;
        return null;
    }

    /// <summary>載入路徑是否落在使用者可寫位置（側載候選）——只做路徑判定，不下惡意結論。</summary>
    public static bool IsSideloadCandidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string[] markers =
        [
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ];
        foreach (string m in markers)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            if (path.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(profile)
               && path.StartsWith(Path.Combine(profile, "Downloads"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJson(string text)
    {
        try { using var parsed = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    private static string Short(string? hash) =>
        string.IsNullOrEmpty(hash) ? "—" : (hash.Length > 16 ? hash[..16] + "…" : hash);

    private static string BaseName(string path) =>
        string.IsNullOrWhiteSpace(path) ? "—" : Path.GetFileName(path);

    private static string? Hash(Func<string, string?> f, string path)
    { try { return f(path); } catch { return null; } }

    private static long Size(Func<string, long> f, string path)
    { try { return f(path); } catch { return -1; } }

    private static string? Text(Func<string, string?> f, string path)
    { try { return f(path); } catch { return null; } }

    private static IReadOnlyList<string> Modules(Func<IReadOnlyList<string>> f)
    { try { return f(); } catch { return []; } }

    private static IReadOnlyList<AuditEntry>? Audit(Func<IReadOnlyList<AuditEntry>?> f)
    { try { return f(); } catch { return null; } }

    private static TokenPrivilegeSnapshot? Privileges(Func<TokenPrivilegeSnapshot?> f)
    { try { return f(); } catch { return null; } }

    private static (bool? Ok, string Note) Verify(Func<string, (bool? Ok, string Note)> f, string path)
    { try { return f(path); } catch { return (null, "驗證呼叫拋出例外"); } }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}

/// <summary>
/// token 特權讀取（IN-004）：TokenElevation／TokenIntegrityLevel／TokenPrivileges 一次讀齊。
/// 任何一步失敗回 <c>null</c>——呼叫端如實標三態，不把「讀不到」畫成「未提權」。
/// </summary>
public static class TokenPrivilegeReader
{
    public static TokenPrivilegeSnapshot? Read()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return null;
            var (enabled, total) = ReadPrivileges(token);
            return new TokenPrivilegeSnapshot(ReadElevation(token), ReadIntegrity(token), total, enabled);
        }
        catch { return null; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }

    private static bool ReadElevation(IntPtr token)
    {
        IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            return GetTokenInformation(token, TokenElevation, buf, sizeof(int), out _)
                   && Marshal.ReadInt32(buf) != 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string ReadIntegrity(IntPtr token)
    {
        if (GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out uint needed) && needed > 0)
        {
            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (GetTokenInformation(token, TokenIntegrityLevel, buf, needed, out _))
                {
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    if (sid != IntPtr.Zero)
                    {
                        byte subAuthorityCount = Marshal.ReadByte(sid, 1);
                        if (subAuthorityCount > 0)
                        {
                            int last = Marshal.ReadInt32(sid, 8 + (subAuthorityCount - 1) * 4);
                            // SECURITY_MANDATORY_*_RID：高 4 位決定等級（0x1000 中／0x2000 高／0x3000 系統）
                            return (last & 0xF000) switch
                            {
                                0x0000 => $"低（RID 0x{last:X}）",
                                0x1000 => "中",
                                0x2000 => "高",
                                0x3000 => "系統",
                                _ => $"未知（RID 0x{last:X}）",
                            };
                        }
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return "讀不到";
    }

    private static (IReadOnlyList<string> Enabled, int Total) ReadPrivileges(IntPtr token)
    {
        if (!GetTokenInformation(token, TokenPrivileges, IntPtr.Zero, 0, out uint needed) || needed == 0)
            return ([], 0);
        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, TokenPrivileges, buf, needed, out _)) return ([], 0);
            int count = Marshal.ReadInt32(buf);
            var enabled = new List<string>();
            // TOKEN_PRIVILEGES{ DWORD PrivilegeCount; LUID_AND_ATTRIBUTES[] }：每筆 12 位元組（LUID 8＋屬性 4）
            for (int k = 0; k < count; k++)
            {
                int offset = 4 + k * 12;
                if ((Marshal.ReadInt32(buf, offset + 8) & SE_PRIVILEGE_ENABLED) == 0) continue;
                string name = LookupPrivilege(buf, offset);
                if (name.Length > 0) enabled.Add(name);
            }
            return (enabled, count);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string LookupPrivilege(IntPtr buf, int offset)
    {
        long luid = Marshal.ReadInt64(buf, offset);
        var sb = new System.Text.StringBuilder(256);
        uint len = (uint)sb.Capacity;
        return LookupPrivilegeName(null, ref luid, sb, ref len) ? sb.ToString() : "";
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const int TokenIntegrityLevel = 25;
    private const int TokenPrivileges = 3;
    private const int SE_PRIVILEGE_ENABLED = 0x00000002;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint infoLength, out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeName(string? system, ref long luid, System.Text.StringBuilder name, ref uint nameLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
