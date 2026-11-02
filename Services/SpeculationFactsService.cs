using System.Globalization;

namespace XinSpect;

/// <summary>
/// 推測層（<c>infer.*</c>）：只從<b>已經收集到的事實</b>做間接推導，
/// 不新增任何硬體讀取、不驗簽、不連網。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要有一層「推測」而不是直接寫進對應的事實：</b>
/// 有些問題的正確答案不是「有／沒有」，而是「以現有證據只能猜到這裡」。
/// 例如 FIT 目錄裡有 ACM 與啟動模組條目，<b>不代表 Boot Guard 已啟用</b>；
/// TPM 可用，<b>不代表遠端 Quote 會被接受</b>。把這些猜測塞進
/// <c>bt.*</c>／<c>tpm.*</c> 會讓它們看起來像量測結果，而<b>那個誤讀正是本專案最不該製造的東西</b>。
/// </para>
/// <para>
/// <b>三重標註是硬性契約，不是文案建議：</b>
/// ①鍵名一律以 <c>infer.</c> 開頭；②值一律以 <c>【推測】</c> 開頭；
/// ③<see cref="HardwareFact.Trust"/> 一律是 <see cref="FactTrustLevel.Speculative"/>。
/// 三者同時存在，任何只讀鍵、只讀值或只讀信任等級的消費者都看得出「這不是量測」。
/// </para>
/// <para>
/// <b>這一層不參與合規判定，也不進 Merkle 指紋。</b>
/// <see cref="ComplianceMappingService"/> 只讀它點名的事實鍵；
/// <see cref="FactMerkleService"/> 用白名單前綴，<c>infer.</c> 不在名單內。
/// 兩者都有測試釘住，避免哪一天有人「順手」把推測當證據。
/// </para>
/// </remarks>
public static class SpeculationFactsService
{
    public const int SchemaVersion = 1;

    /// <summary>值的前綴；三個標註契約之一。</summary>
    public const string Label = "【推測】";

    private const string Category = "推測（非量測）";
    private const string Source = "由既有事實間接推導（未量測、未驗簽、未連網）";

    // 鍵名由目錄掃描器收錄；七把都在 Services/FactKeyCatalog.cs 的 infer.* 區段。
    public const string DisclaimerKey = "infer.disclaimer";
    public const string BootGuardKey = "infer.bt.boot_guard";
    public const string CxlRoleKey = "infer.cxl.role";
    public const string FirmwareRecoveryKey = "infer.fw.recovery";
    public const string UpdateAuthKey = "infer.fw.update_auth";
    public const string TpmQuoteKey = "infer.tpm.quote";
    public const string UefiPqcKey = "infer.uefi.pqc";

    // 依據鍵（全部是既有鍵；不以 *Key 命名，避免被目錄掃描器當成新事實鍵）。
    private const string FitSummaryBasis = "fw.fit.summary";
    private const string BootGuardProfileBasis = "fw.bg.profile";
    private const string IbbBasis = "bt.ibb";
    private const string KmBasis = "bt.km";
    private const string ManifestsBasis = "bt.manifests";
    private const string RecoveryBasis = "bt.recovery";
    private const string CapsuleSummaryBasis = "fw.capsule.summary";
    private const string CapsuleHistoryBasis = "fw.capsule.history";
    private const string SecureBootUefiBasis = "uefi.secure_boot";
    private const string SecureBootRegistryBasis = "platform.secure_boot";
    private const string FirmwareDateBasis = "fw.date";
    private const string FirmwareVendorBasis = "fw.vendor";
    private const string TpmPresentBasis = "tpm.present";
    private const string TpmVersionBasis = "tpm.version";
    private const string TpmAttestBasis = "tpm.attest.capable";
    private const string TpmBanksBasis = "tpm.pcr.banks";
    private const string TpmOwnedBasis = "tpm.owned";
    private const string CxlCountBasis = "cxl.cfmws.count";
    private const string CxlWindowBasis = "cxl.cfmws.0";
    private const string CedtBasis = "ac.cedt";
    private const string UefiDbBasis = "uefi.db";
    private const string UefiDbtBasis = "uefi.dbt";

