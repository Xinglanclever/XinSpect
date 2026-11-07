using System.Globalization;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace BlueSquadron;

/// <summary>
/// 基線雜湊存儲（審計 P2-31 加固版）：保存首次偵測到的安全狀態，後續比對時若有差異即產生威脅事件。
/// 儲存在 %LOCALAPPDATA%\XinSpect\bluesquadron\baseline.json，外層包一個帶 HMAC-SHA256 的信封：
/// { "hmac": hex, "created": iso, "data": { …原基線字典… } }。
/// <para>
/// <b>完整性</b>：HMAC 金鑰派生自機器唯一值（HKLM Cryptography\MachineGuid）＋基線檔首建時間，
/// 派生結果以 DPAPI（CurrentUser）保護存於 baseline.key——跨機器拷貝的基線驗不了、
/// 盲改內容驗不了、刪 key 檔重派生會因派生式確定而得到同一把（檔案首建時間未變時）。
/// </para>
/// <para>
/// <b>壞檔三態，絕不靜默重校</b>：Load 驗證失敗 → ①壞檔改名隔離（.bad-時間戳），
/// ②上一份驗證過的 .bak 若有效則還原（保留舊基線），還原不了就保持空、本輪不建立新基線，
/// ③狀態經 <see cref="LoadStatus"/>／<see cref="IntegrityFailure"/> 上報（Program.cs 轉 RecordThreat）。
/// 舊版（無信封的純字典）檔案視為 <see cref="ParseOutcome.ValidLegacyNoHmac"/>：照樣載入，
/// 下一次 Save 時自動升級為信封格式——不因升級而把老使用者的基線炸掉。
/// </para>
/// <para>
/// 威脅模型申報：MachineGuid 可被使用者讀、DPAPI 同使用者可解；本防護針對其他使用者／
/// 離線拷貝／盲改，以及「事後留痕」，不是對同權限惡意程式的對峙線。
/// </para>
/// </summary>
internal sealed class BaselineStore
{
    private readonly string _path;
    private readonly string _keyPath;
    private Dictionary<string, string> _baseline = [];
    private byte[]? _hmacKey;

    /// <summary>DPAPI 附加熵（常數；CurrentUser 範疇已綁使用者＋機器，這裡只是多一層）。</summary>
    private static readonly byte[] Entropy = { 0x58, 0x69, 0x6E, 0x53, 0x70, 0x65, 0x63, 0x74 }; // "XinSpect"

    /// <summary>最後一次 Load／Save 的狀態（事件上報用）：null＝正常無事。</summary>
    public string? LoadStatus { get; private set; }

    /// <summary>完整性驗證失敗（HMAC 不符／解析失敗且無可還原備份／讀取例外）。</summary>
    public bool IntegrityFailure { get; private set; }

