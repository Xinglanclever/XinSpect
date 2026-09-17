namespace XinSpect;

/// <summary>
/// 強化建議產生器——純函式，從防線評分結果產出優先排序的可執行建議。
/// 只對非 Good 的發現產生建議；已達標的項目不囉叨。
/// </summary>
public static class HardeningAdvisor
{
    public static IReadOnlyList<HardeningRecommendation> Recommend(
        IReadOnlyList<SecurityCategoryScore> categories)
    {
        var recs = new List<HardeningRecommendation>();

        foreach (var cat in categories)
            foreach (var f in cat.Findings)
            {
                if (f.Severity == SecuritySeverity.Good) continue;
                if (f.Recommendation is null) continue;

                recs.Add(new HardeningRecommendation(
                    Id: f.Id,
                    Title: f.Title,
                    Priority: f.Severity,
                    Description: f.Recommendation,
                    Impact: ImpactText(f.Id),
                    Difficulty: DifficultyText(f.Id)
                ));
            }

        recs.Sort((a, b) => SevOrder(a.Priority).CompareTo(SevOrder(b.Priority)));
        return recs;
    }

    private static int SevOrder(SecuritySeverity s) => s switch
    {
        SecuritySeverity.Critical => 0,
        SecuritySeverity.Warning => 1,
        SecuritySeverity.Advisory => 2,
        _ => 3,
    };

