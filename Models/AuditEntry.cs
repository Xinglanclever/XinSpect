using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 審計日誌的一筆紀錄（V7 WP36／A47）：時間／操作者／機器識別（匿名雜湊）／動作／範圍／判決摘要／結果雜湊，
/// 以 <see cref="PreviousHash"/>→<see cref="EntryHash"/> 串成雜湊鏈——任何一筆被改動，後續全部驗證失敗。
/// 誠實界線（TASK-GAP-6 §A47 硬性要求）：
/// ① 只記<b>中繼資料</b>（動作、範圍、雜湊），絕不記掃描內容——日誌本身不得成為洩漏來源；
/// ② 機器識別用硬體指紋<b>雜湊</b>（<see cref="HardwareSnapshotService.CreateAnonymousMachineId"/>），不用序號原文；
/// ③ 與 EvidenceTimeline 的關係＝<b>並存不重疊</b>：Timeline 是使用者事件呈現（時間軸 UI），
///    審計日誌是不可否認的中繼資料鏈（完整性驗證用），兩者儲存與用途分開。
/// </summary>
public sealed record AuditEntry
{
    [JsonPropertyOrder(0)] public required int Sequence { get; init; }
    [JsonPropertyOrder(1)] public required DateTimeOffset TimestampUtc { get; init; }
    [JsonPropertyOrder(2)] public required string Operator { get; init; }
    [JsonPropertyOrder(3)] public required string MachineHash { get; init; }
    [JsonPropertyOrder(4)] public required string Action { get; init; }
    [JsonPropertyOrder(5)] public required string Scope { get; init; }
    [JsonPropertyOrder(6)] public required string ResultSummary { get; init; }
    [JsonPropertyOrder(7)] public required string ResultHash { get; init; }
    [JsonPropertyOrder(8)] public required string PreviousHash { get; init; }
    [JsonPropertyOrder(9)] public required string EntryHash { get; init; }

    /// <summary>鏈的創世標記（第一筆的 PreviousHash）。</summary>
    public const string Genesis = "GENESIS";

    internal static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>本筆雜湊：對「不含 EntryHash 欄位」的 canonical UTF-8 位元組取 SHA-256（hex 小寫）。</summary>
    public static string ComputeHash(AuditEntry entry)
    {
        var unsigned = entry with { EntryHash = "" };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(unsigned, CanonicalOptions);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }
}

/// <summary>機器指紋的審計身分：硬體材料 → 不可逆匿名雜湊（重用快照服務的既有機制，序號原文不落日誌）。</summary>
public static class AuditIdentity
{
    public static string MachineHash(IEnumerable<string?> hardwareIdentityParts) =>
        HardwareSnapshotService.CreateAnonymousMachineId(hardwareIdentityParts);
}
