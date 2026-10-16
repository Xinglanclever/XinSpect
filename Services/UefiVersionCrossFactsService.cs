using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XinSpect;

/// <summary>
/// 映像與韌體版本一致性（ULTIMATE-1000 階段二 P2-050）。
///
/// <para>
/// <b>這一項比對兩個獨立來源的版本字串：</b>
/// </para>
/// <list type="bullet">
///   <item><b>韌體宣告的</b>——SMBIOS Type 0 的 BIOS 版本與日期
///     （既有的 <c>fw.version</c>／<c>fw.date</c>）。</item>
///   <item><b>映像裡內嵌的</b>——UEFI PI 的 VERSION 區段（型別 0x14），
///     內容是 <c>BuildNumber u16</c> ＋ UTF-16LE 版本字串。</item>
/// </list>
///
/// <para>
/// <b>三態，而這是本項最重要的一點：</b>
/// <b>一致／不一致／無從對照</b>——第三種<b>永遠不可以被算成第二種</b>。
/// 映像裡沒有 VERSION 區段、或那個字串讀不出來、或韌體宣告的版本拿不到，
/// 都是「無從對照」，而那與「兩個版本不一樣」是不同的斷言。
/// </para>
///
/// <para>
/// <b>而本服務不給一致率。</b>那個政策語句出現在<b>每一個分支</b>裡——
/// 因為一個只在「有對照」時才出現的聲明，會在沒有對照時被誤讀成默許。
/// （這是 P2-033 修過一次的地方。）
/// </para>
///
/// <para>
/// <b>界線：字串相等不等於版本正確。</b>
/// 本服務只說「這兩個來源的字串一樣」——它不判斷哪一個才對，
/// 也沒有「正確版本」可以比對（那需要原廠映像，見 P2-048 的領域）。
/// </para>
/// </summary>
public static class UefiVersionCrossFactsService
{
    private const string Category = "韌體";
    private const string Source = "UEFI PI VERSION 區段（型別 0x14）對 SMBIOS Type 0 的宣告版本";

    public const string CrossKey = "uefi.version.cross";
    public const string EmbeddedKey = "uefi.version.embedded";

    /// <summary>UEFI PI 的 VERSION 區段型別。</summary>
    public const byte SectTypeVersion = 0x14;

    /// <summary>映像裡的一個版本字串。</summary>
    public sealed record EmbeddedVersion(int FvIndex, string FileGuid, string? UiName,
        ushort BuildNumber, string VersionString);

    /// <summary>解一個 VERSION 區段的內容：BuildNumber u16 ＋ UTF-16LE 字串。</summary>
    public static (ushort BuildNumber, string Text)? DecodeVersionSection(byte[] payload)
    {
        if (payload.Length < 3) return null;   // 至少要有 BuildNumber(2) ＋ 一個字元(2)
        ushort build = BitConverter.ToUInt16(payload, 0);
        var sb = new StringBuilder();
        for (int i = 2; i + 1 < payload.Length; i += 2)
        {
            char c = (char)BitConverter.ToUInt16(payload, i);
            if (c == '\0') break;
            sb.Append(c);
        }
        return (build, sb.ToString());
    }

