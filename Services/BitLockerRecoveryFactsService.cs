using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace XinSpect;

/// <summary>
/// BitLocker 復原金鑰輪廓（Vol2 第 SA-016 項）：<c>sa.bl.recovery</c>——只回答
/// <b>「有幾個復原保護器、幾把金鑰看得到、有沒有掛到 AD／Entra 備份」</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一項的紅線只有一條，而且是硬線：金鑰值永不讀、永不輸出、永不落地。</b>
/// 復原金鑰是「最後一道門」——有了它，加密就等於沒加密。所以本服務查詢的欄位
/// 刻意不含 <c>NumericalPassword</c>、<c>RecoveryPassword</c>、<c>KeyProtector</c> 識別碼；
/// 只取保護器<b>型別</b>與<b>數量</b>。
/// </para>
/// <para>
/// <b>與既有能力的界線：</b><c>ds.bl.summary</c>（DS-001）陳述<b>卷的保護狀態</b>，
/// <c>fw.bitlocker.summary</c>（FW-003）陳述<b>保護器總數</b>——兩者都不區分保護器型別，
/// 也都不回答「金鑰有沒有備份出去」。本項補的是那個缺口：
/// <b>復原金鑰存在但從未備份</b>是一個真實且常見的處境（換主機板就再也進不去），
/// 而那件事只有把保護器型別拆開才看得出來。
/// </para>
/// <para>
/// <b>誠實約定：</b>①「沒有復原保護器」<b>不是缺陷</b>——只用 TPM 保護的機器本來就沒有，
/// 這要說清楚免得被當成紅燈；②「有保護器但備份狀態讀不到」要與「沒備份」分開，
/// 前者是 <see cref="FactAvailability.InsufficientPrivilege"/>／
/// <see cref="FactAvailability.NotSupported"/>，後者是可判讀的事實；
/// ③<b>0 把金鑰是值不是缺席</b>。
/// </para>
/// </remarks>
public static class BitLockerRecoveryFactsService
{
    private const string Cat = "安全稽核深化";
    private const string Source = @"root\CIMV2\Security\MicrosoftVolumeEncryption（Win32_EncryptableVolume，只取保護器型別）";

    /// <summary>SA-016 復原金鑰輪廓。</summary>
    public const string RecoveryKey = "sa.bl.recovery";

    // ── 保護器型別（Win32_EncryptableVolume.GetProtectors 的 type 值，MSDN 出處已核）──

    /// <summary>TPM。</summary>
    public const int ProtectorTpm = 1;
    /// <summary>外部金鑰。</summary>
    public const int ProtectorExternalKey = 2;
    /// <summary>數值密碼（就是俗稱的 48 碼復原金鑰）。</summary>
    public const int ProtectorNumericalPassword = 3;
    /// <summary>TPM ＋ PIN。</summary>
    public const int ProtectorTpmPin = 4;
    /// <summary>TPM ＋ 啟動金鑰。</summary>
    public const int ProtectorTpmStartupKey = 5;
    /// <summary>TPM ＋ PIN ＋ 啟動金鑰。</summary>
    public const int ProtectorTpmPinStartupKey = 6;
    /// <summary>僅 TPM（另一個列舉值，部分版本使用）。</summary>
    public const int ProtectorTpmOnly = 7;
    /// <summary>復原密碼（RecoveryPassword，與數值密碼同為「可抄下來的那種」）。</summary>
    public const int ProtectorRecoveryPassword = 8;
    /// <summary>憑證型。</summary>
    public const int ProtectorCertificate = 9;

    /// <summary>備份狀態：復原資訊是否已掛到網域／雲端。</summary>
    public enum BackupState
    {
        /// <summary>沒有可備份的復原保護器（例如只用了 TPM）——不適用，不是缺陷。</summary>
        NoRecoveryProtector,
        /// <summary>有復原保護器，且已備份到 AD／Entra。</summary>
        BackedUp,
        /// <summary>有復原保護器，但<b>沒有</b>備份出去。</summary>
        NotBackedUp,
        /// <summary>有復原保護器，但備份狀態讀不到（權限或版本限制）。</summary>
        Unknown,
    }

