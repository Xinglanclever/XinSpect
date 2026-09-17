namespace XinSpect;

/// <summary>
/// 安全態勢評分引擎——純函式。
/// 輸入：各類偵測器蓒集到的布林/整數事實。
/// 輸出：六大防線各自的分數與發現清單，以及加權總分。
/// 全部邏輯可用合成輸入單元測試，不碰硬體。
/// </summary>
public static class SecurityScoreEngine
{
    // ── 輸入結構 ────────────────────────────────────────────────────────────────────

    public sealed record DmaFacts(
        bool? HvciEnabled,
        bool? VbsRunning,
        bool? IommuAvailable,
        bool? DmaProtection,
        bool? CredentialGuard,
        bool? BitLockerEnabled,
        bool? BitLockerDmaProtection,
        int? ThunderboltSecurityLevel,
        bool? DmaEnforced
    );

    public sealed record FirmwareFacts(
        bool? SecureBootEnabled,
        bool? SecureBootInSetupMode,
        bool? TestSigningEnabled,
        bool? SpiWriteProtected,
        bool? MeManufacturingMode,
        bool? MeasuredBootPresent,
        string? MeVersion,
        bool? TpmPresent,
        string? TpmVersion,
        bool? UefiBoot,
        bool? BiosPasswordSet
    );

    public sealed record CpuFacts(
        bool? SpecMitigationsEnabled,
        bool? NxEnabled,
        bool? DepEnabled,
        bool? AslrHighEntropy,
        bool? CfgEnabled,
        bool? CetEnabled,
        bool? SmepEnabled,
        bool? SmapEnabled,
        bool? LsassPplEnabled,
        bool? SpecOverrideSafe
    );

    public sealed record StorageFacts(
        bool? BitLockerEnabled,
        bool? NvmeFirmwareBaseline,
        bool? SmartHealthy,
        bool? BitLockerEscrow,
        string? EncryptionMethod
    );

    public sealed record DriverFacts(
        int TotalDrivers,
        int UnsignedDrivers,
        int KnownVulnerableDrivers,
        bool? VulnerableDriverBlocklistEnabled,
        bool? LsassProtected,
        bool? WdacActive,
        bool? KernelDebugOff,
        bool? CoInstallersDisabled,
        bool? StaleDriverCertsClean
    );

    public sealed record SurfaceFacts(
        bool? DefenderRealtimeOn,
        bool? FirewallDomain,
        bool? FirewallPublic,
        bool? FirewallPrivate,
        bool? RdpDisabled,
        bool? AutoLogonOff,
        bool? GuestDisabled,
        bool? AdminSafe,
        bool? PsRestricted,
        bool? WshDisabled,
        bool? RdpNlaRequired,
        bool? SmbV1Disabled,
        bool? WinRmStopped,
        bool? PrintSpoolerStopped,
        int? DaysSinceUpdate
    );

    // ── 主要進入點 ──────────────────────────────────────────────────────────────────

    public static SecurityPosture Evaluate(
        DmaFacts dma, FirmwareFacts fw, CpuFacts cpu,
        StorageFacts stor, DriverFacts drv, SurfaceFacts surface)
    {
        var categories = new List<SecurityCategoryScore>
        {
            EvalDma(dma),
            EvalFirmware(fw),
            EvalCpu(cpu),
            EvalStorage(stor),
            EvalDrivers(drv),
            EvalSurface(surface),
        };

        int total = 0;
        foreach (var c in categories)
            total += c.Score * c.Weight;
        total /= 100;

        string verdict = total switch
        {
            >= 90 => "防禦態勢優良——大多數已知攻擊面已封閉。",
            >= 70 => "防禦態勢尚可——仍有數項可改進的弱點。",
            >= 50 => "防禦態勢偏弱——存在可被利用的顯著缺口。",
            _ => "防禦態勢危險——多條防線未啟用或存在已知漏洞。",
        };

        var recs = HardeningAdvisor.Recommend(categories);

        return new SecurityPosture(
            TotalScore: total,
            Verdict: verdict,
            Categories: categories,
            Recommendations: recs,
            AssessedAt: DateTimeOffset.UtcNow
        );
    }

    // ── 第一防線：DMA 與記憶體保護 (10%) ──────────────────────────────────────