    /// <summary>一筆推測：事實本體＋信心＋依據鍵＋限制。</summary>
    /// <remarks>
    /// <see cref="Fact"/> 的值裡已經重複帶了信心與限制——那不是冗餘，是為了讓
    /// 「只拿到 facts 陣列」的消費者（例如既有 JSON 匯出）也能看到同樣的話。
    /// </remarks>
    public sealed record SpeculationItem(
        HardwareFact Fact,
        string Confidence,
        IReadOnlyList<string> BasisKeys,
        string Basis,
        string Limitations);

    /// <summary>產生七筆推測；純函式，不做 I/O、不新增硬體讀取。</summary>
    public static IReadOnlyList<SpeculationItem> Evaluate(
        IReadOnlyList<HardwareFact> facts, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var index = BuildIndex(facts);

        return
        [
            Disclaimer(at),
            BootGuard(index, at),
            CxlRole(index, at),
            FirmwareRecovery(index, at),
            UpdateAuthentication(index, at),
            TpmQuote(index, at),
            UefiPostQuantum(index, at),
        ];
    }

    /// <summary>只要事實的投影；CLI 用這個把推測混進一般事實輸出時仍然帶三重標註。</summary>
    public static IReadOnlyList<HardwareFact> Collect(
        IReadOnlyList<HardwareFact> facts, DateTimeOffset at)
        => Evaluate(facts, at).Select(x => x.Fact).ToList();

    // ── 七條規則 ─────────────────────────────────────────────────────────

    private static SpeculationItem Disclaimer(DateTimeOffset at)
    {
        const string value =
            "本層所有項目皆為間接推導，非量測、非簽章驗證，亦非合規證據。"
            + "系統以鍵名前綴 infer.、值前綴【推測】及信任等級 Speculative 三重標記明確界定；"
            + "三者缺一即屬實作錯誤。為防推測被升格為證據，"
            + "本層資料一律不納入合規判定與 Merkle 指紋計算。";
        return Item(DisclaimerKey, "推測層範圍聲明", value,
            "不適用（這是範圍聲明，不是對機器的判斷）",
            [], "SpeculationFactsService 的輸出契約", "無；本項描述的是這一層本身。", at);
    }

    private static SpeculationItem BootGuard(FactIndex index, DateTimeOffset at)
    {
        var profile = index.Get(BootGuardProfileBasis);
        var ibb = index.Get(IbbBasis);
        var manifests = index.Get(ManifestsBasis);
        var km = index.Get(KmBasis);
        var fit = index.Get(FitSummaryBasis);

        bool ibbPresent = IsPresent(ibb);
        bool manifestPresent = IsPresent(manifests);
        bool hasStartup = ibbPresent && ibb!.Value.Contains("啟動模組", StringComparison.Ordinal);
        bool hasAcm = manifestPresent && manifests!.Value.Contains("ACM", StringComparison.Ordinal);
        // 只有「尾端掃描完成、確實沒有 FIT 簽名」才能推成「沒有 FIT 目錄」。
        // NotApplicable 也可能是「FREG 地圖無法推導」——那是「定位不了」，不是「不存在」。
        bool noFit = fit?.Availability == FactAvailability.NotApplicable
            && (fit.UnavailableReason?.Contains("沒有 FIT 簽名", StringComparison.Ordinal) ?? false);

        string value;
        string confidence;
        string limitations;
        if (hasStartup && hasAcm)
        {
            value = "FIT 目錄同時出現啟動模組（IBB 起點）與 ACM 條目，"
                + "推測韌體映像包含 Boot Guard 元件；但這只是靜態結構，"
                + "不代表硬體熔絲（FPF）已燒錄或驗證已生效。";
            confidence = "低";
            limitations = "FIT 目錄是靜態映像內容，不是硬體狀態：profile（FLMAP／FMLF）未讀取、"
                + "ACM 簽章未驗、開機策略未重放，也沒有讀 MSR BOOT_GUARD_SACM_INFO 或 ME 狀態，"
                + "所以不能說「Boot Guard 已啟用」或「驗證通過」。";
        }
        else if (hasStartup && !hasAcm)
        {
            value = "FIT 目錄有啟動模組條目，但沒有可判讀的 ACM 條目，"
                + "推測此平台「可能沒有」完整的 Boot Guard 信任鏈。";
            confidence = "低";
            limitations = "ACM 條目可能被裁剪、位於未讀到的區段，或型別不在既有目錄表內；"
                + "「目錄沒看到」不是「硬體沒有」。";
        }
        else if (noFit)
        {
            value = "沒有可用的 FIT 目錄，推測此平台「可能沒有 Boot Guard」，"
                + "或走的是非 FIT 的開機路徑。";
            confidence = "低";
            limitations = "老平台與非 Intel 平台本來就沒有 FIT；「目錄不存在」不能反面證明"
                + "「沒有 Boot Guard」，也不能證明「有」。";
        }
        else if (ibbPresent && !hasStartup)
        {
            value = "FIT 目錄存在但沒有啟動模組條目，推測 Boot Guard 目錄未描述 IBB。";
            confidence = "低";
            limitations = "這只描述目錄內容；「目錄沒有描述」與「機器沒有 IBB」是不同斷言。";
        }
        else
        {
            value = "缺少 FIT／IBB／信任鏈目錄事實，無法推測 Boot Guard 狀態。";
            confidence = "無法評估";
            limitations = "「沒有觀察到」不是「沒有啟用」；本項不從廠牌或年份猜測。";
        }

        return Item(BootGuardKey, "Boot Guard 啟用狀態", value, confidence,
            [FitSummaryBasis, BootGuardProfileBasis, IbbBasis, KmBasis, ManifestsBasis],
            BasisText(profile, ibb, manifests, km, fit),
            limitations + " KM 在任何情況下都未判定（型別編號無出處）。", at);
    }