    /// <summary>從既有解碼結果蒐集映像內嵌的版本。</summary>
    public static IReadOnlyList<EmbeddedVersion> ReadEmbedded(
        IReadOnlyList<UefiFvInfo>? fvs, byte[]? image)
    {
        if (fvs is null || image is null) return [];

        var found = new List<EmbeddedVersion>();
        foreach (var fv in fvs)
            foreach (var file in fv.Files)
                foreach (var sect in file.Sections)
                {
                    if (sect.TypeName != "VERSION") continue;

                    long start = (long)sect.DataOffset;
                    long payload = (long)sect.Size - 4;
                    if (start < 0 || payload <= 0 || start + payload > image.Length) continue;

                    var bytes = new byte[payload];
                    Array.Copy(image, start, bytes, 0, payload);
                    var decoded = DecodeVersionSection(bytes);
                    if (decoded is not { } d) continue;

                    found.Add(new EmbeddedVersion(fv.Index, file.Guid, file.UiName,
                        d.BuildNumber, d.Text));
                }
        return found;
    }

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        string? declaredVersion, IReadOnlyList<EmbeddedVersion>? embedded)
    {
        if (embedded is null)
            return Na(at, "沒有映像掃描結果——本服務不自己去讀 BIOS 區"
                + "（那條路徑有它自己的可及性事實）", FactAvailability.NotSupported);

        if (embedded.Count == 0)
            return
            [
                new HardwareFact(CrossKey, Category, "映像與韌體版本一致性", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    NoBasis(declaredVersion, hasEmbedded: false)),
                new HardwareFact(EmbeddedKey, Category, "映像內嵌的版本字串", "", "個", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "映像裡沒有 VERSION 區段（型別 0x14）——**那可能是這顆韌體確實沒有內嵌版本字串**，"
                    + "也可能它藏在壓縮區段裡（本版不遞迴進入）。"
                    + "**這裡的數量刻意留空而不是 0**——"
                    + "「沒有找到條目」與「找到 0 個條目」在走訪沒有完成時是不同的斷言"),
            ];

        string embeddedText = string.Join("；", embedded.Select(e =>
            $"{e.VersionString}（Build {e.BuildNumber}，FV#{e.FvIndex}"
            + (e.UiName is { Length: > 0 } n ? $"／{n}" : "") + "）"));

        var embeddedFact = new HardwareFact(EmbeddedKey, Category, "映像內嵌的版本字串",
            embeddedText, "個", Source, FactTrustLevel.Measured, false, at, embedded.Count,
            FactAvailability.Present,
            "**這是映像自己帶的版本字串**，與韌體在 SMBIOS 裡宣告的是兩個獨立來源。"
            + "它們相同不代表正確——**只代表這兩個來源沒有互相矛盾**");

        // ── 三態 ────────────────────────────────────────────────────────
        if (declaredVersion is null or "")
            return
            [
                new HardwareFact(CrossKey, Category, "映像與韌體版本一致性", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    NoBasis(declaredVersion, hasEmbedded: true)),
                embeddedFact,
            ];

        var matches = embedded.Where(e =>
            string.Equals(e.VersionString.Trim(), declaredVersion.Trim(),
                StringComparison.OrdinalIgnoreCase)).ToList();

        bool any = matches.Count > 0;
        string value = any
            ? $"一致（{matches.Count} 個條目）"
            : $"不一致：映像「{embedded[0].VersionString}」vs 韌體宣告「{declaredVersion}」"
              + (embedded.Count > 1 ? $"（映像共 {embedded.Count} 個版本條目）" : "");

        return
        [
            new HardwareFact(CrossKey, Category, "映像與韌體版本一致性", value, "", Source,
                FactTrustLevel.Derived, false, at, any ? 1 : 0,
                any ? FactAvailability.Present : FactAvailability.NotApplicable,
                (any
                    ? "**兩個獨立來源的版本字串一樣。**那不代表它是「正確版本」——"
                      + "只代表它們沒有互相矛盾。要判斷正確性需要原廠映像（那是另一件事）"
                    : "**不一致不等於有問題。**映像裡的版本字串與 SMBIOS 宣告的可能"
                      + "本來就描述不同的東西（例如一個是韌體包版本、一個是主機板廠的型號字串）。"
                      + "**本服務指出差異，不判斷誰對。**")
                + Policy),
            embeddedFact,
        ];
    }

    /// <summary>
    /// 「無從對照」的說明。
    ///
    /// <para>
    /// <b>結構：通則在前、分支補充在後。</b>第一版我用一個 <c>bool</c>
    /// 去選兩種措辭，而**那兩種措辭一個含通則、一個不含**——
    /// 於是「缺一邊就沒有對照可言」只在一半的分支裡出現。
    /// </para>
    ///
    /// <para>
    /// 那句話是這個狀態的<b>通則</b>（缺內嵌、缺宣告、或兩者都缺都成立），
    /// 而 <paramref name="hasEmbedded"/> 只決定<b>補充說明缺的是哪一邊</b>。
    /// </para>
    /// </summary>
    private static string NoBasis(string? declaredVersion, bool hasEmbedded)
        => "**「無從對照」不是「不一致」。**"
           + "版本比對需要兩邊都有：**缺一邊就沒有對照可言**。"
           + (hasEmbedded
               ? "這裡映像裡有版本字串，**但韌體宣告的版本拿不到**"
                 + "（SMBIOS Type 0 不可讀或沒有該欄位）"
               : "這裡映像裡沒有版本字串**且**韌體宣告也可能拿不到")
           + "——把這種情況算成「不一致」會讓一台正常的機器看起來像版本不符。"
           + Policy;

    /// <summary>
    /// 政策語句，**出現在每一個分支裡**。
    /// 一個只在「有對照」時才出現的聲明，會在沒有對照時被誤讀成默許。
    /// </summary>
    private const string Policy =
        "；**本服務不給一致率**——它只對觀察到的條目陳述，"
        + "而「有幾成一致」需要先界定分母，那會把「無從對照」偷渡成「不一致」";

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [CrossKey, EmbeddedKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