    private static SecurityCategoryScore EvalDma(DmaFacts f)
    {
        var findings = new List<SecurityFinding>();
        int score = 100;

        score -= Check(findings, "dma.hvci", "核心完整性（HVCI）",
            f.HvciEnabled, "HVCI 已啟用", "HVCI 未啟用——核心代碼可被注入",
            SecuritySeverity.Critical, 20,
            "開啟「記憶體完整性」：Windows 安全性 → 裝置安全性 → 核心隔離。");

        score -= Check(findings, "dma.vbs", "虛擬化型安全（VBS）",
            f.VbsRunning, "VBS 正在執行", "VBS 未執行——進階隔離功能無法運作",
            SecuritySeverity.Warning, 15,
            "啟用 VBS：需 Hyper-V 功能 + BIOS 中啟用虛擬化。");

        score -= Check(findings, "dma.iommu", "IOMMU / VT-d",
            f.IommuAvailable, "IOMMU 可用", "IOMMU 不可用——DMA 攻擊無法防禦",
            SecuritySeverity.Critical, 15,
            "在 BIOS/UEFI 中啟用 VT-d (Intel) 或 AMD-Vi (AMD)。");

        score -= Check(findings, "dma.dma-prot", "DMA 保護",
            f.DmaProtection, "Kernel DMA Protection 已啟用", "Kernel DMA Protection 未啟用",
            SecuritySeverity.Warning, 10,
            "需要 UEFI 韌體支援 + IOMMU 已啟用。");

        score -= Check(findings, "dma.dma-enforce", "DMA 重對映強制執行",
            f.DmaEnforced, "DMA 重對映已強制執行", "DMA 重對映未強制執行",
            SecuritySeverity.Warning, 10,
            "啟用 VBS 並確認 DMA 重對映政策已強制執行。");

        score -= Check(findings, "dma.credguard", "Credential Guard",
            f.CredentialGuard, "已啟用", "未啟用——登入憑證可從記憶體提取",
            SecuritySeverity.Warning, 10,
            "需 VBS + 企業版/教育版 Windows。群組原則：Credential Guard 已啟用。");

        score -= Check(findings, "dma.bitlocker-dma", "BitLocker DMA 防護",
            f.BitLockerDmaProtection ?? f.BitLockerEnabled,
            "BitLocker 已啟用（含 DMA 保護）",
            "BitLocker 未啟用或缺少 DMA 防護",
            SecuritySeverity.Warning, 10,
            "啟用 BitLocker 並確認群組原則中「允許新的 DMA 裝置」已關閉。");

        score -= CheckThunderbolt(findings, f.ThunderboltSecurityLevel, 10);

        return MakeCategory("dma", "DMA 與記憶體保護", 10, score, findings);
    }

    private static int CheckThunderbolt(List<SecurityFinding> findings, int? level, int weight)
    {
        if (level is null)
        {
            findings.Add(new SecurityFinding(
                "dma.thunderbolt", "DMA 與記憶體保護", "Thunderbolt",
                "未偵測到 Thunderbolt 控制器（無此攻擊面）。",
                SecuritySeverity.Good, null));
            return 0;
        }
        if (level >= 2)
        {
            findings.Add(new SecurityFinding(
                "dma.thunderbolt", "DMA 與記憶體保護", "Thunderbolt 安全等級",
                $"安全等級 {level}——已限制未授權裝置的 DMA 存取。",
                SecuritySeverity.Good, null));
            return 0;
        }
        int deduct = weight * (2 - level.Value) / 2;
        findings.Add(new SecurityFinding(
            "dma.thunderbolt", "DMA 與記憶體保護", "Thunderbolt 安全等級不足",
            $"安全等級 {level}——未授權裝置可直接 DMA 存取系統記憶體。",
            SecuritySeverity.Warning,
            "在 BIOS/UEFI 中將 Thunderbolt Security Level 設為 Secure Connect 或更高。"));
        return deduct;
    }

    // ── 第二防線：韌體與啟動鏈 (20%) ───────────────────────────────────────