    /// <summary>
    /// 一台機器上所有卷的復原保護器輪廓。
    /// </summary>
    /// <param name="Volumes">有復原保護器的卷數。</param>
    /// <param name="RecoveryProtectors">復原類保護器（數值密碼／復原密碼）總數。</param>
    /// <param name="TpmProtectors">TPM 類保護器總數。</param>
    /// <param name="Backup">備份狀態。</param>
    /// <param name="SuspendedVolumes">保護已暫停的卷數（暫停期間金鑰等於沒在用）。</param>
    public sealed record RecoveryProfile(
        int Volumes, int RecoveryProtectors, int TpmProtectors,
        BackupState Backup, int SuspendedVolumes);

    public delegate RecoveryProfile? Probe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static HardwareFact Collect(DateTimeOffset at, Probe? probe = null)
    {
        RecoveryProfile? p;
        try { p = (probe ?? ReadRecoveryProfile)(); }
        catch { p = null; }

        if (p is null)
            return Unavailable(at, FactAvailability.ReadError,
                "BitLocker 保護器查詢失敗——讀不到就是不猜（不把讀不到當成沒有金鑰）");

        var v = p;

        if (v.Volumes == 0 && v.RecoveryProtectors == 0 && v.TpmProtectors == 0)
            return Unavailable(at, FactAvailability.NotApplicable,
                "本機沒有回報任何 BitLocker 保護器——不適用（沒有加密卷，或此版本／SKU 未提供 BitLocker）；" +
                "這不是缺陷，只代表沒有東西可備份");

        string backupText = v.Backup switch
        {
            BackupState.BackedUp => "已備份（掛到 AD／Entra）",
            BackupState.NotBackedUp =>
                "<b>未備份</b>——復原金鑰只存在這台機器上。" +
                "換主機板、重灌、或 TPM 清除之後就再也拿不到它；" +
                "這是本項唯一一個「值得現在處理」的結論，但怎麼處理（列印／存到組織保管庫）由人決定，本工具不動手",
            BackupState.Unknown => "備份狀態<b>讀不到</b>（權限或提供者版本限制）——" +
                "「讀不到」與「沒備份」是兩件事，不互相冒充",
            _ => "無復原保護器（例如只以 TPM 保護）——<b>不適用，不是缺陷</b>",
        };

        string suspended = v.SuspendedVolumes > 0
            ? $"・<b>保護暫停中的卷 {v.SuspendedVolumes} 個</b>（暫停期間加密不生效，常用於韌體更新，但不該長期停在那裡）"
            : "";

        return new HardwareFact(RecoveryKey, Cat, "BitLocker 復原金鑰輪廓",
            $"復原保護器 {v.RecoveryProtectors} 把・TPM 類保護器 {v.TpmProtectors} 把・" +
            $"涉及的卷 {v.Volumes} 個{suspended}・備份狀態：{backupText}。" +
            "<b>金鑰值永不讀取、永不輸出、永不落地</b>——本項只數保護器的型別與數量，" +
            "查詢欄位刻意不含 NumericalPassword／RecoveryPassword 與保護器識別碼。" +
            "有幾把金鑰不等於金鑰有效（金鑰輪替過但舊的還在，是常見狀態），本項不判有效性。",
            "把", Source, FactTrustLevel.Reported, false, at, v.RecoveryProtectors);
    }

    // ── 判讀（可單獨測試的純函式）─────────────────────────────────────────

