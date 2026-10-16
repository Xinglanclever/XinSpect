using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>硬體時間膠囊 JSON 格式的目前版本。</summary>
public static class HardwareSnapshotSchema
{
    public const int CurrentVersion = 1;
}

/// <summary>事實的可信程度；這是來源品質標示，不是保證。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FactTrustLevel>))]
public enum FactTrustLevel
{
    Unknown,
    Reported,
    Derived,
    Measured,
}

/// <summary>
/// 事實的可用性六態。誠實原則的型別承載：讀不到就說讀不到，不以 0／0xFF／舊值填補。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼是六態而不是五態：</b>原本的 <c>Present</c>／<c>NotSupported</c>／
/// <c>InsufficientPrivilege</c>／<c>ReadError</c>／<c>NotApplicable</c> 有一個缺口——
/// <b>「有值但我們不確定它算不算數」無處可放</b>。實務上會遇到三種情形需要它：
/// ①值來自快取或上一次開機的殘留；②值通過了讀取但來源本身可疑（例如驅動回了預設值而非真實值）；
/// ③兩條路徑給了不同答案、尚在等第三條仲裁。這三種情形若硬塞進 <c>Present</c>，
/// 就會被當成已確認的事實——那正是本專案最不該犯的錯。
/// </para>
/// <para>
/// <b>六態之間有偏序關係</b>（見 <see cref="FactStateLattice"/>）：
/// 衍生事實的可用性必須由來源事實依這個格傳播，<b>永不靜默塌成單一值</b>。
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<FactAvailability>))]
public enum FactAvailability
{
    /// <summary>讀到了，且來源與路徑都確認。</summary>
    Present,
    /// <summary>硬體或平台不支援這項查詢（環境事實，不是錯誤）。</summary>
    NotSupported,
    /// <summary>有這條路徑但權限不足（提權後可得）。</summary>
    InsufficientPrivilege,
    /// <summary>嘗試過但讀取失敗（呼叫錯誤、逾時、裝置無回應）。</summary>
    ReadError,
    /// <summary>本機不適用這項查詢（例如沒有 BMC 就沒有 IPMI 通路）。</summary>
    NotApplicable,
    /// <summary>
    /// 有值但未確認：快取殘留、來源可疑、或多來源尚未仲裁。
    /// <b>不得當成 <see cref="Present"/> 使用</b>——它的存在就是為了不讓未確認的值冒充已確認。
    /// </summary>
    Unknown,
}