    private static string ImpactText(string id) => id switch
    {
        // DMA
        "dma.hvci" => "阻止核心代碼注入，但可能影響極少數舊驅動。",
        "dma.vbs" => "為 Credential Guard 等進階保護奠定基礎。",
        "dma.iommu" => "阻止 FPGA DMA 板卡攻擊，無副作用。",
        "dma.dma-prot" => "限制外接裝置的 DMA 存取權限。",
        "dma.dma-enforce" => "強制 DMA 重對映，右接裝置登入前無法存取記憶體。",
        "dma.credguard" => "保護登入憑證免受記憶體提取攻擊。",
        "dma.thunderbolt" => "限制 Thunderbolt 裝置的 DMA 存取權限。",
        "dma.bitlocker-dma" => "確保全碟加密在 DMA 攻擊下仍有效。",

        // Firmware
        "fw.secboot" => "可阻止引導kit——重裝系統也無法清除的威脅。",
        "fw.testsign" => "關閉後惡意驅動無法載入，但需確認無相依的測試驅動。",
        "fw.tpm-present" => "無 TPM 時 BitLocker 只能用密碼模式，且無法用於遠端證明。",
        "fw.tpm-ver" => "TPM 1.2 不支援 SHA-256，並無法滿足 Windows 11 最低需求。",
        "fw.uefi" => "Legacy/CSM 開機不支援 Secure Boot，且 MBR 配置更易被篡改。",
        "fw.bios-pwd" => "無 BIOS 密碼時，實體接觸攻擊者可停用 Secure Boot。",
        "fw.spi" => "防止軟體層面的 BIOS 韌體篡改。",
        "fw.measured-boot" => "確認啟動鏈完整性可被遠端驗證。",

        // CPU
        "cpu.specmit" => "緩解推測性執行旁通道攻擊。",
        "cpu.spec-override" => "停用緩解時，惡意程序可透過旁通道讀取核心機密資料。",
        "cpu.nx" => "無 NX/DEP 時攻擊者可在資料區執行 shellcode。",
        "cpu.dep" => "DEP 防止資料區執行，降低利用難度。",
        "cpu.aslr" => "高熵 ASLR 讓記憶體位置更難預測。",
        "cpu.cfg" => "無 CFG 時 ROP/JOP 鐘攻擊更易成功。",
        "cpu.kernel-cet" => "無 CET 時核心模式 ROP 攻擊無法被硬體放截。",
        "cpu.smep" => "無 SMEP 時核心漏洞可跳轉到使用者模式執行惡意程式碼。",
        "cpu.smap" => "無 SMAP 時核心可意外讀取使用者模式資料。",
        "cpu.lsass-ppl" => "無 PPL 時 mimikatz 等工具可直接傾印 LSASS 憑證。",

        // Storage
        "stor.bitlocker" => "實體存取無法直接讀取磁碟資料。",
        "stor.escrow" => "無復原金鑰託管時，硬體變更可能導致永久資料遊失。",
        "stor.enc-method" => "較弱的加密可能在未來被暴力破解。",
        "stor.nvme-fw" => "確認磁碟韌體未被惡意替換。",

        // Driver
        "drv.unsigned" => "消除未經驗證的核心代碼。",
        "drv.byovd" => "移除可被利用的漏洞驅動。",
        "drv.blocklist" => "啟用 Windows 內建的漏洞驅動阻擋。",
        "drv.lsass" => "保護記憶體中的登入憑證。",
        "drv.wdac" => "無 WDAC 政策時任意簽署的驅動均可載入核心，BYOVD 攻擊無法防穡。",
        "drv.kdebug" => "核心偵錠啟用時可完全控制作業系統。",
        "drv.coinstall" => "驅動共同安裝程式可在裝置安裝時執行任意程式碼。",
        "drv.stalecerts" => "過期憑證簽署的驅動可能含有已知漏洞。",

        // Surface (attack surface)
        "sfc.defender" => "即時防護停用時惡意程式無法即時放截。",
        "sfc.fw-domain" or "sfc.fw-public" or "sfc.fw-private" =>
            "防火牆停用時橫向移動風險極高。",
        "sfc.rdp" => "開啟的遠端桌面是暴力破解與憑證填充攻擊的首要目標。",
        "sfc.autologon" => "自動登入代表實體接觸即可取得完整權限，密碼以明文存在登錄檔。",
        "sfc.guest" => "啟用的 Guest 帳戶允許未經驗證的使用者存取共享資源。",
        "sfc.admin" => "內建 Administrator 無密碼時任何可開啟登入畫面的人即可以最高權限登入。",
        "sfc.ps-policy" => "寬鬆的執行原則允許下載即執行的惡意腳本攻擊。",
        "sfc.wsh" => "啟用的 WSH 允許 .vbs/.js 惡意腳本直接執行，是釣魚郵件攻擊的常見入口。",
        "sfc.rdp-nla" => "無 NLA 時遠端桌面在驗證前即顯示登入畫面，暴露系統資訊。",
        "sfc.smbv1" => "SMBv1 存在多個已知重大漏洞 (EternalBlue/WannaCry)。",
        "sfc.winrm" => "執行中的 WinRM 允許遠端執行命令，是橫向移動的主要工具。",
        "sfc.spooler" => "Print Spooler 暴露於 PrintNightmare (CVE-2021-34527) 等遠端執行漏洞。",
        "sfc.wuage" => "長期未更新的系統暴露於所有已公開的安全性漏洞。",

        _ => "提升整體安全態勢。",
    };

    private static string DifficultyText(string id) => id switch
    {
        // 進階 (hardware / firmware / complex policy)
        "fw.secboot" or "fw.uefi" or "fw.spi" or "fw.me-mfg" => "進階",
        "dma.iommu" or "dma.thunderbolt" or "cpu.nx" => "進階",
        "fw.tpm-present" or "fw.tpm-ver" => "進階",
        "cpu.smep" or "cpu.smap" or "cpu.kernel-cet" => "進階",
        "drv.wdac" => "進階",

        // 中等 (group policy / reboot / recompile)
        "fw.testsign" or "cpu.dep" => "中等",
        "dma.hvci" or "dma.vbs" or "dma.credguard" or "dma.dma-enforce" => "中等",
        "cpu.cfg" or "cpu.kernel-cet" or "cpu.specmit" => "中等",
        "stor.bitlocker" or "stor.enc-method" => "中等",
        "drv.unsigned" or "drv.byovd" or "drv.blocklist" or "drv.stalecerts" => "中等",

        // 簡單 (registry / toggle / one-liner)
        _ => "簡單",
    };
}