    private static SecurityCategoryScore EvalFirmware(FirmwareFacts f)
    {
        var findings = new List<SecurityFinding>();
        int score = 100;

        // 保留 null：`== true && != true` 這種寫法會把 null 吸收成 false，
        // 於是「讀不到 UEFI 變數」（例如沒提權、或根本是 Legacy 開機）會被報成
        // 「Secure Boot 未啟用——引導kit 可不受阻礙載入」的 Critical 並扣 25 分——
        // 把無法判定講成確定失敗，是這一頁最不該犯的錯。
        score -= Check(findings, "fw.secboot", "Secure Boot",
            f.SecureBootEnabled is bool sb ? sb && f.SecureBootInSetupMode != true : null,
            "Secure Boot 已啟用（部署模式）",
            f.SecureBootInSetupMode == true
                ? "Secure Boot 處於 Setup Mode——任何人都能修改金鑰"
                : "Secure Boot 未啟用——引導kit 可不受阻礙載入",
            SecuritySeverity.Critical, 25,
            "在 BIOS/UEFI 中啟用 Secure Boot 並確認為 Deployed Mode。");

        score -= Check(findings, "fw.testsign", "測試簽章模式",
            f.TestSigningEnabled == false,
            "測試簽章未啟用",
            "測試簽章模式已啟用——任何驅動都能載入核心",
            SecuritySeverity.Critical, 15,
            "以系統管理員執行：bcdedit /set testsigning off 並重新開機。");

        score -= Check(findings, "fw.tpm-present", "TPM 晶片存在",
            f.TpmPresent,
            "偵測到 TPM 晶片",
            "未偵測到 TPM 晶片",
            SecuritySeverity.Critical, 15,
            "安裝或啟用主機板上的 TPM 模組。");

        // TPM version check (special: string comparison)
        if (f.TpmVersion is not null)
        {
            bool is20 = f.TpmVersion.StartsWith("2.", StringComparison.Ordinal);
            if (is20)
                findings.Add(new SecurityFinding("fw.tpm-ver", "", "TPM 版本",
                    $"TPM {f.TpmVersion}", SecuritySeverity.Good, null));
            else
            {
                score -= 8;
                findings.Add(new SecurityFinding("fw.tpm-ver", "", "TPM 版本低於 2.0",
                    $"TPM {f.TpmVersion}——版本低於 2.0，無法滿足 Windows 11 最低需求",
                    SecuritySeverity.Warning,
                    "升級至 TPM 2.0 以獲得完整的安全功能支援。"));
            }
        }

        score -= Check(findings, "fw.uefi", "UEFI 開機模式",
            f.UefiBoot,
            "系統以 UEFI 模式開機",
            "系統以 Legacy/CSM 模式開機，無法啟用 Secure Boot",
            SecuritySeverity.Warning, 10,
            "將磁碟轉換為 GPT 並以 UEFI 模式開機。");

        score -= Check(findings, "fw.bios-pwd", "BIOS 密碼保護",
            f.BiosPasswordSet,
            "BIOS 已設定管理員密碼",
            "BIOS 未設定密碼，實體存取風險",
            SecuritySeverity.Advisory, 5,
            "在 BIOS/UEFI 設定中設定管理員密碼。");

        score -= Check(findings, "fw.spi", "SPI 寫保護",
            f.SpiWriteProtected,
            "SPI 快閃記憶體寫保護已啟用",
            "SPI 寫保護未啟用",
            SecuritySeverity.Warning, 10,
            "更新至最新 BIOS 版本並確認 BIOS Guard 已啟用。");

        score -= Check(findings, "fw.measured-boot", "Measured Boot",
            f.MeasuredBootPresent,
            "Measured Boot 記錄可用",
            "Measured Boot 不可用",
            SecuritySeverity.Advisory, 7,
            "需 TPM 2.0 + UEFI 支援。確認 BIOS 中 TPM 已啟用。");

        if (f.MeVersion is not null)
            findings.Add(new SecurityFinding(
                "fw.me-ver", "韌體與啟動鏈", "Intel ME 版本",
                $"ME 韌體版本：{f.MeVersion}",
                SecuritySeverity.Good, null));

        return MakeCategory("firmware", "韌體與啟動鏈", 20, score, findings);
    }

    // ── 第三防線：CPU 與晶片組 (15%) ────────────────────────────────────────

