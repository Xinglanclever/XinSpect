using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

namespace XtuBridge
{
    /// <summary>SDK 完整性閘門結果：三態中的一態由 Message 帶出，不給曖昧的 true/false。</summary>
    public sealed class SdkGateResult
    {
        public bool Ok;
        public bool FirstEstablish;   // 本次是首次建立釘扎（依賴 DLL 也允許新釘扎）
        public string Reason = "";
    }

    /// <summary>
    /// Intel XTU SDK 與其依賴 DLL 的完整性閘門（審計 P2-23）。
    /// <list type="bullet">
    /// <item>① Authenticode：WinVerifyTrust 驗嵌入簽章與完整憑證鏈（鏈到本機信任根），</item>
    /// <item>　 並以 X509Chain 重建鏈、要求簽署者主體含 "Intel"。</item>
    /// <item>② SHA-256 釘扎（TOFU）：首次（且僅首次）在簽章雙過時把雜湊記入受 DACL 保護的</item>
    /// <item>　 sdk_pins.txt；之後每次載入逐位元組比對，依賴 DLL 同樣入釘扎。</item>
    /// </list>
    /// 威脅模型誠實申報：這擋的是「換指針＋塞假 DLL」的注入面與同機其他使用者的篡改；
    /// 能動同一使用者 AppData 的同權限惡意程式本來就能改一切使用者層設定，此為深度防禦，
    /// 不是與核心級攻擊的對峙線。
    /// </summary>
    public static class SdkIntegrity
    {
        public static string PinStorePath => Path.Combine(XtuCore.RootDir, "sdk_pins.txt");

        // 本 session 是否允許「新釘扎」：僅在 GateSdk 判定首次建立（且簽章雙過）時為 true。
        private static bool _allowNewPins;
        private static Dictionary<string, string> _pins;
        private static bool _pinsLoaded;

        // ── 對外：SDK 主 DLL 的閘門 ────────────────────────────────────────
        public static SdkGateResult GateSdk(string dll)
        {
            try
            {
                LoadPins();
                string sha = Sha256(dll);
                string key = PinKey("sdk", Path.GetFileName(dll));

                if (_pins.TryGetValue(key, out string pinned))
                {
                    // 已有釘扎：雜湊相符即放行（雜湊是比簽章更強的事實；簽章狀態、撤銷與
                    // 憑證輪替不影響已釘扎的安裝）。不符＝XTU 被更換或遭篡改，拒載。
                    if (string.Equals(pinned, sha, StringComparison.OrdinalIgnoreCase))
                    {
                        _allowNewPins = false;   // 正常 session：依賴 DLL 只准比對，不准新增
                        return new SdkGateResult { Ok = true };
                    }
                    return new SdkGateResult
                    {
                        Reason = "SDK DLL 的 SHA-256 與釘扎記錄不符——Intel XTU 已被更換、更新為未驗版本，" +
                                 "或檔案遭篡改。",
                    };
                }

                // 無釘扎：必須簽章雙過才允許建立（TOFU 不是盲目的）
                var sig = VerifyAuthenticode(dll);
                if (!sig.Ok) return new SdkGateResult { Reason = "Authenticode：" + sig.Reason };
                var intel = IsIntelSigned(dll);
                if (!intel.Ok) return new SdkGateResult { Reason = "簽署者：" + intel.Reason };

                AddPin(key, sha);
                _allowNewPins = true;   // 首次建立 session：依賴 DLL 允許入釘扎
                Diag("SDK 首次釘扎建立：" + dll + " sha256=" + sha);
                return new SdkGateResult { Ok = true, FirstEstablish = true };
            }
            catch (Exception ex)
            {
                return new SdkGateResult { Reason = "完整性檢查本身擲出例外：" + ex.Message };
            }
        }

