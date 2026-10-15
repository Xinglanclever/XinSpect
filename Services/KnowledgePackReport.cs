using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 知識包的離線更新宣告（ULTIMATE-1000 階段一 P1-033 – P1-038）。
///
/// <para>
/// <b>既有的 <see cref="KnowledgePackService"/> 已經把載入、雜湊推導、拒收理由都做好了</b>
/// （<c>knowledge.pack.status</c> 與 <c>knowledge.pack.tables</c> 兩把鍵）。
/// 這一支補的是<b>規格要求但既有實作沒有明說的那幾件事</b>——
/// 它們都是「使用者需要知道、但程式碼裡沒有說出口」的：
/// </para>
///
/// <list type="bullet">
/// <item><c>kp.version</c>——包的版次（既有實作只把它塞進 status 的字串裡）。</item>
/// <item><c>kp.digest</c>——完整性宣告本身（既有實作把 serialNumber 截斷後塞進 status）。</item>
/// <item><c>kp.snapshot_date</c>——快照日期（既有實作只在每張表後面附帶）。</item>
/// <item><c>kp.update_channel</c>——<b>沒有更新通道要明說</b>。這是最容易被漏掉的一項：
/// 「有更新機制」是使用者的預期，不講清楚他會以為過期資料會自己變新。</item>
/// <item><c>kp.swap.audit</c>——換包是否進了審計鏈。</item>
/// <item><c>kp.miss</c>——<b>查不到就顯示「未收錄」，不猜</b>。</item>
/// </list>
/// </summary>
public static class KnowledgePackReport
{
    public const string VersionKey = "kp.version";
    public const string DigestKey = "kp.digest";
    public const string SnapshotKey = "kp.snapshot_date";
    public const string ChannelKey = "kp.update_channel";
    public const string SwapAuditKey = "kp.swap.audit";
    public const string MissKey = "kp.miss";

    /// <summary>沒有更新通道是<b>設計</b>而不是缺陷——所以它是 <c>Present</c>，但要說得很清楚。</summary>
    public const string NoUpdateChannel =
        "沒有更新通道：知識表只在使用者手動換檔時更新，程式不會自己去拿新資料";

    private const string Category = "知識與更新";
    private const string Source = "內建表／knowledge-pack.json（離線）";

    /// <summary>產生 <c>kp.version</c>。</summary>
    public static HardwareFact ToVersionFact(DateTimeOffset at, KnowledgePackLoad load)
    {
        if (load.Pack is null)
            return new HardwareFact(VersionKey, Category, "知識包版次",
                "使用內建表（沒有外掛包）", "版", Source, FactTrustLevel.Reported, false, at,
                null, FactAvailability.NotSupported,
                load.Reason ?? "沒有外掛知識包");

        return new HardwareFact(VersionKey, Category, "知識包版次",
            $"第 {load.Pack.PackVersion} 版（生成 {load.Pack.GeneratedAt}）", "版", Source,
            FactTrustLevel.Measured, false, at, load.Pack.PackVersion, FactAvailability.Present, null);
    }

    /// <summary>
    /// 產生 <c>kp.digest</c>：完整雜湊，<b>不截斷</b>。
    ///
    /// <para>
    /// 既有實作把 serialNumber 截到 18 字元塞進狀態字串——那是為了畫面好看。
    /// 但<b>截斷的雜湊不能用來查證</b>：使用者要拿它去比對另一台機器上的同一份包時，
    /// 少掉的位元正好是他需要的那一段。所以這把鍵給完整的值。
    /// </para>
    /// </summary>
    public static HardwareFact ToDigestFact(DateTimeOffset at, KnowledgePackLoad load)
    {
        // 有包時：完整的序列號，一字不截。界線聲明放進 UnavailableReason——
        // 那個欄位在事實語意上是「補充說明」，而「這不是簽章」正是這把鍵
        // 最需要被讀到的一句話（**不得**因為它非空就當成缺陷：Availability 才是判準）。
        if (load.Pack is not null)
            return new HardwareFact(DigestKey, Category, "知識包完整性",
                load.Pack.SerialNumber, "", Source, FactTrustLevel.Measured, false, at, null,
                FactAvailability.Present, NonRepudiationNote);

        // 沒有外掛包時，回退到「載入時宣告的那個序列號」——它仍然值得顯示，
        // 因為拒收的包也有一個被宣告過的雜湊，而那個值正是查證時要比對的東西。
        if (!string.IsNullOrWhiteSpace(load.DeclaredSerial))
            return new HardwareFact(DigestKey, Category, "知識包完整性",
                load.DeclaredSerial!, "", Source, FactTrustLevel.Reported, false, at, null,
                FactAvailability.ReadError,
                $"包被拒收，但檔案宣告過這個雜湊：{load.Reason}");

        return new HardwareFact(DigestKey, Category, "知識包完整性",
            "沒有外掛包，無完整性宣告", "", Source, FactTrustLevel.Reported, false, at,
            null, FactAvailability.NotApplicable,
            "內建表沒有序列號——它隨程式發佈，完整性由執行檔本身的簽章涵蓋");
    }

