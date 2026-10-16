using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 量測開機支援旗標（ULTIMATE-1000 階段二 P2-056）。
///
/// <para>
/// <b>這一項要回答的不是「有沒有 TPM」，而是「韌體有沒有真的做量測」。</b>
/// 兩者是不同的斷言，而它們最常見的混淆方式是把「有 TPM」當成「有量測開機」。
/// 一台機器可以有 TPM 而<b>關鍵 PCR 全為零</b>——那代表韌體沒有把任何東西
/// extend 進去，量測鏈根本沒開始。
/// </para>
///
/// <para>
/// <b>判準是 PCR 的內容，不是 TPM 的存在：</b>
/// PCR 0 是 CRTM／韌體程式碼的標準位置，一台有完整量測開機的機器
/// PCR 0 一定非零。而 PCR 全零是一個明確的「沒有量測」訊號。
/// </para>
///
/// <para>
/// <b>界線：本服務只讀少數關鍵 PCR，不重讀 24 個。</b>
/// 它<b>不自己去問 TPM</b>——那條路徑有它自己的可及性事實
/// （<c>tpm.present</c>／<c>tpm.pcr.*</c>），本服務接收已經讀到的值。
/// 這與 <see cref="SmbiosCrossFactsService"/> 接收 <c>OsView</c> 是同一個模式。
/// </para>
/// </summary>
public static class MeasuredBootFactsService
{
    private const string Category = "信賴根";
    private const string Source = "關鍵 PCR 的延伸狀態（PCR 0／2／4／7）";

    public const string MeasuredKey = "bt.measured_boot";
    public const string EvidenceKey = "bt.measured_boot.evidence";

    /// <summary>
    /// 關鍵 PCR 的讀值。值為小寫十六進位字串（與 <c>tpm.pcr.*</c> 同一格式），
    /// <c>null</c> 代表那一格讀不到。
    /// </summary>
    public sealed record PcrReadings(
        string? Pcr0,   // CRTM／韌體程式碼
        string? Pcr2,   // 選項 ROM 程式碼
        string? Pcr4,   // 開機載入器與驅動
        string? Pcr7);  // Secure Boot 政策

    /// <summary>一個全零的 PCR 值：<paramref name="hexChars"/> 個 '0'。</summary>
    private static bool IsZero(string? value, int hexChars)
    {
        if (value is null) return false;          // 讀不到不是零
        string v = value.Trim();
        return v.Length == hexChars && v.All(c => c == '0');
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, PcrReadings? readings)
    {
        if (readings is null)
            return Na(at, "沒有 PCR 讀值——本服務不自己去問 TPM"
                + "（那條路徑有它自己的可及性事實：tpm.present／tpm.pcr.*）",
                FactAvailability.NotSupported);

        var known = new (string Name, string? Value)[]
        {
            ("PCR 0（CRTM／韌體程式碼）", readings.Pcr0),
            ("PCR 2（選項 ROM 程式碼）", readings.Pcr2),
            ("PCR 4（開機載入器與驅動）", readings.Pcr4),
            ("PCR 7（Secure Boot 政策）", readings.Pcr7),
        };

        var readable = known.Where(k => k.Value is not null).ToList();

        if (readable.Count == 0)
            return Na(at, "四個關鍵 PCR 都讀不到——**「讀不到」與「全為零」是不同的斷言**："
                + "前者代表這條路現在不通（TPM 不存在、未啟用，或 TBS 拒絕），"
                + "後者代表量測鏈沒有開始", FactAvailability.InsufficientPrivilege);

        // SHA-256 是 32 bytes = 64 個十六進位字元；SHA-1 是 20 bytes = 40。
        var zero = readable.Where(k => IsZero(k.Value, 64) || IsZero(k.Value, 40)).ToList();
        var extended = readable.Where(k => !zero.Contains(k)).ToList();

        string detail = string.Join("；", readable.Select(k =>
            zero.Contains(k) ? $"{k.Name} 全零" : $"{k.Name} 已延伸"));

        return
        [
            new HardwareFact(MeasuredKey, Category, "量測開機", Verdict(extended, zero, readable.Count),
                "", Source, FactTrustLevel.Measured, false, at,
                extended.Count > 0 ? 1 : 0,
                extended.Count > 0 ? FactAvailability.Present
                    : FactAvailability.NotApplicable,
                VerdictReason(extended, zero, readable.Count)),

            new HardwareFact(EvidenceKey, Category, "量測證據（關鍵 PCR）", detail, "", Source,
                FactTrustLevel.Measured, false, at, extended.Count,
                FactAvailability.Present,
                "**判準是 PCR 的內容，不是 TPM 的存在。**"
                + "一台機器可以有 TPM 而關鍵 PCR 全零——那代表韌體沒有把任何東西 "
                + "extend 進去，量測鏈根本沒開始。"
                + "本服務只讀這四個關鍵 PCR，不重讀全部 24 個（那是 tpm.pcr.* 的事）"),
        ];
    }

