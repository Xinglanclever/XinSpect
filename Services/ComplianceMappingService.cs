using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>合規控制項的判定結果。缺資料一律 <see cref="Unverifiable"/>，不得當成通過。</summary>
public enum ComplianceStatus
{
    Satisfied,
    NotSatisfied,
    Unverifiable,
    NotApplicable,
}

/// <summary>ATT&CK 曝險判定。<see cref="Mitigated"/> 只表示「本次看到的緩解措施成立」，不是安全保證。</summary>
public enum AttackExposure
{
    Mitigated,
    Exposed,
    Unknown,
    NotApplicable,
}

/// <summary>一條控制項引用的證據：鍵、可用性、數值（若有的話）、短評。</summary>
public sealed record ComplianceEvidence(string Key, string Availability, string? Numeric, string? Note);

/// <summary>一條合規控制項的評估結果。</summary>
public sealed record ComplianceControl(
    string Id, string Framework, string Title, string Requirement,
    ComplianceStatus Status, string Reason, IReadOnlyList<ComplianceEvidence> Evidence);

/// <summary>一條 ATT&CK 技術的曝險評估。</summary>
public sealed record AttackTechnique(
    string Id, string Name, AttackExposure Exposure, string Reason, IReadOnlyList<string> FactKeys);

/// <summary>整份合規／ATT&CK 對照。</summary>
public sealed record ComplianceReport(
    IReadOnlyList<ComplianceControl> Controls,
    IReadOnlyList<AttackTechnique> Techniques);

/// <summary>
/// 把<b>既有事實</b>對到 NIST SP 800-147／155／193 與 MITRE ATT&amp;CK，不新增任何硬體讀取。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一支的價值是「把已經量到的東西翻成標準語言」，不是「再量一次」。</b>
/// 它只讀 <see cref="HardwareFact"/>，沒有任何 P/Invoke、沒有驅動、沒有網路。
/// 因此它的結論強度上限就是來源事實的強度：事實讀不到，控制項就是
/// <see cref="ComplianceStatus.Unverifiable"/>，<b>不會因為「沒看到問題」而變成 Satisfied</b>。
/// </para>
/// <para>
/// <b>刻意維持 Unverifiable 的三條控制項：</b>更新簽章真實性（800-147）、
/// 遠端 Quote（800-155）、韌體復原（800-193）。前者需要驗簽鏈、第二者需要真的做一次
/// TPM Quote、第三者需要真的觸發復原——本工具三者都沒做，所以不給綠燈。
/// 把「工具沒做」寫成「機器沒問題」是這一支最要避免的錯。
/// </para>
/// </remarks>
public static class ComplianceMappingService
{
    public const int SchemaVersion = 1;

    // 事實鍵常數：名稱含 Key，會被 Facts 目錄掃描器看到；全部已在 FactKeyCatalog 註冊。
    private const string BiosCntlKey = "chipset.bios_cntl";
    private const string SmramcKey = "chipset.smramc";
    private const string SpiPrrKey = "spi.prr";
    private const string SpiWriteSurfaceKey = "spi.write_surface";
    private const string MeasuredBootKey = "bt.measured_boot";
    private const string ReplayKey = "tpm.replay.summary";
    private const string SecureBootUefiKey = "uefi.secure_boot";
    private const string SecureBootRegistryKey = "platform.secure_boot";
    private const string DbCountKey = "uefi.db.count";
    private const string DbxCountKey = "uefi.dbx.count";
    private const string TpmPresentKey = "tpm.present";
    private const string TpmAttestKey = "tpm.attest.capable";
    private const string ByovdHitsKey = "byovd.hits";
    private const string VbsStateKey = "sec.vbs.state";
    private const string RecoveryKey = "bt.recovery";
    private const string CapsuleHistoryKey = "fw.capsule.history";