    /// <summary>
    /// 把保護器型別清單收成輪廓。<b>這是本項的判讀核心</b>，刻意做成純函式：
    /// 什麼算「復原類」、什麼算「TPM 類」、備份狀態該給哪一態，都要能單獨驗。
    /// </summary>
    /// <param name="protectorsByVolume">每個卷的保護器型別清單。</param>
    /// <param name="suspendedVolumes">保護暫停的卷數。</param>
    /// <param name="backupReadable">備份狀態是否讀得到（false＝提供者不給，走 Unknown）。</param>
    /// <param name="backupReported">提供者回報的備份狀態（僅在 backupReadable 為 true 時參考）。</param>
    public static RecoveryProfile Summarize(
        IReadOnlyList<IReadOnlyList<int>> protectorsByVolume,
        int suspendedVolumes = 0,
        bool backupReadable = true,
        bool backupReported = false)
    {
        int volumes = 0, recovery = 0, tpm = 0;
        foreach (var protectors in protectorsByVolume)
        {
            int r = protectors.Count(IsRecoveryProtector);
            int t = protectors.Count(IsTpmProtector);
            if (r > 0 || t > 0) volumes++;
            recovery += r;
            tpm += t;
        }

        BackupState backup = recovery == 0
            ? BackupState.NoRecoveryProtector
            : !backupReadable ? BackupState.Unknown
            : backupReported ? BackupState.BackedUp
            : BackupState.NotBackedUp;

        return new RecoveryProfile(volumes, recovery, tpm, backup, suspendedVolumes);
    }

    /// <summary>數值密碼與復原密碼是「可以抄在紙上的那種」——有它就能解鎖，所以算復原類。</summary>
    public static bool IsRecoveryProtector(int type)
        => type is ProtectorNumericalPassword or ProtectorRecoveryPassword;

    /// <summary>需要 TPM（可能加 PIN 或啟動金鑰）才能解鎖的保護器——綁機器，抄不走。</summary>
    public static bool IsTpmProtector(int type)
        => type is ProtectorTpm or ProtectorTpmPin or ProtectorTpmStartupKey
                or ProtectorTpmPinStartupKey or ProtectorTpmOnly;

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// WMI 通路：逐卷取保護器型別。<b>查詢不含任何金鑰欄位</b>——
    /// 這裡是「金鑰永不讀」那條紅線在程式碼上的落點。
    /// </summary>
    internal static RecoveryProfile? ReadRecoveryProfile()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");

            var perVolume = new List<IReadOnlyList<int>>();
            int suspended = 0;
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    // ProtectionStatus 0 ＝ 保護關閉／暫停；與「有沒有保護器」是兩件事。
                    if (o["ProtectionStatus"] is { } ps && Convert.ToUInt32(ps) == 0) suspended++;

                    var list = new List<int>();
                    try
                    {
                        using var inParams = o.GetMethodParameters("GetProtectors");
                        using var outParams = o.InvokeMethod("GetProtectors", inParams, null);
                        if (outParams?["Protectors"] is ManagementBaseObject[] arr)
                            foreach (var p in arr)
                                using (p)
                                {
                                    // 只取型別整數；ProtectorID 與任何金鑰欄位一律不碰。
                                    if (p["Type"] is { } t) list.Add(Convert.ToInt32(t));
                                }
                    }
                    catch
                    {
                        // 單一卷的保護器列舉失敗，不影響其他卷——但該卷不計入，值裡由數量差異看得出來。
                    }
                    perVolume.Add(list);
                }
            }

            if (perVolume.Count == 0) return new RecoveryProfile(0, 0, 0, BackupState.NoRecoveryProtector, 0);

            // 備份狀態取自登錄的政策宣告；提供者不給就回 Unknown 而不是「沒備份」。
            var (readable, reported) = ReadBackupPolicy();
            return Summarize(perVolume, suspended, readable, reported);
        }
        catch (ManagementException)
        {
            return null;   // 命名空間不存在／查詢失敗 → 由呼叫端標三態
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 備份狀態的政策宣告：<c>OSRecovery</c> 機碼下的 <c>RequireActiveDirectoryBackup</c>／
    /// <c>OSEscrowKeyBackup</c>。機碼不存在＝<b>讀不到</b>（不是「沒備份」）。
    /// </summary>
    internal static (bool Readable, bool Reported) ReadBackupPolicy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\FVE");
            if (key is null) return (false, false);   // 沒開群組原則 → 讀不到，不冒充沒備份
            bool ad = ToBool(key.GetValue("RequireActiveDirectoryBackup"));
            bool escrow = ToBool(key.GetValue("OSEscrowKeyBackup"));
            return (true, ad || escrow);
        }
        catch
        {
            return (false, false);
        }
    }

    private static bool ToBool(object? v) => v is int i && i != 0;

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(RecoveryKey, Cat, "BitLocker 復原金鑰輪廓", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
