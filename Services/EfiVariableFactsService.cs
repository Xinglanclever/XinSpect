using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// EFI 變數的屬性與長度（ULTIMATE-1000 階段二 P2-083）。
///
/// <para>
/// <b>這一項的範圍在撤回簽章清單那一半之後變得明確。</b>
/// 原本它還產出 <c>ev.pk.count</c>（PK／KEK／db／dbx 的簽章計數），
/// 而<b>那已經有實作了</b>——<see cref="UefiSignatureFactsService"/>
/// 產出 <c>uefi.pk</c>／<c>uefi.kek</c>／<c>uefi.db</c>／<c>uefi.dbx</c>，
/// 而且做得更完整（逐清單明細、Boot#### 條目）。
/// <b>所以那一半被撤回了</b>，P2-079／P2-080 改列 alias。
///
/// <para>
/// <b>⚠ 2026-10-18 更正：這裡原本把「兩段式大小查詢」列為它的優點，而那是錯的。</b>
/// 查證後確認那個寫法把 <c>GetFirmwareEnvironmentVariableEx</c> 的第五個參數
/// （<c>pdwAttributes</c>，in/out）當成「所需大小」在用——<b>以 dwSize＝0 呼叫不會回報大小</b>，
/// 所以 <c>size</c> 永远是 0、<b>整條通路每個變數都讀不到</b>，
/// 而同一次查證還發現 <c>db</c>／<c>dbx</c> 的 GUID 也是錯的。
/// 兩者都已修掉，實作收斂到 <see cref="FirmwareVariableAccess"/>；
/// 真機實測（同一台機器）<c>PK</c> 886 bytes、<c>KEK</c> 3955、<c>db</c> 8188、<c>dbx</c> 3724。
/// 詳見 <c>GATE-LOG-261.md</c>。
/// </para>
/// </para>
///
/// <para>
/// <b>教訓：</b>我做那一半之前查了「規格鍵名」（<c>ev.pk.count</c> 不存在）
/// 與「領域詞」（EFI 變數），<b>但沒有查「既有的簽章清單實作」</b>——
/// 而那個實作就在 <c>UefiSignatureFactsService</c>，鍵名是 <c>uefi.*</c>
/// 而不是規格的 <c>ev.*</c>。
/// <b>「規格鍵名不存在」不等於「這件事沒人做」。</b>
/// </para>
///
/// <para>
/// <b>留下來的是屬性那一半，因為既有服務沒有它：</b>
/// <c>GetFirmwareEnvironmentVariableEx</c> 的 <c>pdwAttrib</c> 參數
/// 在既有的讀取裡被丟掉了，而<b>屬性是 P2-083 要的東西。</b>
/// </para>
///
/// <para>
/// <b>而屬性的位元不命名——這是第三次同一個判斷。</b>
/// UEFI 的變數屬性是 <c>EFI_VARIABLE_*</c> 常數（NON_VOLATILE 0x1、
/// BOOTSERVICE_ACCESS 0x2、RUNTIME_ACCESS 0x4…），而
/// <b>本專案沒有任何地方收錄那張表</b>。
/// 我知道其中幾個位元，但<b>「我記得」不是出處</b>——
/// 與 FIT 的 Key Manifest 編號、TPM 的 EV_* 型別名是同一個判斷。
/// </para>
/// </summary>
public static class EfiVariableFactsService
{
    private const string Category = "韌體安全";
    private const string Source = "UEFI 變數（GetFirmwareEnvironmentVariableEx 的 pdwAttrib）";

    public const string AttrsKey = "ev.var.attrs";

    /// <summary>本服務會看的四個簽章清單變數（PK／KEK／db／dbx）。</summary>
    public static readonly IReadOnlyList<string> SignatureVariables = ["PK", "KEK", "db", "dbx"];

    /// <summary>
    /// 一個 EFI 變數的讀取結果。
    /// <c>Bytes</c> 為 <c>null</c> 代表變數不存在或讀不到；<c>Attributes</c> 是原始屬性值。
    /// </summary>
    public sealed record VariableRead(string Name, byte[]? Bytes, uint Attributes, bool Privileged);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<VariableRead>? reads)
    {
        if (reads is null)
            return
            [
                new HardwareFact(AttrsKey, Category, "變數屬性與長度", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                    "沒有變數讀取結果——本服務不自己去問韌體"
                    + "（那條路徑有它自己的可及性事實：uefi.secure_boot／uefi.setup_mode）"),
            ];

        if (reads.Count > 0 && reads.All(r => !r.Privileged))
            return
            [
                new HardwareFact(AttrsKey, Category, "變數屬性與長度", "", "", Source,
                    FactTrustLevel.Reported, false, at, null,
                    FactAvailability.InsufficientPrivilege,
                    "**無法啟用 SeSystemEnvironmentPrivilege（需管理員；或本機為 Legacy 開機）。**"
                    + "**「沒權限讀」與「變數不存在」是不同的斷言**——"
                    + "既有的 FirmwareService 就因為把兩者混在一起而修過一次"
                    + "（AdjustTokenPrivileges 未持有權限時仍回 true，"
                    + "要查 LastError 才不會讓「沒權限讀」被誤報成「變數不存在＝可能 Legacy 開機」）"),
            ];

        if (reads.Count == 0)
            return
            [
                new HardwareFact(AttrsKey, Category, "變數屬性與長度", "", "", Source,
                    FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
                    "沒有讀取結果就沒有屬性可言"),
            ];

        string detail = string.Join("；", reads.Select(r => r.Bytes is null
            ? $"{r.Name} 不存在"
            : $"{r.Name} 屬性 0x{r.Attributes:X8}／{r.Bytes.Length} bytes"));

        return
        [
            new HardwareFact(AttrsKey, Category, "變數屬性與長度", detail, "", Source,
                FactTrustLevel.Reported, false, at, reads.Count(r => r.Bytes is not null),
                reads.Any(r => r.Bytes is not null)
                    ? FactAvailability.Present : FactAvailability.NotApplicable,
                "**屬性的位元不命名，只列原始值。**"
                + "UEFI 的變數屬性是 EFI_VARIABLE_* 常數（NON_VOLATILE 0x1、"
                + "BOOTSERVICE_ACCESS 0x2、RUNTIME_ACCESS 0x4…），而**本專案沒有任何地方收錄那張表**。"
                + "我知道其中幾個位元，但**「我記得」不是出處**——"
                + "與 FIT 的 Key Manifest 編號、TPM 的 EV_* 型別名是同一個判斷："
                + "**填錯的位元會讓一個不存在的屬性看起來存在。**"
                + "要補上位元名，需要先對準 UEFI Spec §8.2 的 EFI_VARIABLE_* 表出處，"
                + "再把它加成一張有出處的表（**那是一處改、多處受益**）"
                + "。而**「唯讀／執行期可寫」這個判斷需要位元名才做得出來**——"
                + "所以規格 P2-083 的後半在對準出處之前不算完成。"
                + "**而簽章清單那一半已經撤回了**：它已經有實作"
                + "（UefiSignatureFactsService 的 uefi.pk／uefi.kek／uefi.db／uefi.dbx），"
                + "而且做得更完整（逐清單明細、Boot#### 條目）——**而它的讀取通路在 2026-10-18 修掉兩個"
                + "讓它讀不到東西的缺陷（權限沒啟用、pdwAttrib 當大小用、db/dbx 的 GUID 錯）**"),
        ];
    }
}