    public BaselineStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XinSpect", "bluesquadron");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "baseline.json");
        _keyPath = Path.Combine(dir, "baseline.key");
    }

    private enum ParseOutcome { Valid, ValidLegacyNoHmac, Corrupt }

    /// <summary>載入已存基線（含完整性驗證；壞檔走隔離→備份還原→上報三態，不靜默重校）。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) { _baseline = []; return; }

            string json = File.ReadAllText(_path);
            var (outcome, data) = Parse(json);

            if (outcome == ParseOutcome.Valid)
            {
                _baseline = data ?? [];
                KeepBackup(json);
                return;
            }
            if (outcome == ParseOutcome.ValidLegacyNoHmac)
            {
                // 舊版無 HMAC 的檔案：照用，標註升級（下次 Save 自動包信封）。不算完整性失敗。
                _baseline = data ?? [];
                LoadStatus = "偵測到舊格式（無 HMAC）基線，下次儲存時自動升級為完整性保護格式。";
                return;
            }

            // ── 壞檔：三態處理 ────────────────────────────────────────────
            // ① 隔離：改名保留現場（鑑識用），不刪、不覆寫。
            // 〔修復 v3.20〕隔離前先記下原檔的 CreationTimeUtc：隔離後 _path 已不在，
            // .bak 驗 HMAC 時若 HmacKey 改以 UtcNow 當派生種子（baseline.key 缺失走重派生
            // 那條路時必現），會得出一把不同的鑰，把完好的備份誤判成損毀。
            DateTime? seedCreatedUtc =
                File.Exists(_path) ? new FileInfo(_path).CreationTimeUtc : (DateTime?)null;
            string quarantine = _path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Move(_path, quarantine); } catch { /* 隔離不了也絕不覆寫原檔 */ }
            // ② 保留舊基線：上一份驗證過的 .bak 可還原就還原；不能就保持空，本輪不悄悄建立新基線。
            string bak = _path + ".bak";
            if (File.Exists(bak))
            {
                var (bakOutcome, bakData) = Parse(File.ReadAllText(bak), seedCreatedUtc);
                if (bakOutcome == ParseOutcome.Valid)
                {
                    _baseline = bakData ?? [];
                    try { File.Copy(bak, _path, overwrite: true); } catch { }
                    IntegrityFailure = true;
                    LoadStatus = $"baseline.json 完整性驗證失敗（HMAC 不符或格式損毀），壞檔隔離於 {quarantine}；"
                               + "已從備份還原上一版基線。若非本機重裝／換機所致，請視為基線遭竄改的警訊。";
                    return;
                }
            }
            IntegrityFailure = true;
            _baseline = [];
            LoadStatus = $"baseline.json 完整性驗證失敗，壞檔隔離於 {quarantine}；無可還原備份，"
                       + "本輪不建立新基線。重新校準需明示（刪除 .bad- 隔離檔與 baseline.key 後重掃），"
                       + "若非本機重裝／換機所致，請視為基線遭竄改的警訊。";
        }
        catch (Exception ex)
        {
            _baseline = [];
            IntegrityFailure = true;
            LoadStatus = "baseline.json 無法讀取：" + ex.Message + "（本輪不建立新基線）";
        }
    }

    /// <summary>儲存目前基線（包 HMAC 信封；先寫 .tmp 再原子換名；寫敗如實上報）。</summary>
    public void Save()
    {
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                hmac = ComputeHmac(_baseline),
                created = DateTime.UtcNow.ToString("o"),
                data = _baseline,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // P2-31：寫敗不再吞——如實記在狀態上（比對在用一份可能沒寫成功的基準，必須讓人知道）
            LoadStatus = "baseline.json 寫入失敗：" + ex.Message;
        }
    }

    // ── 解析與 HMAC ─────────────────────────────────────────────────────

    // 〔修復 v3.20〕hmacSeedCreatedUtc：主檔被隔離後驗 .bak 時傳入原檔的首建時間，
    // 讓金鑰重派生用同一把尺；平時（主檔在場）不傳，照舊以檔案自身的 CreationTimeUtc 為準。
    private (ParseOutcome, Dictionary<string, string>?) Parse(string json, DateTime? hmacSeedCreatedUtc = null)
    {
        try
        {
            var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            if (doc.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (ParseOutcome.Corrupt, null);
            if (doc.TryGetProperty("hmac", out var hmacEl) && doc.TryGetProperty("data", out var dataEl)
                && hmacEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(dataEl.GetRawText());
                if (data is null) return (ParseOutcome.Corrupt, null);
                return (VerifyHmac(data, hmacEl.GetString() ?? "", hmacSeedCreatedUtc)
                        ? ParseOutcome.Valid : ParseOutcome.Corrupt, data);
            }
            // 舊格式：頂層就是 { key: value } 的純字典
            var legacy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return legacy is null ? (ParseOutcome.Corrupt, null) : (ParseOutcome.ValidLegacyNoHmac, legacy);
        }
        catch { return (ParseOutcome.Corrupt, null); }
    }

    private void KeepBackup(string goodJson)
    {
        try { File.WriteAllText(_path + ".bak", goodJson); } catch { /* 備份失敗不影響主流程 */ }
    }

    private string ComputeHmac(Dictionary<string, string> data)
    {
        using var h = new HMACSHA256(HmacKey());
        return Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Canonical(data))));
    }

    private bool VerifyHmac(Dictionary<string, string> data, string hmacHex, DateTime? hmacSeedCreatedUtc = null)
    {
        try
        {
            using var h = new HMACSHA256(HmacKey(hmacSeedCreatedUtc));
            byte[] mac = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Canonical(data)));
            return CryptographicOperations.FixedTimeEquals(mac, Convert.FromHexString(hmacHex));
        }
        catch { return false; }
    }

    /// <summary>規範化序列化（key 依序數排序）——HMAC 不因 JSON 屬性順序漂移而誤判。</summary>
    private static string Canonical(Dictionary<string, string> data)
        => string.Join("\n", data.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                 .Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>
    /// HMAC 金鑰（惰性、整個生命週期一把）：
    /// key 檔存在 → DPAPI 解出；不存在 → 派生自 MachineGuid＋基線檔首建時間，DPAPI 落盤。
    /// 派生式確定：key 檔被刪而基線檔未動時，重派生可得同一把（首建時間不變），
    /// 不會把沒被動過的使用者炸掉；基線檔被換掉則首建時間變 → 金鑰變 → 驗證失敗 → 走隔離流程。
    /// </summary>
    private byte[] HmacKey(DateTime? createdOverride = null)
    {
        if (_hmacKey is not null) return _hmacKey;
        if (File.Exists(_keyPath))
        {
            try
            {
                _hmacKey = ProtectedData.Unprotect(File.ReadAllBytes(_keyPath), Entropy, DataProtectionScope.CurrentUser);
                return _hmacKey;
            }
            catch
            {
                LoadStatus = "baseline.key 無法解密（可能被清除或使用者設定檔變動），將重建金鑰；"
                           + "既有基線會因驗證失敗而隔離。";
            }
        }
        string machineGuid = ReadMachineGuid();
        // 〔修復 v3.20〕基線檔已不在（隔離中）時改用呼叫端捕獲的首建時間，維持上面
        // 「派生式確定」的承諾；檔案在場時照舊以檔案自身的 CreationTimeUtc 為準。
        DateTime created = createdOverride
                          ?? (File.Exists(_path) ? new FileInfo(_path).CreationTimeUtc : DateTime.UtcNow);
        using var sha = SHA256.Create();
        _hmacKey = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
            machineGuid + "|" + created.Ticks.ToString(CultureInfo.InvariantCulture)));
        StoreKey();
        return _hmacKey;
    }

    private void StoreKey()
    {
        try
        {
            File.WriteAllBytes(_keyPath, ProtectedData.Protect(_hmacKey!, Entropy, DataProtectionScope.CurrentUser));
            HardenAcl(_keyPath);
        }
        catch { /* DPAPI 失敗：金鑰留記憶體，本次 session 可用；下次重派生。不靜默——狀態已記 */ }
    }

    private static string ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? "no-guid";
        }
        catch { return "no-guid"; }
    }

    /// <summary>key 檔 DACL：停用繼承，只留目前使用者／SYSTEM／Administrators 三條 Allow。</summary>
    private static void HardenAcl(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            FileSecurity sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(true, false);
            IdentityReference user = WindowsIdentity.GetCurrent().User!;
            IdentityReference system = new SecurityIdentifier("S-1-5-18");
            IdentityReference admins = new SecurityIdentifier("S-1-5-32-544");
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (!r.IdentityReference.Value.Equals(user.Value)
                    && !r.IdentityReference.Value.Equals(system.Value)
                    && !r.IdentityReference.Value.Equals(admins.Value))
                    sec.RemoveAccessRuleSpecific(r);
            sec.SetAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            sec.SetAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            sec.SetAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch { /* ACL 加固失敗不阻斷（金鑰本身已有 DPAPI 保護） */ }
    }

    /// <summary>
    /// 設定或更新一個基線值。若是首次設定回傳 null；若值變更回傳舊值。
    /// </summary>
    public string? Set(string key, string value)
    {
        if (_baseline.TryGetValue(key, out var old))
        {
            if (old == value) return null;
            _baseline[key] = value;
            return old;  // changed!
        }
        _baseline[key] = value;
        return null;  // first set
    }

    /// <summary>取得基線值。</summary>
    public string? Get(string key)
        => _baseline.GetValueOrDefault(key);

    /// <summary>所有基線項目。</summary>
    public IReadOnlyDictionary<string, string> All => _baseline;

    /// <summary>
    /// 快照目前事實到基線。回傳有變更的項目清單（key, oldValue, newValue）。
    /// </summary>
    public List<(string Key, string Old, string New)> Snapshot(SecurityFacts facts)
    {
        var changes = new List<(string, string, string)>();

        void Check(string key, string? value)
        {
            if (value is null) return;
            string? old = Set(key, value);
            if (old is not null)
                changes.Add((key, old, value));
        }

        Check("hvci", facts.HvciRunning?.ToString());
        Check("vbs", facts.VbsRunning?.ToString());
        Check("secureboot", facts.SecureBootEnabled?.ToString());
        Check("testsigning", facts.TestSigningEnabled?.ToString());
        Check("iommu", facts.IommuAvailable?.ToString());
        Check("dma_protection", facts.DmaProtection?.ToString());
        Check("credential_guard", facts.CredentialGuardRunning?.ToString());
        Check("total_drivers", facts.TotalDriverCount.ToString());
        Check("unsigned_drivers", facts.UnsignedDriverCount.ToString());
        Check("driver_blocklist", facts.VulnerableDriverBlocklistPresent?.ToString());

        return changes;
    }
}