    private static SpeculationItem CxlRole(FactIndex index, DateTimeOffset at)
    {
        var count = index.Get(CxlCountBasis);
        var window = index.Get(CxlWindowBasis);
        var cedt = index.Get(CedtBasis);

        string value;
        string confidence;
        string limitations;
        if (IsPresent(count) && count!.NumericValue is > 0)
        {
            value = $"CEDT 宣告 {count.NumericValue:0} 個 CXL 固定記憶體窗口，"
                + "代表韌體已保留主機端實體位址空間；"
                + "但這不代表插槽上實際有已訓練成功的 CXL 端點。";
            confidence = "低";
            limitations = "CFMWS 只描述主機端窗口，不描述端點型別或連接狀態；"
                + "未掃描 PCIe DVSEC 與連結訓練狀態，不能說「已接有 Type 3 裝置」。";
        }
        else if (IsPresent(count) && count!.NumericValue == 0)
        {
            value = "CEDT 存在但 CFMWS 窗口數為 0，推測 CXL 能力可能僅限 Type 1／Type 2，"
                + "或記憶體擴充尚未由韌體啟用。";
            confidence = "低";
            limitations = "窗口數 0 是韌體當下的宣告；CXL 端點是否存在、是否被隱藏，本工具都沒有讀。";
        }
        else if (index.IsNotApplicable(count) || index.IsNotApplicable(cedt))
        {
            value = "ACPI 表列沒有 CEDT，推測此平台沒有 CXL 主機能力。";
            confidence = "低";
            limitations = "韌體可以省略 CEDT；「表列沒有」不能排除 CXL 硬體存在，"
                + "也不能證明它不存在。";
        }
        else
        {
            value = "缺少 CEDT／CFMWS 事實，無法推測 CXL 角色。";
            confidence = "無法評估";
            limitations = "不以 PCIe 裝置名稱或 CPU 世代代替 CEDT 推測。";
        }

        return Item(CxlRoleKey, "CXL 角色（主機或端點）", value, confidence,
            [CedtBasis, CxlCountBasis, CxlWindowBasis],
            BasisText(count, window, cedt), limitations, at);
    }

