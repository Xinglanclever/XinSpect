using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>
/// 原始暫存器快照的存取：canonical JSON ＋ SHA-256 完整性信封（比照 HardwareSnapshotService 的做法）。
/// 載入時逐位元組驗完整性——被竄改或損毀的檔案拒載，不猜內容。
/// </summary>
public static class RawRegisterSnapshotStore
{
    public const string Sha256Algorithm = "sha256";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>對「不含 Integrity 欄位」的 canonical UTF-8 位元組取 SHA-256。</summary>
    public static string ComputeHash(RawRegisterSnapshot snapshot)
    {
        var unsigned = snapshot with { Integrity = null };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    public static RawRegisterSnapshot WithIntegrity(RawRegisterSnapshot snapshot)
        => snapshot with { Integrity = new RawSnapshotIntegrity { Algorithm = Sha256Algorithm, Hash = ComputeHash(snapshot) } };

    public static void Save(string path, RawRegisterSnapshot snapshot) =>
        File.WriteAllText(path, JsonSerializer.Serialize(WithIntegrity(snapshot), JsonOptions));

    public static RawRegisterSnapshot Load(string path)
    {
        var snapshot = JsonSerializer.Deserialize<RawRegisterSnapshot>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("原始快照解析為空");
        if (snapshot.Integrity is null)
            throw new InvalidOperationException("原始快照缺少完整性欄位——不是本程式產出的有效檔案");
        if (!string.Equals(snapshot.Integrity.Algorithm, Sha256Algorithm, StringComparison.Ordinal))
            throw new InvalidOperationException($"不支援的完整性演算法：{snapshot.Integrity.Algorithm}");
        if (!string.Equals(snapshot.Integrity.Hash, ComputeHash(snapshot), StringComparison.Ordinal))
            throw new InvalidOperationException("完整性校驗失敗：檔案可能被竄改或損毀，拒載");
        return snapshot;
    }
}
