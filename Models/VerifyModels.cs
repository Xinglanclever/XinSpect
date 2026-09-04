namespace XinSpect;

/// <summary>
/// 驗機稽核用得到的每一個硬體事實。一個 <see cref="FactId"/> 就是一個「可被對帳的量」。
/// </summary>
/// <remarks>
/// 規則不直接去讀服務，而是宣告自己需要哪幾個 FactId；引擎在跑規則前先檢查事實袋裡有沒有，
/// 缺就直接判「無法判定」並指出缺的是哪一個。這樣規則本體只剩純比對，也不會各自寫一套
/// 「讀不到怎麼辦」。
/// </remarks>
public enum FactId
{
    // ── 記憶體（SMBIOS Type 16／17）──
    DimmCount, DimmManufacturers, DimmPartNumbers, DimmSerials,
    DimmSpeedMts, DimmConfiguredMts, DimmSizeTotalMiB, ArrayMaxCapacityMiB, ArraySlotCount,

    // ── 儲存（NVMe Log 0x02／ATA SMART／IDENTIFY DEVICE）──
    NvmePowerOnHours, NvmeDataUnitsWritten, NvmePercentageUsed,
    NvmePowerCycles, NvmeUnsafeShutdowns, NvmeCriticalWarning,
    SmartPowerOnHours, SmartHostWritesGiB, SmartPowerCycles,
    AtaRotationRate, AtaTotalLba, AtaAcsVersion, DiskClaimedCapacityGB, DiskModel,

    // ── 電池（Win32_Battery／WMI 電池靜態資料）──
    BatteryDesignCapacityMWh, BatteryFullCapacityMWh,
}

/// <summary>這個值是從哪裡讀來的。</summary>
public enum FactSource { Cpuid, Msr, Smbios, AtaIdentify, SmartAttr, NvmeLog, Edid, PciConfig, Wmi, Derived }

/// <summary>
/// 讀值的信賴層級。<see cref="Native"/>＝直接問矽晶片或控制器；
/// <see cref="FirmwareReported"/>＝經韌體或驅動轉述——**韌體是可以說謊的**，這一點必須讓使用者看見；
/// <see cref="Derived"/>＝由其他事實推導而來。
/// </summary>
public enum FactTrust { Native, FirmwareReported, Derived }

/// <summary>一條規則的判定：相符／矛盾／無法判定。</summary>
public enum VerifyVerdict { Match, Conflict, Unread }

/// <summary>
/// 一個帶完整血統的硬體事實。畫面上每一個數字都必須說得出自己是怎麼來的，這是本專案誠實主軸
/// 在驗機這個功能上的最低要求。
/// </summary>
/// <param name="Value">已格式化、給人看的字串。</param>
/// <param name="Numeric">給規則做算術用；沒有數值意義（如料號清單）時為 <c>null</c>。</param>
/// <param name="Method">精確到位移的讀取方法，例如 <c>"SMBIOS Type 17 +0x15"</c>、<c>"MSR 0xCE bits 15:8"</c>。</param>
public sealed record VerifyFact(
    FactId Id,
    string Label,
    string Value,
    double? Numeric,
    string Unit,
    FactSource Source,
    string Method,
    bool NeedsAdmin,
    FactTrust Trust,
    DateTime ReadAt);

/// <summary>
/// 一條規則跑完的結果。
/// </summary>
/// <param name="BenignCause">
/// 這個矛盾可能的正當成因。刻意設為必填參數（可傳 <c>null</c>，但每條規則都得明確表態）：
/// 混批記憶體常常只是使用者自己加的，通電小時低而寫入量高可能是原主換過主機板。
/// 少了這一欄，工具就從「指出對不上」變成「暗示賣家有惡意」。
/// </param>
public sealed record VerifyFinding(
    string Id,
    string Part,
    string Title,
    VerifyVerdict Verdict,
    Severity Severity,
    string Explanation,
    string? BenignCause,
    VerifyFact[] Evidence);

/// <summary>
/// 事實袋。取不到就回 <c>null</c>——呼叫端不准把「缺少」當成 0。
/// </summary>
/// <remarks>
/// 這個型別刻意做成純資料、不含任何檢視模型參照，因為後續的「硬體變更稽核」要把同一份事實
/// 序列化存檔、跟過去比對。本輪不寫持久化，但資料模型先不要擋路。
/// </remarks>
public sealed class VerifyFacts
{
    private readonly Dictionary<FactId, VerifyFact> _map = [];

    /// <summary>同一個 FactId 重複裝入時以後者為準（collector 可安全地後蓋前）。</summary>
    public VerifyFacts(IEnumerable<VerifyFact> facts)
    {
        foreach (var f in facts) _map[f.Id] = f;
    }

    public bool Has(FactId id) => _map.ContainsKey(id);

    public VerifyFact? Get(FactId id) => _map.TryGetValue(id, out var f) ? f : null;

    /// <summary>數值；缺少或該事實無數值意義時回 <c>null</c>。**不要改成回 0。**</summary>
    public double? Num(FactId id) => _map.TryGetValue(id, out var f) ? f.Numeric : null;

    public string? Text(FactId id) => _map.TryGetValue(id, out var f) ? f.Value : null;

    /// <summary>目前袋子裡的所有事實（給 UI 的「已讀到的事實」清單用）。</summary>
    public IReadOnlyCollection<VerifyFact> All => _map.Values;
}