    private static SpeculationItem FirmwareRecovery(FactIndex index, DateTimeOffset at)
    {
        var recovery = index.Get(RecoveryBasis);
        var capsule = index.Get(CapsuleSummaryBasis);
        var history = index.Get(CapsuleHistoryBasis);

        string value;
        string confidence;
        string limitations;
        if (IsPresent(recovery) && recovery!.NumericValue is > 0)
        {
            value = $"平台宣告 OsIndicationsSupported＝0x{recovery.NumericValue:0}（非 0），"
                + "推測此韌體至少具備一種由 OS 觸發的韌體更新／復原路徑；"
                + "若含膠囊指示，則可能有 capsule 更新能力。";
            confidence = "中";
            limitations = "位元語意未逐位命名；原始碼明寫「未辨識復原區」，"
                + "所以不能確認有可開機的復原映像，也不能確認更新失敗會自動回滾。";
        }
        else if (IsPresent(recovery) && recovery!.NumericValue == 0)
        {
            value = "平台明確宣告 OsIndicationsSupported＝0：沒有宣告任何 OS 觸發的韌體指示，"
                + "推測沒有標準膠囊復原路徑。";
            confidence = "中";
            limitations = "0 是「沒有宣告」，不是「沒有復原能力」：OEM 私有復原／雙 BIOS 仍可能存在。";
        }
        else if (IsPresent(capsule) || IsPresent(history))
        {
            value = "有膠囊相關事實，但缺少 OsIndicationsSupported，"
                + "推測更新／復原可能由 OEM 工具而非標準 UEFI 指示驅動。";
            confidence = "低";
            limitations = "膠囊檔存在或曾被套用，不代表失敗時能復原；回滾策略未讀取。";
        }
        else
        {
            value = "缺少 OsIndicationsSupported 與膠囊事實，無法推測韌體復原路徑。";
            confidence = "無法評估";
            limitations = "「讀不到」與「值為 0」不同；本項不以 0 代填。";
        }

        return Item(FirmwareRecoveryKey, "韌體更新／復原路徑", value, confidence,
            [RecoveryBasis, CapsuleSummaryBasis, CapsuleHistoryBasis],
            BasisText(recovery, capsule, history), limitations, at);
    }

    private static SpeculationItem UpdateAuthentication(FactIndex index, DateTimeOffset at)
    {
        var capsule = index.Get(CapsuleSummaryBasis);
        var history = index.Get(CapsuleHistoryBasis);
        var sbUefi = index.Get(SecureBootUefiBasis);
        var sbRegistry = index.Get(SecureBootRegistryBasis);

        bool? sbU = Affirmative(sbUefi);
        bool? sbR = Affirmative(sbRegistry);
        bool? sb = sbU ?? sbR;
        bool sbConflict = sbU is not null && sbR is not null && sbU != sbR;
        bool hasCapsule = IsPresent(capsule) || IsPresent(history);

        string value;
        string confidence;
        string limitations;
        if (sbConflict)
        {
            value = "UEFI 層與登錄檔層的 Secure Boot 回報不一致，"
                + "推測更新驗證狀態無法由這兩個來源決定。";
            confidence = "無法評估";
            limitations = "兩個來源各說各話時不挑一個相信；衝突本身才是這一項的結果。";
        }
        else if (sb == true)
        {
            value = "Secure Boot 回報開啟，且"
                + (hasCapsule ? "有膠囊相關事實" : "平台為 UEFI 開機")
                + "；推測韌體更新「可能」走 UEFI Secure Boot 金鑰鏈或 OEM 簽章驗證。";
            confidence = "低";
            limitations = "本工具沒有驗證任何膠囊簽章、沒有讀取 ESRT／更新策略、沒有檢查 OEM 憑證；"
                + "「Secure Boot 開」只證明開機鏈有驗證，不證明更新通道也驗簽。";
        }
        else if (sb == false)
        {
            value = "Secure Boot 回報關閉；推測標準 UEFI 更新驗證可能未啟用，"
                + "但 OEM 私有簽章機制仍可能存在。";
            confidence = "低";
            limitations = "「Secure Boot 關」不等於「更新不驗簽」；兩者是不同的信任決策點。";
        }
        else
        {
            value = "缺少可判讀的 Secure Boot 與膠囊事實，無法推測韌體更新是否驗簽。";
            confidence = "無法評估";
            limitations = "沒有觀察到驗證機制，不是「沒有驗證」；本項不從廠牌或韌體年份猜測。";
        }

        return Item(UpdateAuthKey, "韌體更新驗證方式", value, confidence,
            [CapsuleSummaryBasis, CapsuleHistoryBasis, SecureBootUefiBasis, SecureBootRegistryBasis],
            BasisText(capsule, history, sbUefi, sbRegistry), limitations, at);
    }

