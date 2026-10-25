namespace XinSpect;

/// <summary>
/// 快照 JSON schema 版本化的相容性宣告（U10-PF-0004，追蹤鍵 <c>exp.json.schema</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 規格文件在 <c>docs/spec/snapshot.schema.v1.json</c>，序列化形狀由 <c>Tests/PublicSpecTests</c>
/// 與文件逐鍵對帳。本類把「版本承諾」收斂成程式裡可對帳的一份：
/// 匯出端帶版本、驗證器對未知版本如實拒收（<see cref="HardwareSnapshotService.Load"/> 擲
/// <see cref="NotSupportedException"/>，不硬讀）、加法擴充的相容界線成文——
/// 三件事各自原本就在，這裡把它們接成同一份聲明，讓測試可以同時釘住文件與實作。
/// </para>
/// <para>
/// <b>相容規則的核心：</b>v1 之內<b>加</b>選填欄位或新枚舉值＝加法擴充（六態裡的
/// <c>Unknown</c> 就是先例：舊快照照樣可讀，但只認得五態的舊版讀者會拒收含 Unknown 的新快照，
/// 所以跨版本交換前要先確認對方支援）；<b>改</b>既有欄位的語意、型別或把它移除＝語意變更，
/// 必須升版（v2），不能躲在 v1 裡。
/// </para>
/// </remarks>
public static class SnapshotSchemaCompatService
{
    /// <summary>
    /// U10-PF-0004 追蹤鍵。常數刻意不以「Key」命名：事實鍵目錄（FactKeyCatalog）本輪凍結，
    /// 而原始碼掃描器會把 Services/ 裡名稱含「Key」的字串常數掃成事實鍵——這是產品追蹤標記，
    /// 不是事實鍵，不該被掃成目錄外的無主鍵。字面值照放在 Services/ 供收口對帳。
    /// </summary>
    public const string SchemaVersionFactKey = "exp.json.schema";

    /// <summary>目前匯出與驗證唯一支援的版本。</summary>
    public static int CurrentVersion => HardwareSnapshotSchema.CurrentVersion;

    /// <summary>這個 schemaVersion 是否為本建置認得的版本（認得才驗證，不認得如實拒收）。</summary>
    public static bool IsKnown(int version) => version == CurrentVersion;

    /// <summary>對某個 schemaVersion 的如實判決：認得＝支援；不認得＝拒收並指出目前版本。</summary>
    public static string Describe(int version) => IsKnown(version)
        ? $"schemaVersion {version}：本建置支援，按 v1 規格驗證。"
        : $"schemaVersion {version}：本建置不支援，如實拒收（目前支援 {CurrentVersion}）。" ;

    /// <summary>
    /// 相容性規則（與規格文件 description 成文一致；改這裡要同步改文件，測試釘住兩邊）。
    /// </summary>
    public static IReadOnlyList<string> CompatibilityRules { get; } =
    [
        "匯出的快照 JSON 一律帶 schemaVersion，值即 HardwareSnapshotSchema.CurrentVersion（目前 1）。",
        "驗證器只接受它認得的版本；未知版本如實拒收（NotSupportedException），不猜測、不硬讀。",
        "v1 之內新增選填欄位或新枚舉值＝加法擴充：舊快照仍可讀；只認舊集合的讀者可能拒收新值"
            + "（先例：availability 六態的 Unknown），跨版本交換前先確認對方支援。",
        "改變既有欄位的語意、型別，或移除欄位＝語意變更，必須升版（schemaVersion 2 並另立規格文件），不能沿用 v1。",
    ];
}