    // 控制項識別碼：刻意用大寫開頭、不含小寫點分鍵的形狀，避免被事實鍵掃描器誤認。
    private const string Nist147WriteProtection = "NIST-800-147-WP";
    private const string Nist147Smram = "NIST-800-147-SMM";
    private const string Nist147ProtectedRanges = "NIST-800-147-PRR";
    private const string Nist147UpdateAuth = "NIST-800-147-UPD";
    private const string Nist155MeasuredBoot = "NIST-800-155-MEASURE";
    private const string Nist155Replay = "NIST-800-155-REPLAY";
    private const string Nist155SecureBoot = "NIST-800-155-SB";
    private const string Nist155Quote = "NIST-800-155-QUOTE";
    private const string Nist193Protect = "NIST-800-193-PROTECT";
    private const string Nist193Detect = "NIST-800-193-DETECT";
    private const string Nist193Recover = "NIST-800-193-RECOVER";

    private const string Nist147 = "NIST SP 800-147";
    private const string Nist155 = "NIST SP 800-155";
    private const string Nist193 = "NIST SP 800-193";

    private static readonly Regex MismatchCount = new(@"(?<!不)一致\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex InconsistentCount = new(@"不一致\s*(\d+)", RegexOptions.Compiled);

    /// <summary>評估全部控制項與 ATT&amp;CK 技術；純函式，不做 I/O。</summary>
    public static ComplianceReport Evaluate(IReadOnlyList<HardwareFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var map = facts
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var controls = new List<ComplianceControl>
        {
            BiosWriteProtection(map),
            SmramProtection(map),
            ProtectedRanges(map),
            UpdateAuthenticity(map),
            MeasuredBoot(map),
            ReplayConsistency(map),
            SecureBoot(map),
            RemoteQuote(map),
            ResiliencyProtect(map),
            ResiliencyDetect(map),
            ResiliencyRecover(map),
        };

        return new ComplianceReport(controls,
        [
            Technique1542(map),
            Technique1495(map),
            Technique1014(map),
            Technique1562(map),
            Technique1553(map),
        ]);
    }

    // ── NIST SP 800-147：BIOS 完整性保護 ────────────────────────────────

    private static ComplianceControl BiosWriteProtection(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var bios = Find(map, BiosCntlKey, evidence);
        var surface = Find(map, SpiWriteSurfaceKey, evidence);

        if (surface is { Availability: FactAvailability.Present })
        {
            string text = surface.Value;
            if (text.Contains("未保護", StringComparison.Ordinal))
                return Control(Nist147WriteProtection, Nist147, "BIOS 寫入保護",
                    "主機軟體對 BIOS 區的寫入必須受到硬體機制限制。",
                    ComplianceStatus.NotSatisfied,
                    "SPI 寫入面綜合裁決為「未保護」：BLE=0，任何 ring0 都能寫 BIOS。", evidence);

            if (text.Contains("暴露面", StringComparison.Ordinal))
                return Control(Nist147WriteProtection, Nist147, "BIOS 寫入保護",
                    "主機軟體對 BIOS 區的寫入必須受到硬體機制限制。",
                    ComplianceStatus.NotSatisfied,
                    "主要鎖存在，但仍有暴露面：" + ExposureTail(text), evidence);

            if (text.Contains("最強保護", StringComparison.Ordinal)
                || text.Contains("有鎖保護", StringComparison.Ordinal))
                return Control(Nist147WriteProtection, Nist147, "BIOS 寫入保護",
                    "主機軟體對 BIOS 區的寫入必須受到硬體機制限制。",
                    ComplianceStatus.Satisfied,
                    "SPI 寫入面綜合裁決顯示鎖定生效，且未列出暴露面。", evidence);

            return Control(Nist147WriteProtection, Nist147, "BIOS 寫入保護",
                "主機軟體對 BIOS 區的寫入必須受到硬體機制限制。",
                ComplianceStatus.Unverifiable,
                "SPI 寫入面有值，但文字不是已知的裁決形狀——不臆測。", evidence);
        }

        if (bios is { Availability: FactAvailability.Present })
        {
            if (bios.Value.Contains("未保護", StringComparison.Ordinal))
                return Control(Nist147WriteProtection, Nist147, "BIOS 寫入保護",
                    "主機軟體對 BIOS 區的寫入必須受到硬體機制限制。",
                    ComplianceStatus.NotSatisfied,
                    "BIOS_CNTL 顯示未保護：BLE=0。", evidence);
            if (bios.Value.Contains("最強保護", StringComparison.Ordinal)
                || bios.Value.Contains("有鎖保護", StringComparison.Ordinal))
                return Control(Nist147WriteProtection, Nist147, "BIOS 寫入保護",
                    "主機軟體對 BIOS 區的寫入必須受到硬體機制限制。",
                    ComplianceStatus.Satisfied,
                    "BIOS_CNTL 顯示 SMM_BWP 或 BLE 鎖定生效；SPI 面不可讀，故未涵蓋 FLOCKDN／PR 的暴露面。", evidence);
        }

        return UnverifiableControl(Nist147WriteProtection, Nist147, "BIOS 寫入保護",
            "主機軟體對 BIOS 區的寫入必須受到硬體機制限制。",
            "BIOS_CNTL 與 SPI 寫入面都讀不到——缺資料不算通過。", evidence);
    }

    private static ComplianceControl SmramProtection(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var f = Find(map, SmramcKey, evidence);
        if (f is not { Availability: FactAvailability.Present })
            return UnverifiableControl(Nist147Smram, Nist147, "SMM 隔離",
                "SMRAM 必須在開機後鎖定，避免作業系統或 ring0 改寫 SMM 程式碼。",
                "SMRAMC 讀不到——缺資料不算通過。", evidence);

        if (f.Value.Contains("未鎖", StringComparison.Ordinal)
            || f.Value.Contains("對外開放", StringComparison.Ordinal))
            return Control(Nist147Smram, Nist147, "SMM 隔離",
                "SMRAM 必須在開機後鎖定，避免作業系統或 ring0 改寫 SMM 程式碼。",
                ComplianceStatus.NotSatisfied, "SMRAMC 顯示 SMRAM 未鎖或對外開放。", evidence);

        if (f.Value.Contains("已鎖", StringComparison.Ordinal))
            return Control(Nist147Smram, Nist147, "SMM 隔離",
                "SMRAM 必須在開機後鎖定，避免作業系統或 ring0 改寫 SMM 程式碼。",
                ComplianceStatus.Satisfied, "SMRAMC 顯示 D_LCK=1 且無 D_OPEN 暴露。", evidence);

        return UnverifiableControl(Nist147Smram, Nist147, "SMM 隔離",
            "SMRAM 必須在開機後鎖定，避免作業系統或 ring0 改寫 SMM 程式碼。",
            "SMRAMC 有值，但文字不是已知的裁決形狀——不臆測。", evidence);
    }

    private static ComplianceControl ProtectedRanges(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var f = Find(map, SpiPrrKey, evidence);
        if (f is not { Availability: FactAvailability.Present })
            return UnverifiableControl(Nist147ProtectedRanges, Nist147, "SPI 受保護範圍",
                "BIOS 區應由至少一個啟用的 PR 範圍以寫保護涵蓋。",
                "spi.prr 讀不到——缺資料不算通過。", evidence);

        if (f.Value.Contains("全部停用", StringComparison.Ordinal))
            return Control(Nist147ProtectedRanges, Nist147, "SPI 受保護範圍",
                "BIOS 區應由至少一個啟用的 PR 範圍以寫保護涵蓋。",
                ComplianceStatus.NotSatisfied, "PR0-4 全部停用，SPI 硬體層沒有範圍保護。", evidence);

        if (f.Value.Contains("寫+讀保護", StringComparison.Ordinal)
            || f.Value.Contains("寫保護", StringComparison.Ordinal))
            return Control(Nist147ProtectedRanges, Nist147, "SPI 受保護範圍",
                "BIOS 區應由至少一個啟用的 PR 範圍以寫保護涵蓋。",
                ComplianceStatus.Satisfied, "至少一個 PR 範圍以寫保護涵蓋。", evidence);

        if (f.Value.Contains("讀保護", StringComparison.Ordinal))
            return Control(Nist147ProtectedRanges, Nist147, "SPI 受保護範圍",
                "BIOS 區應由至少一個啟用的 PR 範圍以寫保護涵蓋。",
                ComplianceStatus.NotSatisfied, "PR 範圍只有讀保護，沒有寫保護。", evidence);

        return UnverifiableControl(Nist147ProtectedRanges, Nist147, "SPI 受保護範圍",
            "BIOS 區應由至少一個啟用的 PR 範圍以寫保護涵蓋。",
            "spi.prr 有值，但文字不是已知的裁決形狀——不臆測。", evidence);
    }

    private static ComplianceControl UpdateAuthenticity(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        Find(map, CapsuleHistoryKey, evidence);
        Find(map, DbxCountKey, evidence);
        return UnverifiableControl(Nist147UpdateAuth, Nist147, "韌體更新真實性",
            "韌體更新必須先驗證廠商簽章，才允許寫入。",
            "本工具只列膠囊更新歷史與 dbx 條數，**不驗證更新映像的簽章鏈**——"
            + "沒有做驗簽就不給綠燈；這是工具界線，不是機器缺陷。", evidence);
    }

    // ── NIST SP 800-155：BIOS 量測開機 ──────────────────────────────────

    private static ComplianceControl MeasuredBoot(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var f = Find(map, MeasuredBootKey, evidence);
        if (f is null)
            return UnverifiableControl(Nist155MeasuredBoot, Nist155, "韌體量測",
                "韌體必須把 CRTM 與開機元件量測進 TPM PCR。",
                "沒有 bt.measured_boot——缺資料不算通過。", evidence);

        if (f.Availability == FactAvailability.Present && f.NumericValue is > 0)
            return Control(Nist155MeasuredBoot, Nist155, "韌體量測",
                "韌體必須把 CRTM 與開機元件量測進 TPM PCR。",
                ComplianceStatus.Satisfied, "至少一個關鍵 PCR 已延伸（有量測證據）。", evidence);

        if (f.Availability == FactAvailability.NotApplicable)
            return Control(Nist155MeasuredBoot, Nist155, "韌體量測",
                "韌體必須把 CRTM 與開機元件量測進 TPM PCR。",
                ComplianceStatus.NotSatisfied,
                "關鍵 PCR 可讀但全為零——量測鏈沒有開始（「有 TPM」不等於「有量測」）。", evidence);

        return UnverifiableControl(Nist155MeasuredBoot, Nist155, "韌體量測",
            "韌體必須把 CRTM 與開機元件量測進 TPM PCR。",
            $"bt.measured_boot 目前是 {f.Availability}——缺資料不算通過。", evidence);
    }

    private static ComplianceControl ReplayConsistency(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var f = Find(map, ReplayKey, evidence);
        if (f is not { Availability: FactAvailability.Present })
            return UnverifiableControl(Nist155Replay, Nist155, "TCG log 與 PCR 一致",
                "TCG 事件 log 重放後必須與 TPM 實讀 PCR 一致。",
                "tpm.replay.summary 讀不到——缺資料不算通過。", evidence);

        int? inconsistent = ParseCount(InconsistentCount, f.Value);
        int? consistent = ParseCount(MismatchCount, f.Value);
        if (inconsistent is > 0)
            return Control(Nist155Replay, Nist155, "TCG log 與 PCR 一致",
                "TCG 事件 log 重放後必須與 TPM 實讀 PCR 一致。",
                ComplianceStatus.NotSatisfied,
                $"重放與實讀有 {inconsistent} 顆 PCR 不一致——不一致是事實，不是入侵判決，但也不能算通過。", evidence);
        if (inconsistent == 0 && consistent is > 0)
            return Control(Nist155Replay, Nist155, "TCG log 與 PCR 一致",
                "TCG 事件 log 重放後必須與 TPM 實讀 PCR 一致。",
                ComplianceStatus.Satisfied, $"{consistent} 顆 PCR 重放與實讀一致，且沒有不一致。", evidence);

        return UnverifiableControl(Nist155Replay, Nist155, "TCG log 與 PCR 一致",
            "TCG 事件 log 重放後必須與 TPM 實讀 PCR 一致。",
            "摘要裡沒有可判讀的一致／不一致計數——不臆測。", evidence);
    }

    private static ComplianceControl SecureBoot(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var uefi = Find(map, SecureBootUefiKey, evidence);
        var registry = Find(map, SecureBootRegistryKey, evidence);
        Find(map, DbCountKey, evidence);
        Find(map, DbxCountKey, evidence);

        bool? enabled = null;
        string source = "";
        if (uefi is { Availability: FactAvailability.Present })
        {
            enabled = uefi.Value.Contains("是", StringComparison.Ordinal) ? true
                : uefi.Value.Contains("否", StringComparison.Ordinal) ? false : null;
            source = "UEFI 變數";
        }
        if (enabled is null && registry is { Availability: FactAvailability.Present })
        {
            enabled = registry.Value.Contains("開啟", StringComparison.Ordinal) ? true
                : registry.Value.Contains("關閉", StringComparison.Ordinal) ? false : null;
            source = "登錄檔";
        }

        if (enabled is true)
            return Control(Nist155SecureBoot, Nist155, "Secure Boot",
                "開機時必須以受信任金鑰驗證開機元件的簽章。",
                ComplianceStatus.Satisfied, $"Secure Boot 已啟用（來源：{source}）。", evidence);
        if (enabled is false)
            return Control(Nist155SecureBoot, Nist155, "Secure Boot",
                "開機時必須以受信任金鑰驗證開機元件的簽章。",
                ComplianceStatus.NotSatisfied, $"Secure Boot 未啟用（來源：{source}）。", evidence);

        return UnverifiableControl(Nist155SecureBoot, Nist155, "Secure Boot",
            "開機時必須以受信任金鑰驗證開機元件的簽章。",
            "UEFI 變數與登錄檔都給不出可判讀的啟用狀態——缺資料不算通過。", evidence);
    }

    private static ComplianceControl RemoteQuote(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        Find(map, TpmPresentKey, evidence);
        Find(map, TpmAttestKey, evidence);
        return UnverifiableControl(Nist155Quote, Nist155, "遠端完整性證明",
            "應能以 TPM Quote 對遠端挑戰簽章，證明 PCR 現值。",
            "本工具只讀 TPM 是否存在與是否宣稱支援 attestation，**沒有發出 Quote、也沒有驗證簽章**——"
            + "能力宣告不等於已驗證。", evidence);
    }

    // ── NIST SP 800-193：平台韌體韌性 ──────────────────────────────────

    private static ComplianceControl ResiliencyProtect(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var surface = Find(map, SpiWriteSurfaceKey, evidence);
        Find(map, BiosCntlKey, evidence);
        Find(map, SpiPrrKey, evidence);

        if (surface is not { Availability: FactAvailability.Present })
            return UnverifiableControl(Nist193Protect, Nist193, "保護",
                "平台韌體與關鍵設定必須受硬體寫入保護。",
                "SPI 寫入面讀不到——缺資料不算通過。", evidence);

        if (surface.Value.Contains("未保護", StringComparison.Ordinal)
            || surface.Value.Contains("暴露面", StringComparison.Ordinal))
            return Control(Nist193Protect, Nist193, "保護",
                "平台韌體與關鍵設定必須受硬體寫入保護。",
                ComplianceStatus.NotSatisfied,
                surface.Value.Contains("暴露面", StringComparison.Ordinal)
                    ? "仍有暴露面：" + ExposureTail(surface.Value)
                    : "BIOS 寫入面未保護。", evidence);

        if (surface.Value.Contains("最強保護", StringComparison.Ordinal)
            || surface.Value.Contains("有鎖保護", StringComparison.Ordinal))
            return Control(Nist193Protect, Nist193, "保護",
                "平台韌體與關鍵設定必須受硬體寫入保護。",
                ComplianceStatus.Satisfied,
                "BIOS 寫入面鎖定生效且未列出暴露面；範圍僅涵蓋本工具能看到的 BIOS 區。", evidence);

        return UnverifiableControl(Nist193Protect, Nist193, "保護",
            "平台韌體與關鍵設定必須受硬體寫入保護。",
            "SPI 寫入面有值，但文字不是已知的裁決形狀——不臆測。", evidence);
    }

    private static ComplianceControl ResiliencyDetect(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var measured = Find(map, MeasuredBootKey, evidence);
        var replay = Find(map, ReplayKey, evidence);

        if (measured is { Availability: FactAvailability.Present, NumericValue: > 0 }
            && replay is { Availability: FactAvailability.Present }
            && ParseCount(InconsistentCount, replay.Value) == 0
            && ParseCount(MismatchCount, replay.Value) is > 0)
            return Control(Nist193Detect, Nist193, "偵測",
                "平台必須能偵測韌體遭未授權變更。",
                ComplianceStatus.Satisfied, "量測開機有證據，且 TCG log 重放與 PCR 實讀一致。", evidence);

        if (measured is { Availability: FactAvailability.NotApplicable }
            || (replay is { Availability: FactAvailability.Present }
                && ParseCount(InconsistentCount, replay.Value) is > 0))
            return Control(Nist193Detect, Nist193, "偵測",
                "平台必須能偵測韌體遭未授權變更。",
                ComplianceStatus.NotSatisfied,
                "量測鏈沒有開始，或重放與實讀不一致——偵測能力不成立。", evidence);

        return UnverifiableControl(Nist193Detect, Nist193, "偵測",
            "平台必須能偵測韌體遭未授權變更。",
            "量測或重放其中一項讀不到——缺資料不算通過。", evidence);
    }

    private static ComplianceControl ResiliencyRecover(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var f = Find(map, RecoveryKey, evidence);
        return UnverifiableControl(Nist193Recover, Nist193, "復原",
            "平台必須能在韌體受損後回復到已知良好狀態。",
            f is { Availability: FactAvailability.Present }
                ? "bt.recovery 只描述膠囊更新宣告與 FV 清單，原始碼明寫「未辨識復原區」；"
                  + "本工具也沒有觸發復原——看得到宣告不等於復原能力成立。"
                : "復原路徑事實讀不到——缺資料不算通過。", evidence);
    }

    // ── MITRE ATT&CK 曝險 ────────────────────────────────────────────────

    private static AttackTechnique Technique1542(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var evidence = new List<ComplianceEvidence>();
        var bios = Find(map, BiosCntlKey, evidence);
        if (bios is not { Availability: FactAvailability.Present })
            return new AttackTechnique("T1542.001", "System Firmware", AttackExposure.Unknown,
                "BIOS_CNTL 讀不到——無法判斷寫入保護是否成立。", [BiosCntlKey]);

        if (bios.Value.Contains("未保護", StringComparison.Ordinal))
            return new AttackTechnique("T1542.001", "System Firmware", AttackExposure.Exposed,
                "BIOS_CNTL 顯示未保護：任何 ring0 都能寫 BIOS。", [BiosCntlKey]);

        if (bios.Value.Contains("最強保護", StringComparison.Ordinal)
            || bios.Value.Contains("有鎖保護", StringComparison.Ordinal))
            return new AttackTechnique("T1542.001", "System Firmware", AttackExposure.Mitigated,
                "BIOS_CNTL 顯示 SMM_BWP 或 BLE 鎖定生效；這是 BIOS 寫入面的緩解，不是完整韌體防護。",
                [BiosCntlKey]);

        return new AttackTechnique("T1542.001", "System Firmware", AttackExposure.Unknown,
            "BIOS_CNTL 有值但文字不是已知裁決形狀。", [BiosCntlKey]);
    }

    private static AttackTechnique Technique1495(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var surface = map.GetValueOrDefault(SpiWriteSurfaceKey);
        var measured = map.GetValueOrDefault(MeasuredBootKey);
        bool writeProtected = surface is { Availability: FactAvailability.Present }
            && !surface.Value.Contains("未保護", StringComparison.Ordinal)
            && !surface.Value.Contains("暴露面", StringComparison.Ordinal)
            && (surface.Value.Contains("最強保護", StringComparison.Ordinal)
                || surface.Value.Contains("有鎖保護", StringComparison.Ordinal));
        bool measuredOk = measured is { Availability: FactAvailability.Present, NumericValue: > 0 };

        if (writeProtected && measuredOk)
            return new AttackTechnique("T1495", "Firmware Corruption", AttackExposure.Mitigated,
                "BIOS 寫入面鎖定且量測開機有證據——損毀可被限制並被看見；這是曝險降低，不是免疫。",
                [SpiWriteSurfaceKey, MeasuredBootKey]);

        if (surface is { Availability: FactAvailability.Present }
            && (surface.Value.Contains("未保護", StringComparison.Ordinal)
                || surface.Value.Contains("暴露面", StringComparison.Ordinal)))
            return new AttackTechnique("T1495", "Firmware Corruption", AttackExposure.Exposed,
                "BIOS 寫入面未保護或仍有暴露面。", [SpiWriteSurfaceKey]);

        return new AttackTechnique("T1495", "Firmware Corruption", AttackExposure.Unknown,
            "寫入保護或量測開機至少一項讀不到——無法判斷。", [SpiWriteSurfaceKey, MeasuredBootKey]);
    }

    private static AttackTechnique Technique1014(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var f = map.GetValueOrDefault(ByovdHitsKey);
        if (f is not { Availability: FactAvailability.Present })
            return new AttackTechnique("T1014", "Rootkit", AttackExposure.Unknown,
                "byovd.hits 讀不到（多半是沒有提供封鎖清單）——沒有比對就沒有結論。", [ByovdHitsKey]);

        if (f.NumericValue is 0)
            return new AttackTechnique("T1014", "Rootkit", AttackExposure.Mitigated,
                "載入中的核心模組在這份封鎖清單上 0 命中；這不是「沒有 rootkit」的證明。", [ByovdHitsKey]);
        if (f.NumericValue is > 0)
            return new AttackTechnique("T1014", "Rootkit", AttackExposure.Exposed,
                $"{f.NumericValue:0} 個載入中的核心模組命中微軟建議封鎖清單。", [ByovdHitsKey]);

        return new AttackTechnique("T1014", "Rootkit", AttackExposure.Unknown,
            "byovd.hits 有值但沒有可判讀的命中數。", [ByovdHitsKey]);
    }

    private static AttackTechnique Technique1562(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var f = map.GetValueOrDefault(VbsStateKey);
        if (f is not { Availability: FactAvailability.Present })
            return new AttackTechnique("T1562.001", "Impair Defenses: Disable or Modify Tools",
                AttackExposure.Unknown, "VBS／HVCI 狀態讀不到——無法判斷。", [VbsStateKey]);

        return f.NumericValue switch
        {
            2 => new AttackTechnique("T1562.001", "Impair Defenses: Disable or Modify Tools",
                AttackExposure.Mitigated, "VBS 已啟用且執行中（狀態碼 2）。", [VbsStateKey]),
            0 => new AttackTechnique("T1562.001", "Impair Defenses: Disable or Modify Tools",
                AttackExposure.Exposed, "VBS 未啟用（狀態碼 0）——HVCI 這類核心完整性防護不在位。", [VbsStateKey]),
            1 => new AttackTechnique("T1562.001", "Impair Defenses: Disable or Modify Tools",
                AttackExposure.Unknown, "VBS 已啟用但未執行（狀態碼 1）——政策與生效狀態不一致。", [VbsStateKey]),
            _ => new AttackTechnique("T1562.001", "Impair Defenses: Disable or Modify Tools",
                AttackExposure.Unknown, "VBS 狀態碼沒有可判讀的數值。", [VbsStateKey]),
        };
    }

    private static AttackTechnique Technique1553(IReadOnlyDictionary<string, HardwareFact> map)
    {
        var uefi = map.GetValueOrDefault(SecureBootUefiKey);
        var registry = map.GetValueOrDefault(SecureBootRegistryKey);
        var dbx = map.GetValueOrDefault(DbxCountKey);

        bool? enabled = null;
        if (uefi is { Availability: FactAvailability.Present })
            enabled = uefi.Value.Contains("是", StringComparison.Ordinal) ? true
                : uefi.Value.Contains("否", StringComparison.Ordinal) ? false : null;
        if (enabled is null && registry is { Availability: FactAvailability.Present })
            enabled = registry.Value.Contains("開啟", StringComparison.Ordinal) ? true
                : registry.Value.Contains("關閉", StringComparison.Ordinal) ? false : null;

        if (enabled is true)
            return new AttackTechnique("T1553.006", "Subvert Trust Controls: Code Signing Policy Modification",
                AttackExposure.Mitigated,
                "Secure Boot 已啟用" + (dbx is { Availability: FactAvailability.Present }
                    ? "，且 dbx 撤銷清單可讀。" : "；dbx 撤銷清單讀不到，撤銷面未被涵蓋。"),
                [SecureBootUefiKey, SecureBootRegistryKey, DbxCountKey]);

        if (enabled is false)
            return new AttackTechnique("T1553.006", "Subvert Trust Controls: Code Signing Policy Modification",
                AttackExposure.Exposed, "Secure Boot 未啟用——開機簽章政策可被改寫而不被阻擋。",
                [SecureBootUefiKey, SecureBootRegistryKey]);

        return new AttackTechnique("T1553.006", "Subvert Trust Controls: Code Signing Policy Modification",
            AttackExposure.Unknown, "Secure Boot 狀態讀不到——無法判斷。",
            [SecureBootUefiKey, SecureBootRegistryKey]);
    }

    // ── 共用 ────────────────────────────────────────────────────────────

    private static HardwareFact? Find(IReadOnlyDictionary<string, HardwareFact> map, string key,
        List<ComplianceEvidence> evidence)
    {
        if (!map.TryGetValue(key, out var f))
        {
            evidence.Add(new ComplianceEvidence(key, "Missing", null, "本次收集沒有這把鍵"));
            return null;
        }
        evidence.Add(new ComplianceEvidence(key, f.Availability.ToString(),
            f.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            f.Availability == FactAvailability.Present ? null : f.UnavailableReason));
        return f;
    }

    private static ComplianceControl Control(string id, string framework, string title, string requirement,
        ComplianceStatus status, string reason, List<ComplianceEvidence> evidence)
        => new(id, framework, title, requirement, status, reason, evidence);

    private static ComplianceControl UnverifiableControl(string id, string framework, string title,
        string requirement, string reason, List<ComplianceEvidence> evidence)
        => Control(id, framework, title, requirement, ComplianceStatus.Unverifiable, reason, evidence);

    private static string ExposureTail(string text)
    {
        int i = text.IndexOf("暴露面：", StringComparison.Ordinal);
        return i >= 0 ? text[(i + "暴露面：".Length)..].TrimEnd('。') : "（未列出細節）";
    }

    private static int? ParseCount(Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
    }
}