    private static SpeculationItem TpmQuote(FactIndex index, DateTimeOffset at)
    {
        var present = index.Get(TpmPresentBasis);
        var version = index.Get(TpmVersionBasis);
        var attest = index.Get(TpmAttestBasis);
        var banks = index.Get(TpmBanksBasis);
        var owned = index.Get(TpmOwnedBasis);

        bool tpmPresent = IsPresent(present);
        bool attestPresent = IsPresent(attest);
        bool version2 = version is not null && version.Value.Contains("2.0", StringComparison.Ordinal);

        string value;
        string confidence;
        string limitations;
        if (tpmPresent && version2 && attestPresent)
        {
            value = "TPM 2.0 可用，且背書階層處於啟用狀態；"
                + "但這只代表階層未被韌體鎖定，EK／AK 金鑰控制代碼未必已建立，"
                + "實際 Quote 仍可能失敗。";
            confidence = "低";
            limitations = "本工具沒有送出 challenge、沒有確認或建立 EK／AK、"
                + "沒有驗 EK 憑證鏈、沒有驗 PCR 簽章；"
                + "「階層可用」不等於「Quote 會成功」，也不等於「遠端驗證者會接受」。";
        }
        else if (tpmPresent)
        {
            value = "TPM 可用，但版本或背書階層前提不完整，"
                + "推測「可能」能做 Quote，證據不足。";
            confidence = "低";
            limitations = "不以「有 TPM」代填「可遠端證明」；EK／AK 是否存在另屬一項。";
        }
        else
        {
            value = "沒有可用的 TPM 事實，推測此機器無法做硬體式遠端證明；"
                + "軟體度量（例如 VBS／事件日誌）另計。";
            confidence = "低";
            limitations = "「TPM 讀不到」與「沒有 TPM」不同；本項不把兩者壓成同一個答案。";
        }

        return Item(TpmQuoteKey, "遠端 TPM Quote 可行性", value, confidence,
            [TpmPresentBasis, TpmVersionBasis, TpmAttestBasis, TpmBanksBasis, TpmOwnedBasis],
            BasisText(present, version, attest, banks, owned), limitations, at);
    }

    private static SpeculationItem UefiPostQuantum(FactIndex index, DateTimeOffset at)
    {
        var date = index.Get(FirmwareDateBasis);
        var vendor = index.Get(FirmwareVendorBasis);
        var db = index.Get(UefiDbBasis);
        var dbt = index.Get(UefiDbtBasis);

        int? year = FirmwareYear(date);
        string value;
        string confidence;
        string limitations;
        if (year is >= 2025)
        {
            value = $"韌體發行日為 {year} 年；推測此世代韌體「可能」開始包含後量子簽章驗證元件"
                + "（ML-DSA／ML-KEM 或混合憑證），但沒有直接證據。";
            confidence = "極低";
            limitations = "年份不是能力證明；本工具沒有讀取 PQC OID、沒有檢查 db／dbt 中的"
                + "後量子憑證、也沒有驗證韌體是否實際啟用。";
        }
        else if (year is >= 2023)
        {
            value = $"韌體發行日為 {year} 年；推測正處於 PQC 過渡期，"
                + "「可能」有部分廠商實作，但大多數平台仍以 RSA／ECDSA 為主。";
            confidence = "極低";
            limitations = "過渡期的年份推測幾乎沒有判別力；沒有 PQC 憑證或 OID 證據。";
        }
        else if (year is not null)
        {
            value = $"韌體發行日為 {year} 年；推測此韌體「不太可能」包含標準化的 PQC 驗證。";
            confidence = "極低";
            limitations = "舊年份不能排除 OEM 自訂延伸；「不太可能」不是「沒有」。";
        }
        else
        {
            value = "缺少韌體發行日，無法推測 UEFI 後量子驗證能力。";
            confidence = "無法評估";
            limitations = "不以 CPU 世代或作業系統版本代替韌體事實。";
        }

        return Item(UefiPqcKey, "UEFI 後量子驗證能力", value, confidence,
            [FirmwareDateBasis, FirmwareVendorBasis, UefiDbBasis, UefiDbtBasis],
            BasisText(date, vendor, db, dbt), limitations, at);
    }

