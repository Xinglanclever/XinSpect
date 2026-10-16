using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// EFI 變數的簽章清單與屬性（ULTIMATE-1000 階段二 P2-079／P2-080／P2-083）。
///
/// <para>
/// <b>這一項把兩塊既有原料接起來，不新增解碼器：</b>
/// 讀 EFI 變數的路（<c>GetFirmwareEnvironmentVariableEx</c> ＋
/// <c>SeSystemEnvironmentPrivilege</c>，既有於 <c>FirmwareService</c>／<c>UefiBootFactsService</c>）
/// 與解簽章清單的 <see cref="EfiSigListDecoder"/>（既有，含 SpecRef 出處）。
/// </para>
///
/// <para>
/// <b>一把鍵涵蓋四個變數（PK／KEK／db／dbx），因為它們是同一種東西：</b>
/// 四個都是 <c>EFI_SIGNATURE_LIST</c> 的串接，只是用途不同
/// （PK＝平台金鑰、KEK＝金鑰交換金鑰、db＝允許清單、dbx＝撤銷清單）。
/// <b>把它們拆成四把鍵只會讓「這四個是同一種結構」這件事消失。</b>
/// </para>
///
/// <para>
/// <b>而屬性的位元不命名——這是第三次同一個判斷。</b>
/// UEFI 的變數屬性是 <c>EFI_VARIABLE_*</c> 常數（NON_VOLATILE 0x1、
/// BOOTSERVICE_ACCESS 0x2、RUNTIME_ACCESS 0x4…），而
/// <b>本專案沒有任何地方收錄那張表</b>。
/// 我知道其中幾個位元，但<b>「我記得」不是出處</b>——
/// 與 FIT 的 Key Manifest 編號、TPM 的 EV_* 型別名是同一個判斷：
/// <b>填錯的位元會讓一個不存在的屬性看起來存在。</b>
/// </para>
///
/// <para>
/// <b>界線：這裡報的是「變數裡有幾筆簽章」，不是「那些簽章有效」。</b>
/// 本服務不驗簽章、不判斷撤銷清單夠不夠新（那是 <c>secure.dbx</c> 的領域）。
/// </para>
/// </summary>
public static class EfiVariableFactsService
{
    private const string Category = "韌體安全";
    private const string Source = "UEFI 變數（GetFirmwareEnvironmentVariableEx）＋ EFI_SIGNATURE_LIST 解碼";

    public const string KeysKey = "ev.pk.count";
    public const string AttrsKey = "ev.var.attrs";

    /// <summary>
    /// 一個 EFI 變數的讀取結果。
    /// <c>Bytes</c> 為 <c>null</c> 代表變數不存在或讀不到；<c>Attributes</c> 是原始屬性值。
    /// </summary>
    public sealed record VariableRead(string Name, byte[]? Bytes, uint Attributes, bool Privileged);