    private static SecurityCategoryScore EvalCpu(CpuFacts f)
    {
        var findings = new List<SecurityFinding>();
        int score = 100;

        score -= Check(findings, "cpu.specmit", "Spectre/Meltdown 緩解",
            f.SpecMitigationsEnabled,
            "推測執行緩解已啟用（IBRS/STIBP/SSBD）",
            "推測執行緩解未完全啟用",
            SecuritySeverity.Warning, 12,
            "更新 Windows 並確認微碼已為最新版。");

        score -= Check(findings, "cpu.spec-override", "推測性執行緩解覆寫",
            f.SpecOverrideSafe,
            "未偵測到停用緩解的覆寫設定",
            "登錄檔中存在停用 Spectre/Meltdown 緩解的覆寫",
            SecuritySeverity.Critical, 12,
            "刪除 FeatureSettingsOverride 與 FeatureSettingsOverrideMask 登錄值。");

        score -= Check(findings, "cpu.nx", "NX / XD 位元",
            f.NxEnabled,
            "NX（不可執行）位元已啟用",
            "NX 位元未啟用——可執行堆疊區段",
            SecuritySeverity.Critical, 10,
            "在 BIOS 中啟用 Execute Disable Bit / NX Mode。");

        score -= Check(findings, "cpu.dep", "DEP（資料執行防止）",
            f.DepEnabled,
            "DEP 已啟用",
            "DEP 未啟用",
            SecuritySeverity.Warning, 8,
            "確認 bcdedit nx 設定不是 AlwaysOff。");

        score -= Check(findings, "cpu.aslr", "ASLR 高熵",
            f.AslrHighEntropy,
            "ASLR 高熵已啟用",
            "ASLR 高熵未啟用",
            SecuritySeverity.Advisory, 6,
            "登錄檔：MoveImages 設為 0xFFFFFFFF。");

        score -= Check(findings, "cpu.cfg", "Control Flow Guard",
            f.CfgEnabled,
            "CFG 已啟用",
            "CFG 未偵測到",
            SecuritySeverity.Warning, 8,
            "確認應用程式已以 /guard:cf 編譯。");

        score -= Check(findings, "cpu.smep", "SMEP",
            f.SmepEnabled,
            "Supervisor Mode Execution Prevention 已啟用",
            "SMEP 未啟用",
            SecuritySeverity.Warning, 8,
            "CPU 硬體功能，需 Haswell 以上。");

        score -= Check(findings, "cpu.smap", "SMAP",
            f.SmapEnabled,
            "Supervisor Mode Access Prevention 已啟用",
            "SMAP 未啟用",
            SecuritySeverity.Advisory, 5,
            "CPU 硬體功能，配合 HVCI 使用。");

        score -= Check(findings, "cpu.lsass-ppl", "LSASS 保護程序 (PPL)",
            f.LsassPplEnabled,
            "LSASS 以 PPL 模式執行，憑證傾印受阻",
            "LSASS 未啟用 PPL，易受 mimikatz 類工具攻擊",
            SecuritySeverity.Critical, 12,
            "設定 HKLM\\...\\Lsa\\RunAsPPL = 1。");

        // CET 只有一個事實欄位（CpuFacts.CetEnabled），分成兩條檢查等於同一個事實扣兩次；
        // 合併為一條，權重取原兩條之和（10+9）以維持防線總權重不變。
        score -= Check(findings, "cpu.kernel-cet", "核心模式硬體堆疊保護 (CET)",
            f.CetEnabled,
            "核心模式 CET Shadow Stack 已啟用",
            "核心模式 CET 未啟用",
            SecuritySeverity.Warning, 19,
            "需 11 代以上 Intel 或 Zen 3 以上 AMD CPU，並啟用 HVCI。");

        return MakeCategory("cpu", "CPU 緩解與記憶體防護", 15, score, findings);
    }

    // ── 第四防線：儲存與資料 (5%) ─────────────────────────────────────────

