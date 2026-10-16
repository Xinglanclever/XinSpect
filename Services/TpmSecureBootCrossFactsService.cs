using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// TPM 與 Secure Boot 的狀態對照（ULTIMATE-1000 階段二 P2-073）。
///
/// <para>
/// <b>這一項是純交叉：不新增任何讀取、不新增任何編號。</b>
/// 兩邊的輸入都是既有的事實——
/// <c>tpm.present</c>（TPM 有沒有）與 <c>platform.secure_boot</c>／
/// <c>uefi.secure_boot</c>（Secure Boot 的兩個端點：
/// 登錄檔層與 UEFI 變數層）。
/// </para>
///
/// <para>
/// <b>而這裡最重要的一件事是「可疑」不是「錯誤」。</b>
/// TPM 與 Secure Boot 在架構上相關（Secure Boot 的 PK／KEK 可以存在 TPM 裡，
/// 而量測開機需要 TPM），但<b>兩者不是互相依賴的</b>：
/// 一台機器可以沒有 TPM 而 Secure Boot 開著
/// （憑證放在 UEFI 變數裡，那不需要 TPM）。
/// <b>所以本服務只說「這個組合值得注意」，不說「設定有問題」。</b>
/// </para>
///
/// <para>
/// <b>界線一：兩個端點讀不到時不猜。</b>
/// Secure Boot 有登錄檔層與 UEFI 變數層兩個來源，
/// 而它們可以一個有值、一個讀不到——<b>那時本服務說「無從對照」，
/// 不拿單邊的值去配 TPM 的存在性。</b>
/// </para>
///
/// <para>
/// <b>界線二：這不是判決。</b>
/// 它不宣告「這台機器的安全設定正確或不正確」——
/// 那只是一個組合的狀態陳述。
/// </para>
/// </summary>
public static class TpmSecureBootCrossFactsService
{
    private const string Category = "信賴根";
    private const string Source = "交叉對照（tpm.present × platform.secure_boot／uefi.secure_boot）";

    public const string CrossKey = "tpm.cross.sb";

    /// <summary>對照的輸入三態：<c>null</c> ＝ 那一邊讀不到。</summary>
    public sealed record CrossInput(bool? TpmPresent, bool? SecureBootEnabled, string? SecureBootEndpoint);

    /// <summary>從既有事實取出對照輸入。找不到或非 Present 的值一律回 <c>null</c>（不推測）。</summary>
    public static CrossInput FromFacts(IReadOnlyList<HardwareFact> facts)
    {
        // tpm.present 的值是「可用（Windows TBS 仲介，唯讀查詢）」。
        bool? tpm = Read(facts, "tpm.present", "可用");

        // Secure Boot 兩個端點：先看 UEFI 變數層（更接近韌體），再看登錄檔層。
        var (sb, endpoint) = ReadSecureBoot(facts);
        return new CrossInput(tpm, sb, endpoint);
    }

    private static bool? Read(IReadOnlyList<HardwareFact> facts, string key, string onText)
    {
        var f = facts.FirstOrDefault(x => x.Key == key);
        if (f is null || f.Availability != FactAvailability.Present) return null;
        return f.Value.Contains(onText, StringComparison.Ordinal);
    }

    private static (bool?, string?) ReadSecureBoot(IReadOnlyList<HardwareFact> facts)
    {
        var uefi = facts.FirstOrDefault(x => x.Key == "uefi.secure_boot");
        if (uefi is { Availability: FactAvailability.Present })
            return (uefi.Value.Contains("是", StringComparison.Ordinal), "uefi.secure_boot");

        var registry = facts.FirstOrDefault(x => x.Key == "platform.secure_boot");
        if (registry is { Availability: FactAvailability.Present })
            return (registry.Value.Contains("開啟", StringComparison.Ordinal), "platform.secure_boot");

        return (null, null);
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, CrossInput? input)
    {
        if (input is null)
            return
            [
                new HardwareFact(CrossKey, Category, "TPM 與 Secure Boot 對照", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                    "沒有輸入就沒有對照可言——本服務不自己去讀 TPM 或 Secure Boot"
                    + "（那兩條路徑有它們自己的可及性事實）"),
            ];

        if (input.SecureBootEndpoint is null)
            return
            [
                new HardwareFact(CrossKey, Category, "TPM 與 Secure Boot 對照", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "**Secure Boot 的兩個端點都讀不到，所以無從對照。**"
                    + "**「讀不到」與「關閉」是不同的斷言**——"
                    + "而本服務不拿單邊的值去配 TPM 的存在性"
                    + (input.TpmPresent is null
                        ? "；而 TPM 那一邊也讀不到" : "")),
            ];

        if (input.TpmPresent is null)
            return
            [
                new HardwareFact(CrossKey, Category, "TPM 與 Secure Boot 對照", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    $"**TPM 那一邊讀不到，所以無從對照**（Secure Boot 由 "
                    + $"{input.SecureBootEndpoint} 得知為 "
                    + $"{(input.SecureBootEnabled == true ? "開啟" : "關閉")}）。"
                    + "**「沒有 TPM」與「讀不到 TPM」是不同的斷言**"),
            ];

        bool tpm = input.TpmPresent.Value;
        bool sb = input.SecureBootEnabled == true;
        string combination = (tpm, sb) switch
        {
            (true, true) => "兩者都到位",
            (true, false) => "有 TPM 而 Secure Boot 關閉",
            (false, true) => "**沒有 TPM 而 Secure Boot 開啟**",
            _ => "兩者都沒有",
        };

        string note = (tpm, sb) switch
        {
            (true, false) =>
                "**那不是矛盾。**TPM 與 Secure Boot 不是互相依賴的——"
                + "有 TPM 只代表有量測與金鑰儲存的硬體，"
                + "而 Secure Boot 開不開是另一個決定",
            (false, true) =>
                "**這個組合值得注意，但不是錯誤。**Secure Boot 的憑證（PK／KEK／db）"
                + "存在 UEFI 變數裡，那不需要 TPM——**所以一台沒有 TPM 的機器"
                + "可以正常開啟 Secure Boot。**"
                + "值得注意的是那個組合在**量測面**的後果："
                + "沒有 TPM 就沒有 PCR，所以 Secure Boot 的驗證結果不會被量測進信任鏈",
            (true, true) =>
                "**而這不代表量測開機已經在運作**："
                + "那要看 PCR 的內容（見 bt.measured_boot），不是看 TPM 存不存在",
            _ => "**這在 CSM／Legacy 開機的機器上是正常的樣貌**",
        };

        return
        [
            new HardwareFact(CrossKey, Category, "TPM 與 Secure Boot 對照",
                combination + "。" + note
                // **而值一定說得出來源**：Secure Boot 那一邊有兩個端點
                // （uefi.secure_boot 較接近韌體、platform.secure_boot 是登錄檔層），
                // 而它們可以一個有值、一個讀不到。
                // **不知道用了哪一個就無法回推那一段的可靠度。**
                + $"（Secure Boot 的值來自 {input.SecureBootEndpoint}）",
                "", Source,
                FactTrustLevel.Derived, false, at, tpm && sb ? 1 : 0, FactAvailability.Present,
                // **Present 的事實不得帶 unavailableReason**（HardwareSnapshotService 的不變式），
                // 而 HardwareFact 的第二條不變式會把這裡寫的東西併進 Value。
                // 所以 note 的開頭**不能重複 combination 已經說過的話**——
                // 兩段話本來是給兩個欄位的，接起來就必須像一段話。
                null),
        ];
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
        => [new HardwareFact(CrossKey, Category, "TPM 與 Secure Boot 對照", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)];
}