        // ── 對外：依賴解析器（AssemblyResolve）每載一個檔案都要過這道 ────────
        public static bool CheckDependency(string file)
        {
            try
            {
                string sha = Sha256(file);
                string key = PinKey("dep", Path.GetFileName(file));
                if (_pins.TryGetValue(key, out string pinned))
                {
                    if (string.Equals(pinned, sha, StringComparison.OrdinalIgnoreCase)) return true;
                    Diag("依賴拒載（釘扎不符）：" + file);
                    return false;
                }
                if (_allowNewPins)
                {
                    AddPin(key, sha);
                    Diag("依賴首次釘扎：" + file + " sha256=" + sha);
                    return true;
                }
                Diag("依賴拒載（無釘扎且本 session 不允許新增）：" + file);
                return false;
            }
            catch (Exception ex)
            {
                Diag("依賴完整性檢查失敗，拒載：" + file + " · " + ex.Message);
                return false;   // 檢查做不了就拒載——fail closed
            }
        }

        // ── Authenticode（WinVerifyTrust，完整政策驗證含時間戳）──────────────
        // 〔複驗更正〕2026-11-07 紅隊複驗：原稿 GUID "00C568FA-99AA-4E38-9F5D-93BCD5BB5EA2"
        // 在 wintrust.h／softpub.h 的已登錄政策動作裡查無此號——未登錄的 action GUID 會使
        // WinVerifyTrust 回 TRUST_E_PROVIDER_UNKNOWN（0x800B0001），驗證閘門永遠 fail closed、
        // SDK 永遠拒載（安全但整個功能死掉）。改用公開文件可考的
        // WINTRUST_ACTION_GENERIC_VERIFY_V2（softpub.h：{00AAC56B-CD44-11D0-8CC2-00C04FC295EE}）——
        // 這正是 Get-AuthenticodeSignature／標準 Authenticode 檔案驗證用的動作。
        private static readonly Guid ActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        // ── wintrust interop（〔修復建置 2026-11-07〕net48 BCL 不內建這些型別，依 wintrust.h 自行宣告）──
        // WINTRUST_FILE_INFO：cbStruct/pcwszFilePath/hFile/pgKnownSubject，x64 佈局 32 bytes。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;   // LPWSTR（CharSet.Unicode）
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        // WINTRUST_DATA：本橋只走 WTD_CHOICE_FILE，union 以單一指標位代表 pFile 成員
        // （CLR Sequential 依自然對齊補 padding，x64 sizeof=80 與 C 一致）。
        [StructLayout(LayoutKind.Sequential)]
        internal struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;           // union：WINTRUST_FILE_INFO_*（WTD_CHOICE_FILE）
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hWnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        private static IntegrityResult VerifyAuthenticode(string path)
        {
            var fi = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = path,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fi, pFile, false);
                var wd = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,            // WTD_UI_NONE：不彈任何 UI
                    fdwRevocationChecks = 0,   // WTD_REVOKE_NONE：離線環境不因撤銷檢查誤判
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,         // WTD_STATEACTION_IGNORE：一次性驗證
                };
                Guid action = ActionGenericVerifyV2;
                int hr = WinVerifyTrust(IntPtr.Zero, ref action, ref wd);
                if (hr == 0) return IntegrityResult.Pass();
                return IntegrityResult.Fail("簽章驗證未通過（WinVerifyTrust=0x" + hr.ToString("X8") + DescribeTrustError(hr) + "）");
            }
            catch (Exception ex)
            {
                return IntegrityResult.Fail("無法驗證簽章：" + ex.Message);
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
            }
        }

        private static string DescribeTrustError(int hr)
        {
            switch (hr)
            {
                case unchecked((int)0x800B0100): return "＝檔案沒有嵌入簽章";
                case unchecked((int)0x800B0101): return "＝簽章已過期";
                case unchecked((int)0x800B0109): return "＝鏈到不受信任的根";
                case unchecked((int)0x80092009): return "＝無法讀取簽章";
                default: return "";
            }
        }

        // ── Intel 簽署者驗證（X509Chain 重建鏈＋主體檢查）────────────────────
        private static IntegrityResult IsIntelSigned(string path)
        {
            try
            {
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                if (cert.Subject.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) < 0)
                    return IntegrityResult.Fail("簽章有效，但簽署者不是 Intel：" + cert.Subject);

                // 以本機信任根重建鏈：WinVerifyTrust 已做完整政策驗證，這裡補一層
                // managed 鏈驗證（UntrustedRoot／NotTimeValid 等狀態任一即拒）。
                var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                bool ok = chain.Build(cert);
                if (!ok || chain.ChainStatus.Length == 0)
                {
                    string st = string.Join("; ", Array.ConvertAll(chain.ChainStatus,
                        s => s.Status + "=" + s.StatusInformation.Trim()));
                    return IntegrityResult.Fail("簽署者憑證鏈未驗過：" + (st.Length > 0 ? st : "Build 回 false"));
                }
                return IntegrityResult.Pass();
            }
            catch (Exception ex)
            {
                return IntegrityResult.Fail("無法解析簽署者憑證：" + ex.Message);
            }
        }

        // ── 釘扎檔（受 DACL 保護）────────────────────────────────────────────
        private static void LoadPins()
        {
            if (_pinsLoaded) return;
            _pinsLoaded = true;
            _pins = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(PinStorePath)) return;
                foreach (string line in File.ReadAllLines(PinStorePath))
                {
                    string l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    _pins[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                Diag("讀取釘扎檔失敗（將視為無釘扎、需簽章重建）：" + ex.Message);
                _pins.Clear();
            }
        }

        private static void AddPin(string key, string sha)
        {
            _pins[key] = sha;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# XtuBridge SDK 釘扎檔（審計 P2-23）。行格式：key=sha256hex。");
                sb.AppendLine("# XTU 升級若被本檔擋下：先確認新版 SDK 為 Intel 簽章，再刪除本檔讓閘門重建。");
                foreach (var kv in _pins) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(PinStorePath, sb.ToString());
                HardenAcl(PinStorePath);
            }
            catch (Exception ex)
            {
                Diag("寫入釘扎檔失敗：" + ex.Message);
                throw;   // 釘扎寫不進去就等於沒有閘門——拒載，不要帶著假安全感放行
            }
        }

        /// <summary>
        /// 釘扎檔 DACL：停用繼承、清除既有 ACE，只留目前使用者／SYSTEM／Administrators 三條 Allow。
        /// 其他使用者（含低權攻擊者）讀不到、更寫不了。失敗不靜默——記入 bridge.log。
        /// </summary>
        private static void HardenAcl(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                FileSecurity sec = fi.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);   // 停用繼承、不保留既有 ACE

                IdentityReference user = WindowsIdentity.GetCurrent().User;
                IdentityReference system = new SecurityIdentifier("S-1-5-18");                    // SYSTEM
                IdentityReference admins = new SecurityIdentifier("S-1-5-32-544");               // Administrators
                foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (!r.IdentityReference.Value.Equals(user.Value) &&
                        !r.IdentityReference.Value.Equals(system.Value) &&
                        !r.IdentityReference.Value.Equals(admins.Value))
                        sec.RemoveAccessRuleSpecific(r);
                sec.SetAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                sec.SetAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
                sec.SetAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
                fi.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                Diag("釘扎檔 ACL 加固失敗（不阻斷，僅記錄）：" + ex.Message);
            }
        }

        // ── 雜湊與工具 ─────────────────────────────────────────────────────
        private static string Sha256(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return ToHexUpper(sha.ComputeHash(fs));
        }

        // 〔修復建置 2026-11-07〕net48 沒有 Convert.ToHexString（.NET 5+ 才有），
        // 以大寫十六進位等價實作（比對端本就 OrdinalIgnoreCase）。
        private static string ToHexUpper(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static string PinKey(string prefix, string fileName)
            => prefix + ":" + fileName.ToLowerInvariant();

        private static void Diag(string msg) => XtuCore.Diag("[integrity] " + msg);
    }

    /// <summary>內部三態小結構（與 SdkGateResult 分開，避免語意混淆）。</summary>
    internal sealed class IntegrityResult
    {
        public bool Ok;
        public string Reason = "";
        public static IntegrityResult Pass() => new IntegrityResult { Ok = true };
        public static IntegrityResult Fail(string why) => new IntegrityResult { Ok = false, Reason = why };
    }
}