    private static string Verdict(List<(string Name, string? Value)> extended,
        List<(string Name, string? Value)> zero, int readableCount)
    {
        if (extended.Count == 0)
            return $"沒有量測證據（{readableCount} 個關鍵 PCR 都可讀，但全為零）";

        string names = string.Join("、", extended.Select(e => e.Name.Split('（')[0]));
        return $"有量測證據（{names} 已延伸"
            + (zero.Count > 0 ? $"；{zero.Count} 個仍為零" : "") + "）";
    }

    private static string VerdictReason(List<(string Name, string? Value)> extended,
        List<(string Name, string? Value)> zero, int readableCount)
    {
        if (extended.Count == 0)
            return "**這一項回答的不是「有沒有 TPM」，而是「韌體有沒有真的做量測」。**"
                + "關鍵 PCR 全零代表韌體沒有把任何東西 extend 進去——"
                + "**量測鏈根本沒開始**，而那與「這台機器有沒有 TPM」是兩件不同的事。"
                + "有些平台在韌體設定裡可以關閉量測，而關閉後 TPM 仍然存在";

        string zeroNote = zero.Count > 0
            ? $"另有 {zero.Count} 個關鍵 PCR 仍為零——"
              + "**那不代表那些層級沒有執行**：有些項目的量測取決於平台政策"
              + "（例如 PCR 7 只在 Secure Boot 啟用時才延伸）"
            : "四個關鍵 PCR 都已延伸";

        return $"**有 TPM 不等於有量測開機**，而這裡看到的是後者。" + zeroNote;
    }

    /// <summary>
    /// 從既有的事實裡挑出四個關鍵 PCR。
    ///
    /// <para>
    /// <b>這是一個「讀既有事實」的函式，而不是「問 TPM」的函式</b>——
    /// 它不解碼任何東西，只是從已經產出的事實裡取值。
    /// 找不到就回 <c>null</c>（呼叫端會標成「讀不到」，<b>不是零</b>）。
    /// </para>
    ///
    /// <para>
    /// 優先取 SHA-256 bank；只有 SHA-1 時才退回它，
    /// <b>而那個退回要看得出來</b>——因為兩個 bank 的全零長度不同。
    /// </para>
    /// </summary>
    public static PcrReadings? FromFacts(IReadOnlyList<HardwareFact> facts)
    {
        if (facts.Count == 0) return null;

        string? Get(int index)
        {
            var sha256 = facts.FirstOrDefault(f => f.Key == $"tpm.pcr.sha256.{index}");
            if (sha256 is not null && sha256.Availability == FactAvailability.Present)
                return sha256.Value;
            var sha1 = facts.FirstOrDefault(f => f.Key == $"tpm.pcr.sha1.{index}");
            if (sha1 is not null && sha1.Availability == FactAvailability.Present)
                return sha1.Value;
            return null;
        }

        var r = new PcrReadings(Get(0), Get(2), Get(4), Get(7));
        return r.Pcr0 is null && r.Pcr2 is null && r.Pcr4 is null && r.Pcr7 is null ? null : r;
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [MeasuredKey, EvidenceKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