/// <summary>UI 可直接組成的硬體事實；NumericValue 只在可可靠解析時提供。</summary>
/// <remarks>
/// <para>
/// <b>這裡有一個不變式：<c>Availability</c> 不是 <c>Present</c> 時，<c>NumericValue</c> 必須是 <c>null</c>。</b>
/// </para>
///
/// <para>
/// <b>為什麼要寫成程式碼而不是靠注意力：</b>同一個錯誤在本專案發生過<b>五次</b>——
/// <c>MeasuredBootFacts</c>（讀不到 PCR 與 PCR 全零）、
/// <c>TrustRootFacts</c>（讀不到信任根與沒有信任根）、
/// <c>UefiSectionTreeFacts</c> 的兩個鍵（沒有 FV／沒有樹可以數）、
/// <c>EfiVariableFacts</c>（四個變數都讀不到）。
/// <b>五次都是把兩個不同的「沒有」壓成一個</b>，而那個 <c>0</c> 會讓
/// 「讀不到」看起來像「量到 0」。
/// </para>
///
/// <para>
/// <b>處置是不 throw，而是把矛盾抹平並記錄下來。</b>
/// throw 會讓一個服務的疏忽變成整個掃描崩潰——<b>那比一個數字錯誤更糟</b>。
/// 所以非 <c>Present</c> 時 <c>NumericValue</c> 一律收斂成 <c>null</c>，
/// 並在 <c>UnavailableReason</c> 留下痕跡（<b>不是靜默修正</b>）。
/// </para>
/// </remarks>
public sealed record HardwareFact(
    string Key,
    string Category,
    string Name,
    string Value,
    string Unit,
    string Source,
    FactTrustLevel Trust,
    bool Sensitive,
    DateTimeOffset MeasuredAtUtc,
    double? NumericValue = null,
    FactAvailability Availability = FactAvailability.Present,
    string? UnavailableReason = null)
{
    /// <summary>
    /// 收斂後的數字：只有 <see cref="FactAvailability.Present"/> 才帶值。
    /// 非 <c>Present</c> 時一律 <c>null</c>——**「沒有」與「有 0 個」是不同的斷言**。
    /// </summary>
    public double? NumericValue { get; init; } =
        Availability == FactAvailability.Present ? NumericValue : null;

    /// <summary>
    /// 說明文字：呼叫端給的，加上（若發生過）那個被抹平的數字矛盾。
    ///
    /// <para>
    /// <b>而這裡有第二條不變式：<c>Availability</c> 是 <c>Present</c> 時，
    /// 呼叫端給的說明會被併進 <see cref="Value"/>，<c>UnavailableReason</c> 留空。</b>
    /// </para>
    ///
    /// <para>
    /// <b>為什麼：</b><c>UnavailableReason</c> 是「<b>為什麼讀不到</b>」的欄位——
    /// 而 <c>Present</c> 的事實沒有「讀不到」可言。
    /// <c>HardwareSnapshotService.ValidateFact</c> 早就有一條檢查會 throw，
    /// 但<b>它只在匯出快照那條路徑上跑</b>（整合測試），
    /// 所以<b>同一個錯誤在本專案發生過第二次</b>：我在 P2-061～P2-095 那批服務裡
    /// 把「這把鍵與那把的區隔」、「本服務不判決」寫進了 <c>Present</c> 的理由欄，
    /// 五處一起，而<b>單元測試全綠、只有整合測試紅燈</b>。
    /// </para>
    ///
    /// <para>
    /// <b>處置與 <c>NumericValue</c> 那條一致：不 throw，而是把放錯位置的東西
    /// 搬到對的位置並留下痕跡。</b>那些話不是垃圾——它們是「這個值是什麼意思」，
    /// 而<b>那正是 <c>Value</c> 該說的事</b>。搬到別處丟掉會失去資訊，
    /// 留在原地會讓整個掃描在匯出時崩潰。
    /// </para>
    /// </remarks>
    public string? UnavailableReason { get; init; } =
        Availability == FactAvailability.Present
            ? null
            : Availability != FactAvailability.Present && NumericValue is not null
                ? (UnavailableReason is null ? "" : UnavailableReason + "；")
                  + $"**（本筆原本帶了數字 {NumericValue} 而狀態是 {Availability}——"
                  + "非 Present 的事實不得帶數字，已如實收斂為空）**"
                : UnavailableReason;

    /// <summary>
    /// 收斂後的說明：<c>Present</c> 時，呼叫端寫在理由欄的說明併進值裡。
    /// </summary>
    public string Value { get; init; } =
        Availability == FactAvailability.Present
        && !string.IsNullOrWhiteSpace(UnavailableReason)
            ? Value + "。" + UnavailableReason
            : Value;
}

/// <summary>匯出敏感值時的明確政策。預設一律遮蔽，Preserve 必須由呼叫端主動指定。</summary>
public enum SensitiveValuePolicy
{
    Redact,
    Preserve,
}

