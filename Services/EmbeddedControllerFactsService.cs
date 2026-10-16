using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 嵌入式控制器（EC）的界線與存在性（ULTIMATE-1000 階段二 P2-092／P2-093）。
///
/// <para>
/// <b>這一項的形狀由規格自己指定：</b>P2-092 說「<b>陳述 EC 埠不讀寫的界線</b>」、
/// P2-093 說「<b>由既有表間接陳述</b>」。<b>兩句合起來就是「不要直接碰 EC」。</b>
/// </para>
///
/// <para>
/// <b>為什麼不碰：</b>EC 的 I/O 埠（0x62／0x66）走的是有狀態的握手
/// （寫命令 → 等 IBF／OBF → 讀資料），而在現代機器上那個埠通常由 ACPI 的
/// EC 驅動持有。<b>直接讀會與韌體／OS 打架</b>——那不是「讀到錯的值」，
/// 而是「讓別人的值變錯」。<b>所以本專案不做，而那件事要被說出來。</b>
/// </para>
///
/// <para>
/// <b>存在性改由 ACPI 表間接得知：</b>ECDT（Embedded Controller Boot Resources Table）
/// 存在就代表平台有 EC 的開機資源描述。既有的 <c>acpi.table.*</c> 逐表列舉
/// （動態家族）已經把簽章清單攤開，<b>所以這一項不新增讀取，只做判定。</b>
/// </para>
///
/// <para>
/// <b>界線：沒有 ECDT 不代表沒有 EC。</b>ECDT 是<b>可選</b>的表
/// （ACPI 規格允許以 DSDT 裡的 <c>PNP0C09</c> 裝置描述 EC 而不提供 ECDT）。
/// 本服務<b>只陳述「有沒有從 ACPI 表看到 ECDT」</b>，不宣告「這台機器沒有 EC」。
/// </para>
/// </summary>
public static class EmbeddedControllerFactsService
{
    private const string Category = "平台";
    private const string Source = "ACPI 表簽章清單（既有的 acpi.table.* 逐表列舉）";

    public const string PresentKey = "ec.present";
    public const string BoundaryKey = "ec.boundary";

    /// <summary>ECDT 的 ACPI 簽章。</summary>
    public const string EcdtSignature = "ECDT";

    /// <summary>
    /// <paramref name="acpiSignatures"/> 是既有的逐表列舉結果（簽章清單）。
    /// <c>null</c> 代表那條路讀不到。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<string>? acpiSignatures)
    {
        if (acpiSignatures is null)
            return
            [
                new HardwareFact(PresentKey, Category, "嵌入式控制器（EC）", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                    "**ACPI 表清單讀不到，所以無從間接判斷。**"
                    + "**「表清單讀不到」與「表清單裡沒有 ECDT」是不同的斷言**——"
                    + "前者代表那條路不通，後者才代表平台沒有提供 ECDT"),
                Boundary(at),
            ];

        var hits = acpiSignatures
            .Where(s => string.Equals(s, EcdtSignature, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return
        [
            new HardwareFact(PresentKey, Category, "嵌入式控制器（EC）",
                hits.Count == 0
                    ? "沒有從 ACPI 表看到 ECDT"
                    : $"有 ECDT（{hits.Count} 張）",
                hits.Count == 0 ? "" : "張", Source, FactTrustLevel.Derived, false, at,
                // **沒有 ECDT 時不帶數字。**「沒有」不是一個量測結果，而是一個否定——
                // 傳 0 會讓它看起來像「找到 0 張」。型別不變式會把它收斂成 null，
                // 而這裡直接寫對，因為那個收斂是安全網、不是設計。
                hits.Count == 0 ? null : hits.Count,
                hits.Count == 0 ? FactAvailability.NotApplicable : FactAvailability.Present,
                (hits.Count == 0
                    ? "**「沒有 ECDT」與「沒有 EC」是不同的斷言。**"
                      + "ECDT 是可選的表——ACPI 規格允許以 DSDT 裡的 PNP0C09 裝置描述 EC "
                      + "而不提供 ECDT。**所以這裡只說「沒有從 ACPI 表看到它」。**"
                    : "**ECDT 存在代表平台有 EC 的開機資源描述。**"
                      + "本服務**不讀 ECDT 的內容**——那需要解析它的欄位，"
                      + "而存在性這個問題用簽章就回答了")),
            Boundary(at),
        ];
    }

    private static HardwareFact Boundary(DateTimeOffset at)
        => new(BoundaryKey, Category, "EC 存取界線", "本專案不讀寫 EC 埠", "", Source,
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
            "**EC 的 I/O 埠（0x62／0x66）走的是有狀態的握手**"
            + "（寫命令 → 等 IBF／OBF → 讀資料），而在現代機器上那個埠"
            + "通常由 ACPI 的 EC 驅動持有。**直接讀會與韌體／OS 打架**——"
            + "那不是「讀到錯的值」，而是**「讓別人的值變錯」**。"
            + "所以本專案不做。"
            + "**而這把鍵存在的理由就是說出那件事**——"
            + "一個沒說出口的界線，會被讀成「還沒做」而不是「刻意不做」。"
            + "規格自己也是這樣寫的：P2-092 的目標是「陳述 EC 埠不讀寫的界線」，"
            + "而 P2-093 的存在性走「由既有表間接陳述」那條路");
}