    /// <summary>
    /// 完整性的界線聲明。<b>放進 Value 而不是 UnavailableReason</b>——
    /// 因為它不是「不可用的原因」（這把鍵是完好的），而是「這個數字能證明什麼、不能證明什麼」。
    /// 把界線塞進 UnavailableReason 會讓它看起來像缺陷，而它其實是常駐的說明。
    /// </summary>
    public const string NonRepudiationNote =
        "由「不含 serialNumber 欄位的 canonical JSON」以 SHA-256 推導；" +
        "可偵測改動與截斷，但不是簽章——不能阻止有能力重算雜湊的人改檔";

    /// <summary>產生 <c>kp.snapshot_date</c>：逐表列出快照日期，並指出最舊的一張。</summary>
    public static HardwareFact ToSnapshotFact(DateTimeOffset at, KnowledgePackLoad load)
    {
        if (load.Pack is null || load.Pack.Tables.Count == 0)
            return new HardwareFact(SnapshotKey, Category, "知識包快照日期",
                "使用內建表", "日", Source, FactTrustLevel.Reported, false, at,
                null, FactAvailability.NotSupported, KnowledgePackService.BuiltinSnapshot);

        string detail = string.Join("；", load.Pack.Tables.Select(t => $"{t.Table} {t.SnapshotDate}"));
        return new HardwareFact(SnapshotKey, Category, "知識包快照日期", detail, "張", Source,
            FactTrustLevel.Measured, false, at, load.Pack.Tables.Count, FactAvailability.Present, null);
    }

    /// <summary>
    /// 產生 <c>kp.update_channel</c>。
    ///
    /// <para>
    /// <b>這一項存在的理由是「不講清楚會被誤解」：</b>使用者看到一個「知識包」，
    /// 很自然會預期它會更新。這把鍵把「沒有更新通道」變成畫面上的一句話，
    /// 而不是一個要讀原始碼才知道的設計決定。
    /// </para>
    /// </summary>
    public static HardwareFact ToChannelFact(DateTimeOffset at)
    {
        return new HardwareFact(ChannelKey, Category, "更新通道",
            NoUpdateChannel, "", Source, FactTrustLevel.Reported, false, at, 0,
            // 「沒有更新通道」是刻意的設計，是確定的陳述——所以是 Present。
            FactAvailability.Present, null);
    }

    /// <summary>
    /// 產生 <c>kp.swap.audit</c>：換包動作是否進了審計鏈。
    /// <b>沒有換過包</b>與<b>換了但沒記錄</b>是兩件事——前者不適用，後者是缺陷。
    /// </summary>
    public static HardwareFact ToSwapAuditFact(DateTimeOffset at, int swaps, int recorded, string? lastReason)
    {
        if (swaps == 0)
            return new HardwareFact(SwapAuditKey, Category, "換包審計",
                "本輪沒有換包動作", "次", Source, FactTrustLevel.Reported, false, at,
                0, FactAvailability.NotApplicable, "沒有換包動作可以稽核");

        if (recorded >= swaps)
            return new HardwareFact(SwapAuditKey, Category, "換包審計",
                $"{swaps} 次換包全部寫入審計鏈（append-only、雜湊鏈可驗）", "次", Source,
                FactTrustLevel.Measured, false, at, swaps, FactAvailability.Present, null);

        return new HardwareFact(SwapAuditKey, Category, "換包審計",
            $"{swaps} 次換包，只有 {recorded} 次進審計鏈", "次", Source,
            FactTrustLevel.Measured, false, at, recorded, FactAvailability.ReadError,
            lastReason ?? $"有 {swaps - recorded} 次換包沒有留下紀錄——沒有紀錄的換包等於沒發生過");
    }

    /// <summary>
    /// 產生 <c>kp.miss</c>：包裡查不到的鍵有多少。
    ///
    /// <para>
    /// <b>這一項是「不猜」原則的具體實作：</b>查不到時顯示「未收錄」，
    /// 而不是回一個看起來合理的預設名稱。「未收錄」是<b>確定的陳述</b>
    /// （我們確實沒收錄），所以標 <c>Present</c>；而「猜一個名字」會產生
    /// 一個無法查證的假事實。
    /// </para>
    /// </summary>
    public static HardwareFact ToMissFact(DateTimeOffset at, int lookups, int misses,
        IReadOnlyList<string> missedKeys)
    {
        if (lookups == 0)
            return new HardwareFact(MissKey, Category, "未收錄查詢",
                "本輪沒有查詢知識表", "次", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有查詢可以統計");

        if (misses == 0)
            return new HardwareFact(MissKey, Category, "未收錄查詢",
                $"{lookups} 次查詢全部命中", "次", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.Present, null);

        return new HardwareFact(MissKey, Category, "未收錄查詢",
            $"{misses}/{lookups} 次查詢未收錄：" + string.Join("、", missedKeys.Take(10)), "次",
            Source, FactTrustLevel.Derived, false, at, misses,
            // 「未收錄」本身是確定的事實（我們確實沒收錄），所以這把鍵不是錯誤狀態；
            // 但它代表有東西沒被辨識，所以不是全綠。
            FactAvailability.NotSupported,
            $"{misses} 筆查不到對照——顯示為「未收錄」，不猜名稱");
    }
}