/// <summary>一筆可跨時間比較的硬體事實。</summary>
public sealed record HardwareSnapshotFact
{
    [JsonPropertyOrder(0)] public required string Key { get; init; }
    [JsonPropertyOrder(1)] public required string Category { get; init; }
    [JsonPropertyOrder(2)] public required string Name { get; init; }
    [JsonPropertyOrder(3)] public required string Value { get; init; }
    [JsonPropertyOrder(4)] public double? NumericValue { get; init; }
    [JsonPropertyOrder(5)] public string? Unit { get; init; }
    [JsonPropertyOrder(6)] public required string Source { get; init; }
    [JsonPropertyOrder(7)] public FactTrustLevel Trust { get; init; }
    [JsonPropertyOrder(8)] public bool Sensitive { get; init; }
    [JsonPropertyOrder(9)] public DateTimeOffset MeasuredAtUtc { get; init; }
    [JsonPropertyOrder(10)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FactAvailability Availability { get; init; } = FactAvailability.Present;
    [JsonPropertyOrder(11)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnavailableReason { get; init; }
}

/// <summary>
/// 與檔案同放的 SHA-256 完整性摘要。它不是數位簽章，也不能阻止有能力重算摘要的人改檔。
/// </summary>
public sealed record HardwareSnapshotIntegrity
{
    public const string Sha256Algorithm = "SHA-256";

    [JsonPropertyOrder(0)] public required string Algorithm { get; init; }
    [JsonPropertyOrder(1)] public required string Hash { get; init; }
}

/// <summary>一份版本化硬體時間膠囊；機器識別只接受匿名雜湊，不保存原始序號。</summary>
public sealed record HardwareSnapshot
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; init; }
    [JsonPropertyOrder(1)] public required string AppVersion { get; init; }
    [JsonPropertyOrder(2)] public required string AnonymousMachineId { get; init; }
    [JsonPropertyOrder(3)] public DateTimeOffset CapturedAtUtc { get; init; }
    [JsonPropertyOrder(4)] public bool SensitiveValuesPreserved { get; init; }
    [JsonPropertyOrder(5)] public required IReadOnlyList<HardwareSnapshotFact> Facts { get; init; }
    [JsonPropertyOrder(6)] public required HardwareSnapshotIntegrity Integrity { get; init; }
}

/// <summary>存檔選項。敏感值預設遮蔽；保留必須明確 opt-in。</summary>
public sealed record HardwareSnapshotSaveOptions
{
    public SensitiveValuePolicy SensitiveValues { get; init; } = SensitiveValuePolicy.Redact;
    public bool Overwrite { get; init; } = true;
}

public enum SnapshotChangeKind
{
    Added,
    Removed,
    Changed,
    Unchanged,
}

/// <summary>單一穩定 key 的前後差異。</summary>
public sealed record HardwareFactChange
{
    public required string Key { get; init; }
    public SnapshotChangeKind Kind { get; init; }
    public HardwareFact? Previous { get; init; }
    public HardwareFact? Current { get; init; }
    public double? NumericDelta { get; init; }
    public string? DeltaText => NumericDelta is { } d ? d.ToString("+0.################;-0.################;0", System.Globalization.CultureInfo.InvariantCulture) : null;
}

/// <summary>兩份時間膠囊的完整比較結果。</summary>
public sealed record HardwareSnapshotDiff
{
    public required string BeforeMachineId { get; init; }
    public required string AfterMachineId { get; init; }
    public DateTimeOffset BeforeCapturedAtUtc { get; init; }
    public DateTimeOffset AfterCapturedAtUtc { get; init; }
    public bool IsSameMachine => string.Equals(BeforeMachineId, AfterMachineId, StringComparison.Ordinal);
    public required IReadOnlyList<HardwareFactChange> Changes { get; init; }
    public IReadOnlyList<HardwareFactChange> Entries => Changes;
    public int Added => Changes.Count(x => x.Kind == SnapshotChangeKind.Added);
    public int Removed => Changes.Count(x => x.Kind == SnapshotChangeKind.Removed);
    public int Changed => Changes.Count(x => x.Kind == SnapshotChangeKind.Changed);
    public int Unchanged => Changes.Count(x => x.Kind == SnapshotChangeKind.Unchanged);
    public int AddedCount => Added;
    public int RemovedCount => Removed;
    public int ChangedCount => Changed;
    public int UnchangedCount => Unchanged;
}
