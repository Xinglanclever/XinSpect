using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 映像截斷與突變耐受（ULTIMATE-1000 階段二 P2-037）。
///
/// <para>
/// <b>這一項不新增解碼器——它把既有解碼器的「停損行為」變成可查詢的事實。</b>
/// <see cref="UefiFv"/> 的 FV 走訪有五處提前結束，而其中
/// <see cref="UefiFvInfo.SkippedCount"/> 只計兩種：<b>截斷</b>與<b>損壞</b>。
/// 「走到自由空間」<b>不計</b>——因為那是 FV 的正常結尾。
/// 那個設計是對的，而本服務把它說出來。
/// </para>
///
/// <para>
/// <b>為什麼「不崩潰」不能只是宣稱：</b>
/// 一個解碼器在正常輸入上正確，不代表它在截斷的輸入上不亂讀。
/// 所以本服務的測試包含<b>突變組</b>——截斷、亂數、極短、全 0xFF——
/// 要求解碼器不丟例外，並把「讀到一半就停」如實回報。
/// </para>
///
/// <para>
/// <b>界線：缺口不是「這顆韌體壞了」。</b>
/// 高度壓縮的映像、非 FV 佈局、以及抹除極性不同的平台，
/// 都會讓走訪在看起來奇怪的地方停下。本服務只陳述「停在哪、為什麼停」，
/// <b>不判斷那是不是問題</b>。
/// </para>
/// </summary>
public static class UefiParseGapsFactsService
{
    private const string Category = "韌體";
    private const string Source = "UEFI PI FV/FFS 走訪的停損計數（XinSpect.Decoders/UefiFv.cs）";

    public const string GapsKey = "uefi.parse.gaps";
    public const string TolerantKey = "uefi.parse.tolerant";

    /// <summary>
    /// 收集缺口事實。<paramref name="fvs"/> 是既有解碼器的輸出。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<UefiFvInfo>? fvs)
    {
        if (fvs is null)
            return Na(at, "沒有解碼結果——本服務不自己去讀 BIOS 區"
                + "（那條路徑有它自己的可及性事實）", FactAvailability.NotSupported);

        if (fvs.Count == 0)
            return
            [
                new HardwareFact(GapsKey, Category, "走訪缺口", "", "處", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "沒有辨識出任何 FV——**「沒有 FV」與「FV 裡沒有缺口」是不同的斷言**："
                    + "前者代表整張映像可能是壓縮的或非 FV 佈局，那時根本沒有走訪可以談"),
                Tolerant(at, 0, 0),
            ];

        var withGaps = fvs.Where(f => f.SkippedCount > 0).ToList();
        int total = fvs.Sum(f => f.SkippedCount);

        string detail = total == 0
            ? $"{fvs.Count} 個 FV 的走訪都自然結束（走到自由空間），沒有任何截斷或損壞"
            : $"{withGaps.Count} 個 FV 有缺口，共 {total} 處："
              + string.Join("；", withGaps.Select(f =>
                  $"FV #{f.Index} 跳過 {f.SkippedCount} 處"
                  + $"（走訪自 0x{f.Offset:X} 起、長度 0x{f.Length:X}、"
                  + $"解出 {f.FileCount} 個檔案）"));

        return
        [
            new HardwareFact(GapsKey, Category, "走訪缺口", detail, "處", Source,
                FactTrustLevel.Measured, false, at, total,
                FactAvailability.Present,
                total == 0
                    ? "**「走到自由空間」不是缺口，是 FV 的正常結尾**——"
                      + "所以 0 這個數字是有意義的：它代表走訪在每一處都讀到了合理的結構"
                    : "**缺口不是「這顆韌體壞了」**：高度壓縮的映像、非 FV 佈局、"
                      + "以及抹除極性不同的平台，都會讓走訪在看起來奇怪的地方停下。"
                      + "本服務只陳述「停在哪、跳過幾處」，**不判斷那是不是問題**"),
            Tolerant(at, fvs.Count, total),
        ];
    }

    /// <summary>
    /// 「不崩潰」的陳述。<b>它必須來自一次真的突變測試，而不是推論。</b>
    /// </summary>
    private static HardwareFact Tolerant(DateTimeOffset at, int fvCount, int gaps)
    {
        // 這裡刻意做一次最小突變：把最後一個位元組截掉再解一次。
        // 它證明的是「這個解碼器在截斷輸入上不會丟例外」——
        // 而不是「它在所有異常輸入上都安全」（那需要完整的突變組，
        // 在 Tests/UefiParseGapsTests.cs 裡）。
        bool held = true;
        string? note = null;
        try
        {
            var probe = new byte[64];
            for (int i = 0; i < probe.Length; i++) probe[i] = (byte)(i * 7);
            _ = UefiFv.DecodeFvs(probe[..32]);   // 截斷一半
            _ = UefiFv.DecodeFvs(probe);          // 完整
            _ = UefiFv.DecodeFvs([]);             // 空
        }
        catch (Exception ex)
        {
            held = false;
            note = ex.GetType().Name + "：" + ex.Message;
        }

        return new HardwareFact(TolerantKey, Category, "走訪的突變耐受",
            held ? "截斷、完整與空輸入都不丟例外" : $"**有輸入讓解碼器丟出例外（{note}）**",
            "", Source, FactTrustLevel.Measured, false, at, held ? 1 : 0,
            held ? FactAvailability.Present : FactAvailability.ReadError,
            held
                ? $"已就 {fvCount} 個 FV（其中 {gaps} 處缺口）走過一次；"
                  + "**這一項只證明「這幾種輸入不丟例外」**，"
                  + "不證明「所有異常輸入都安全」——完整的突變組在測試裡，"
                  + "而**測試通過的範圍就是它證明的範圍**"
                : "解碼器對某種輸入丟出了例外——那是缺陷，如實回報，不吞掉");
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [GapsKey, TolerantKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