    private static SecurityCategoryScore EvalStorage(StorageFacts f)
    {
        var findings = new List<SecurityFinding>();
        int score = 100;

        score -= Check(findings, "stor.bitlocker", "BitLocker 系統磁碟加密",
            f.BitLockerEnabled,
            "BitLocker 已啟用",
            "BitLocker 未啟用——實體存取可讀取磁碟資料",
            SecuritySeverity.Warning, 30,
            "啟用 BitLocker。需 TPM。");

        score -= Check(findings, "stor.escrow", "BitLocker 復原金鑰託管",
            f.BitLockerEscrow,
            "復原金鑰已託管",
            "未偵測到復原金鑰託管",
            SecuritySeverity.Warning, 20,
            "將 BitLocker 復原金鑰備份至 Azure AD 或 Active Directory。");

        // Encryption method strength (special)
        if (f.EncryptionMethod is not null)
        {
            bool strong = f.EncryptionMethod.Contains("256", StringComparison.Ordinal)
                       || f.EncryptionMethod.Contains("Hardware", StringComparison.OrdinalIgnoreCase);
            if (strong)
                findings.Add(new SecurityFinding("stor.enc-method", "", "系統磁碟加密強度",
                    $"使用 {f.EncryptionMethod}", SecuritySeverity.Good, null));
            else
            {
                bool isNone = f.EncryptionMethod.Equals("None", StringComparison.OrdinalIgnoreCase);
                score -= isNone ? 20 : 10;
                findings.Add(new SecurityFinding("stor.enc-method", "", "系統磁碟加密強度不足",
                    $"使用 {f.EncryptionMethod}——建議升級至 XTS-AES-256",
                    isNone ? SecuritySeverity.Warning : SecuritySeverity.Advisory,
                    "透過群組原則設定 BitLocker 使用 XTS-AES-256。"));
            }
        }

        score -= Check(findings, "stor.nvme-fw", "NVMe 韌體基線",
            f.NvmeFirmwareBaseline,
            "磁碟韌體與基線一致",
            "磁碟韌體與基線不一致",
            SecuritySeverity.Warning, 15,
            "執行 snapshot 更新基線。");

        score -= Check(findings, "stor.smart", "SMART 健康狀態",
            f.SmartHealthy,
            "磁碟 SMART 狀態正常",
            "磁碟 SMART 回報異常",
            SecuritySeverity.Advisory, 15,
            "檢查磁碟健康。");

        return MakeCategory("storage", "儲存與資料", 5, score, findings);
    }

    // ── 第五防線：驅動與對抗 (20%) ────────────────────────────────────────

    private static SecurityCategoryScore EvalDrivers(DriverFacts f)
    {
        var findings = new List<SecurityFinding>();
        int score = 100;

        // Unsigned drivers
        if (f.UnsignedDrivers > 0)
        {
            int deduct = Math.Min(20, f.UnsignedDrivers * 7);
            score -= deduct;
            findings.Add(new SecurityFinding(
                "drv.unsigned", "驅動與對抗",
                $"{f.UnsignedDrivers} 個未簽章驅動",
                $"偵測到 {f.UnsignedDrivers} 個未經數位簽章的核心驅動（共 {f.TotalDrivers} 個）。",
                f.UnsignedDrivers >= 3 ? SecuritySeverity.Critical : SecuritySeverity.Warning,
                "使用「驅動稽核」頁查看並移除未簽章驅動。"));
        }
        else
        {
            findings.Add(new SecurityFinding(
                "drv.unsigned", "驅動與對抗", "所有驅動皆已簽章",
                $"全部 {f.TotalDrivers} 個驅動皆通過數位簽章驗證。",
                SecuritySeverity.Good, null));
        }

        // Known vulnerable (BYOVD)
        if (f.KnownVulnerableDrivers > 0)
        {
            score -= Math.Min(25, f.KnownVulnerableDrivers * 12);
            findings.Add(new SecurityFinding(
                "drv.byovd", "驅動與對抗",
                $"{f.KnownVulnerableDrivers} 個已知漏洞驅動",
                $"偵測到 {f.KnownVulnerableDrivers} 個符合 BYOVD 漏洞清單的驅動。",
                SecuritySeverity.Critical,
                "移除或更新這些驅動。"));
        }
        else
        {
            findings.Add(new SecurityFinding(
                "drv.byovd", "驅動與對抗", "未發現已知漏洞驅動",
                "目前載入的驅動不在 BYOVD 漏洞清單中。",
                SecuritySeverity.Good, null));
        }

        score -= Check(findings, "drv.blocklist", "漏洞驅動阻擋清單",
            f.VulnerableDriverBlocklistEnabled,
            "Windows 漏洞驅動阻擋清單已啟用",
            "漏洞驅動阻擋清單未啟用",
            SecuritySeverity.Warning, 15,
            "Windows 安全性 → 裝置安全性 → 核心隔離 → 弱點驅動程式封鎖清單。");

        score -= Check(findings, "drv.lsass", "LSASS 保護",
            f.LsassProtected,
            "LSASS 受 PPL 或 Credential Guard 保護",
            "LSASS 未受保護",
            SecuritySeverity.Warning, 15,
            "啟用 Credential Guard 或設定 LSASS 以 PPL 執行。");

        score -= Check(findings, "drv.wdac", "WDAC / Device Guard 政策",
            f.WdacActive,
            "WDAC 程式碼完整性政策已部署",
            "未部署 WDAC 政策，任意驅動可載入",
            SecuritySeverity.Warning, 10,
            "部署 WDAC 原則以限制可載入的核心模式程式碼。");

        score -= Check(findings, "drv.kdebug", "核心偵錠已停用",
            f.KernelDebugOff,
            "核心偵錠已停用",
            "核心偵錠已啟用，允許繞過安全檢查",
            SecuritySeverity.Critical, 10,
            "執行 bcdedit /debug off 並重新開機。");

        score -= Check(findings, "drv.coinstall", "驅動共同安裝程式已停用",
            f.CoInstallersDisabled,
            "已停用驅動共同安裝程式",
            "驅動共同安裝程式仍允許執行",
            SecuritySeverity.Advisory, 5,
            "設定 DisableCoInstallers=1。");

        score -= Check(findings, "drv.stalecerts", "過期驅動憑證",
            f.StaleDriverCertsClean,
            "未偵測到過期驅動憑證",
            "存在過期或已撤銷的驅動憑證",
            SecuritySeverity.Advisory, 5,
            "更新或移除使用過期憑證簽署的驅動程式。");

        return MakeCategory("drivers", "驅動與對抗", 20, score, findings);
    }

