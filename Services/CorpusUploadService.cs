using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>WP48 corpus 貢獻包 v1（骨架）：由遮蔽版快照派生、匿名、帶完整性信封。</summary>
public sealed record CorpusContributionV1
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyOrder(1)] public required string ToolVersion { get; init; }
    [JsonPropertyOrder(2)] public required string AnonymousMachineId { get; init; }
    [JsonPropertyOrder(3)] public required DateTimeOffset CapturedAtUtc { get; init; }
    [JsonPropertyOrder(4)] public required IReadOnlyList<HardwareSnapshotFact> Facts { get; init; }
    [JsonPropertyOrder(5)] public required HardwareSnapshotIntegrity Integrity { get; init; }
}

/// <summary>
/// WP48 corpus 上傳格式（**骨架**）：本服務只定義「什麼可以成為社群貢獻包」——
/// ① 只收遮蔽版快照（`SensitiveValuesPreserved=true` 的**拒收**，因為那代表使用者主動要求保留敏感識別）；
/// ② 敏感事實與身份鍵逐鍵排除（serial／uuid／mac 等——比事實自己的 Sensitive 旗標更嚴）；
/// ③ 帶與快照同款的 SHA-256 完整性信封。
/// **上傳通路刻意不實作**（無網路、無伺服器、無同意流程）——收集等社群，重開時先立同意閘門。
/// </summary>
public static class CorpusUploadService
{
    /// <summary>身份鍵排除清單（key 前綴／子串，不分大小寫）。新增鍵時同步更新測試。</summary>
    public static readonly string[] ExcludedKeyFragments =
    [
        "serial", "uuid", "mac", "asset.tag", "system.product", "user.",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>由遮蔽版快照建立貢獻包；敏感保留版拒收（回 null），身份鍵逐鍵排除。</summary>
    public static CorpusContributionV1? Build(HardwareSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SensitiveValuesPreserved) return null;

        var facts = snapshot.Facts
            .Where(f => !f.Sensitive)
            .Where(f => !ExcludedKeyFragments.Any(fragment =>
                f.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return new CorpusContributionV1
        {
            ToolVersion = snapshot.AppVersion,
            AnonymousMachineId = snapshot.AnonymousMachineId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            Facts = facts,
            Integrity = snapshot.Integrity,
        };
    }

    public static string ToJson(CorpusContributionV1 contribution) =>
        JsonSerializer.Serialize(contribution, JsonOptions);

    public static CorpusContributionV1? FromJson(string json) =>
        JsonSerializer.Deserialize<CorpusContributionV1>(json, JsonOptions);
}