    // ── 組裝與判讀 ───────────────────────────────────────────────────────

    private static SpeculationItem Item(string key, string name, string claim, string confidence,
        IReadOnlyList<string> basisKeys, string basis, string limitations, DateTimeOffset at)
    {
        string value = Label + claim
            + $"（信心：{confidence}；依據：{basis}；限制：{limitations}）";
        var fact = new HardwareFact(key, Category, name, value, "", Source,
            FactTrustLevel.Speculative, false, at);
        return new SpeculationItem(fact, confidence, basisKeys, basis, limitations);
    }

    private static string BasisText(params HardwareFact?[] facts)
    {
        var parts = new List<string>(facts.Length);
        foreach (var f in facts)
        {
            if (f is null) continue;
            string state = f.Availability == FactAvailability.Present
                ? "可用"
                : f.Availability.ToString();
            parts.Add($"{f.Key}＝{state}");
        }
        return parts.Count == 0 ? "沒有可用依據" : string.Join("、", parts);
    }

    /// <summary>
    /// 只做「值像不像肯定」的窄判讀；回 <c>null</c> 代表無法判讀。
    /// 刻意不把「未知」讀成「否」——那會讓推測多一層沒有依據的確定性。
    /// </summary>
    private static bool? Affirmative(HardwareFact? fact)
    {
        if (fact is null || fact.Availability != FactAvailability.Present) return null;
        // 有數字就用數字：這是比文字比對更窄、也更不容易被「未開啟」騙過的判讀。
        if (fact.NumericValue is { } n) return n != 0;

        string v = fact.Value.Trim();
        // 先判否定，再判肯定——否則「未開啟」會先命中「開啟」而回 true。
        if (v.Contains("未開啟", StringComparison.Ordinal)
            || v.Contains("未啟用", StringComparison.Ordinal)
            || v.Contains("關閉", StringComparison.Ordinal)
            || v.Contains("False", StringComparison.OrdinalIgnoreCase)
            || v.Contains("＝0", StringComparison.Ordinal)
            || v.Contains("=0", StringComparison.Ordinal)
            || v.StartsWith("否", StringComparison.Ordinal))
            return false;
        if (v.Contains("開啟", StringComparison.Ordinal)
            || v.Contains("啟用", StringComparison.Ordinal)
            || v.Contains("True", StringComparison.OrdinalIgnoreCase)
            || v.Contains("＝1", StringComparison.Ordinal)
            || v.Contains("=1", StringComparison.Ordinal)
            || v.StartsWith("是", StringComparison.Ordinal))
            return true;
        return null;
    }

    private static int? FirmwareYear(HardwareFact? date)
    {
        if (date is null || date.Availability != FactAvailability.Present) return null;
        string v = date.Value.Trim();
        if (v.Length >= 4 && v[..4].All(char.IsAsciiDigit)
            && int.TryParse(v[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int y)
            && y is >= 1990 and <= 2200)
            return y;
        return null;
    }

    private static bool IsPresent(HardwareFact? fact)
        => fact is not null && fact.Availability == FactAvailability.Present;

    /// <summary>同鍵多筆時取第一筆；事實收集器不該給重複鍵，但推測層不因此崩潰。</summary>
    private sealed class FactIndex
    {
        private readonly Dictionary<string, HardwareFact> _byKey;

        public FactIndex(IReadOnlyList<HardwareFact> facts)
        {
            _byKey = new Dictionary<string, HardwareFact>(StringComparer.Ordinal);
            foreach (var f in facts)
                _byKey.TryAdd(f.Key, f);
        }

        public HardwareFact? Get(string key) => _byKey.GetValueOrDefault(key);

        public bool IsNotApplicable(HardwareFact? fact)
            => fact?.Availability == FactAvailability.NotApplicable;
    }

    private static FactIndex BuildIndex(IReadOnlyList<HardwareFact> facts) => new(facts);
}