    // ── 第六防線：系統攻擊面 (30%) ──── NEW ─────────────────────────────

    private static SecurityCategoryScore EvalSurface(SurfaceFacts f)
    {
        var findings = new List<SecurityFinding>();
        int score = 100;

        score -= Check(findings, "sfc.defender", "Windows Defender 即時防護",
            f.DefenderRealtimeOn,
            "即時防護已啟用",
            "即時防護已停用，惡意程式可能無法即時放截",
            SecuritySeverity.Critical, 12,
            "在 Windows 安全性中心啟用即時防護。");

        score -= Check(findings, "sfc.fw-domain", "Windows 防火牆 (網域)",
            f.FirewallDomain,
            "網域設定檔防火牆已啟用",
            "網域設定檔防火牆已停用",
            SecuritySeverity.Critical, 6,
            "啟用網域設定檔的 Windows 防火牆。");

        score -= Check(findings, "sfc.fw-public", "Windows 防火牆 (公用)",
            f.FirewallPublic,
            "公用設定檔防火牆已啟用",
            "公用設定檔防火牆已停用",
            SecuritySeverity.Critical, 6,
            "啟用公用設定檔的 Windows 防火牆。");

        score -= Check(findings, "sfc.fw-private", "Windows 防火牆 (私人)",
            f.FirewallPrivate,
            "私人設定檔防火牆已啟用",
            "私人設定檔防火牆已停用",
            SecuritySeverity.Warning, 4,
            "啟用私人設定檔的 Windows 防火牆。");

        score -= Check(findings, "sfc.rdp", "遠端桌面已停用",
            f.RdpDisabled,
            "遠端桌面已停用",
            "遠端桌面已啟用，增加遠端攻擊面",
            SecuritySeverity.Warning, 6,
            "若無需要，停用遠端桌面。");

        score -= Check(findings, "sfc.autologon", "自動登入已停用",
            f.AutoLogonOff,
            "未設定自動登入",
            "已設定自動登入，實體存取即可取得完整權限",
            SecuritySeverity.Critical, 7,
            "移除 Winlogon 下的 AutoAdminLogon 設定。");

        score -= Check(findings, "sfc.guest", "Guest 帳戶已停用",
            f.GuestDisabled,
            "Guest 帳戶已停用",
            "Guest 帳戶已啟用，允許匿名存取",
            SecuritySeverity.Warning, 4,
            "停用 Guest 帳戶。");

        score -= Check(findings, "sfc.admin", "Administrator 帳戶安全",
            f.AdminSafe,
            "內建 Administrator 帳戶已停用或已設密碼",
            "內建 Administrator 帳戶已啟用且未設密碼",
            SecuritySeverity.Critical, 6,
            "停用內建 Administrator 帳戶或設定強密碼。");

        score -= Check(findings, "sfc.ps-policy", "PowerShell 執行原則",
            f.PsRestricted,
            "執行原則已設定為 Restricted 或 AllSigned",
            "執行原則過於寬鬆，惡意腳本可直接執行",
            SecuritySeverity.Warning, 5,
            "將 PowerShell 執行原則設定為 AllSigned 或 Restricted。");

        score -= Check(findings, "sfc.wsh", "Windows Script Host 已停用",
            f.WshDisabled,
            "WSH 已停用",
            "WSH 已啟用，.vbs/.js 惡意腳本可直接執行",
            SecuritySeverity.Warning, 4,
            "設定 Windows Script Host\\Settings\\Enabled = 0。");

        score -= Check(findings, "sfc.rdp-nla", "遠端桌面 NLA 驗證",
            f.RdpNlaRequired,
            "NLA (網路層級驗證) 已要求",
            "NLA 未啟用，遠端桌面暴露於未驗證存取",
            SecuritySeverity.Warning, 5,
            "在遠端桌面設定中啟用 NLA。");

        score -= Check(findings, "sfc.smbv1", "SMBv1 通訊協定已停用",
            f.SmbV1Disabled,
            "SMBv1 已停用或未安裝",
            "SMBv1 仍啟用，EternalBlue 類型攻擊有可能",
            SecuritySeverity.Critical, 8,
            "透過新增移除 Windows 功能或 PowerShell 停用 SMBv1。");

        score -= Check(findings, "sfc.winrm", "WinRM 服務已停止",
            f.WinRmStopped,
            "WinRM 服務未執行",
            "WinRM 服務執行中，允許遠端管理",
            SecuritySeverity.Warning, 4,
            "若無需要，停止並停用 WinRM 服務。");

        score -= Check(findings, "sfc.spooler", "Print Spooler 已停止",
            f.PrintSpoolerStopped,
            "Print Spooler 服務未執行",
            "Print Spooler 執行中 (PrintNightmare 攻擊面)",
            SecuritySeverity.Warning, 5,
            "若不需列印，停止並停用 Print Spooler 服務。");

        // Windows Update age (special: integer)
        if (f.DaysSinceUpdate is int days)
        {
            bool ok = days <= 30;
            if (ok)
                findings.Add(new SecurityFinding("sfc.wuage", "", "距上次 Windows Update",
                    $"近 {days} 天內已安裝更新", SecuritySeverity.Good, null));
            else
            {
                score -= 11;
                findings.Add(new SecurityFinding("sfc.wuage", "", "距上次 Windows Update",
                    $"超過 {days} 天未安裝更新，已知漏洞可能未修補",
                    SecuritySeverity.Critical,
                    "立即執行 Windows Update 並安裝所有安全性更新。"));
            }
        }
        else
        {
            score -= 5; // unknown = 50% of 11
            findings.Add(new SecurityFinding("sfc.wuage", "", "距上次 Windows Update",
                "無法讀取上次更新時間", SecuritySeverity.Advisory, null));
        }

        return MakeCategory("surface", "系統攻擊面", 30, score, findings);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────────────

    private static int Check(
        List<SecurityFinding> findings, string id, string title,
        bool? value, string goodText, string badText,
        SecuritySeverity badSev, int weight, string recommendation)
    {
        if (value is null)
        {
            findings.Add(new SecurityFinding(
                id, "", title, "讀不到（可能需要系統管理員權限或此功能不適用）。",
                SecuritySeverity.Advisory, null));
            return weight / 2;  // null = 50% deduction (stricter)
        }
        if (value == true)
        {
            findings.Add(new SecurityFinding(id, "", title, goodText, SecuritySeverity.Good, null));
            return 0;
        }
        findings.Add(new SecurityFinding(id, "", title, badText, badSev, recommendation));
        return badSev switch
        {
            SecuritySeverity.Critical => weight,
            SecuritySeverity.Warning => weight * 3 / 4,
            SecuritySeverity.Advisory => weight / 2,
            _ => 0,
        };
    }

    private static SecurityCategoryScore MakeCategory(
        string id, string name, int weight, int score, List<SecurityFinding> findings)
    {
        score = Math.Clamp(score, 0, 100);
        var sev = score switch
        {
            >= 90 => SecuritySeverity.Good,
            >= 70 => SecuritySeverity.Advisory,
            >= 50 => SecuritySeverity.Warning,
            _ => SecuritySeverity.Critical,
        };
        for (int i = 0; i < findings.Count; i++)
            if (string.IsNullOrEmpty(findings[i].Category))
                findings[i] = findings[i] with { Category = name };

        return new SecurityCategoryScore(id, name, score, weight, sev, findings);
    }
}