    /// <summary>本服務會看的四個簽章清單變數（PK／KEK／db／dbx）。</summary>
    public static readonly IReadOnlyList<string> SignatureVariables = ["PK", "KEK", "db", "dbx"];

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<VariableRead>? reads)
    {
        if (reads is null)
            return Na(at, "沒有變數讀取結果——本服務不自己去問韌體"
                + "（那條路徑有它自己的可及性事實：uefi.secure_boot／uefi.setup_mode）",
                FactAvailability.NotSupported);

        // 有沒有權限，是整組的性質——先講清楚。
        if (reads.Count > 0 && reads.All(r => !r.Privileged))
            return
            [
                new HardwareFact(KeysKey, Category, "簽章清單變數", "", "個", Source,
                    FactTrustLevel.Reported, false, at, null,
                    FactAvailability.InsufficientPrivilege,
                    "**無法啟用 SeSystemEnvironmentPrivilege（需管理員；或本機為 Legacy 開機）。**"
                    + "**「沒權限讀」與「變數不存在」是不同的斷言**——"
                    + "既有的 BackendEnvironmentService 就因為把兩者混在一起而修過一次"
                    + "（AdjustTokenPrivileges 未持有權限時仍回 true，"
                    + "要查 LastError 才不會讓「沒權限讀」被誤報成「變數不存在＝可能 Legacy 開機」）"),
                Attrs(at, reads),
            ];

        var present = reads.Where(r => r.Bytes is not null).ToList();
        var lists = new List<(string Name, EfiSignatureListInfo Info)>();
        foreach (var r in present)
            foreach (var info in EfiSigListDecoder.Decode(r.Bytes!))
                lists.Add((r.Name, info));

        int totalSigs = lists.Sum(l => l.Info.SignatureCount);
        string detail = present.Count == 0
            ? "四個簽章清單變數都讀不到"
            : string.Join("；", SignatureVariables.Select(name =>
            {
                var r = reads.FirstOrDefault(x => x.Name == name);
                if (r is null) return $"{name} 未讀取";
                if (r.Bytes is null) return $"{name} 不存在";
                var mine = lists.Where(l => l.Name == name).ToList();
                int n = mine.Sum(l => l.Info.SignatureCount);
                int k = mine.Count;
                return $"{name} {r.Bytes.Length} bytes／{k} 條清單／{n} 筆簽章";
            }));

        return
        [
            new HardwareFact(KeysKey, Category, "簽章清單變數", detail, "筆", Source,
                FactTrustLevel.Measured, false, at,
                // **四個都讀不到時這個數字是空的，不是 0。**
                // 這是本專案第五次犯同型錯誤（MeasuredBoot／TrustRoot／
                // UefiSectionTree 的 TreeKey 與 DepthKey 各一次）：
                // 「有 0 筆簽章」與「沒有變數可以數」是不同的斷言。
                present.Count == 0 ? null : totalSigs,
                present.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
                (present.Count == 0
                    ? "**四個變數都讀不到。**權限是通的（否則整組會標沒權限），"
                      + "所以這代表它們真的不存在——而那是 CSM／Legacy 開機的正常樣貌"
                    : "**這裡報的是「變數裡有幾筆簽章」，不是「那些簽章有效」。**"
                      + "本服務不驗簽章、不判斷撤銷清單夠不夠新"
                      + "（**那是 secure.dbx 的領域**，它比對 SVN 與知識包）。"
                      + "而 PK／KEK／db／dbx 是**同一種結構的四種用途**——"
                      + "把它們拆成四把鍵只會讓「它們是同一種東西」這件事消失")
                + "。**讀得到與否也如實分開**：「沒權限讀」在上面那條分支單獨處理，"
                + "而這裡的「不存在」是確定沒有"),
            Attrs(at, reads),
        ];
    }

    private static HardwareFact Attrs(DateTimeOffset at, IReadOnlyList<VariableRead> reads)
    {
        if (reads.Count == 0)
            return new HardwareFact(AttrsKey, Category, "變數屬性與長度", "", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
                "沒有讀取結果就沒有屬性可言");

        string detail = string.Join("；", reads.Select(r => r.Bytes is null
            ? $"{r.Name} 不存在"
            : $"{r.Name} 屬性 0x{r.Attributes:X8}／{r.Bytes.Length} bytes"));

        return new HardwareFact(AttrsKey, Category, "變數屬性與長度", detail, "", Source,
            FactTrustLevel.Reported, false, at, reads.Count(r => r.Bytes is not null),
            reads.Any(r => r.Bytes is not null) ? FactAvailability.Present : FactAvailability.NotApplicable,
            "**屬性的位元不命名，只列原始值。**"
            + "UEFI 的變數屬性是 EFI_VARIABLE_* 常數（NON_VOLATILE 0x1、"
            + "BOOTSERVICE_ACCESS 0x2、RUNTIME_ACCESS 0x4…），而**本專案沒有任何地方收錄那張表**。"
            + "我知道其中幾個位元，但**「我記得」不是出處**——"
            + "與 FIT 的 Key Manifest 編號、TPM 的 EV_* 型別名是同一個判斷："
            + "**填錯的位元會讓一個不存在的屬性看起來存在。**"
            + "要補上位元名，需要先對準 UEFI Spec §8.2 的 EFI_VARIABLE_* 表出處，"
            + "再把它加成一張有出處的表（**那是一處改、多處受益**）"
            + "。而**「唯讀／執行期可寫」這個判斷需要位元名才做得出來**——"
            + "所以規格 P2-083 的後半在對準出處之前不算完成");
    }

    private static IReadOnlyList<HardwareFact> Na(DateTimeOffset at, string reason,
        FactAvailability availability)
    {
        string[] keys = [KeysKey, AttrsKey];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason)).ToList();
    }
}
