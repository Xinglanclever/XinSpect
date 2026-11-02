using System.Security.Cryptography;
using System.Text;

namespace XinSpect;

/// <summary>Merkle 建樹時被排除的事實計數；每一類都如實分開，不混成一個「跳過」。</summary>
public sealed record MerkleExclusion(int Sensitive, int Unavailable, int Volatile, int NotAllowlisted);

/// <summary>一片葉子：只公開鍵與葉雜湊，<b>不公開值</b>。</summary>
public sealed record MerkleLeaf(string Key, string LeafSha256);

/// <summary>穩定事實 Merkle 樹的結果。<see cref="RootSha256"/> 為 null 代表沒有任何穩定事實。</summary>
public sealed record MerkleTree(
    int LeafCount,
    string? RootSha256,
    IReadOnlyList<MerkleLeaf> Leaves,
    MerkleExclusion Excluded);

/// <summary>
/// 把<b>穩定、非敏感、已讀到</b>的事實排成 Merkle 樹，作為可重現的硬體組態指紋。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼不能對「全部事實」建樹：</b>溫度、風扇轉速、負載、可用記憶體、延遲這些鍵每次收集都會變，
/// 把它們放進樹裡，同一台機器每跑一次就得到不同 root——那樣的 root 不能當指紋，
/// 只能當「這一次開機的雜湊」。本服務因此用<b>白名單前綴</b>而不是黑名單：
/// 新服務產生新鍵時，預設<b>不會</b>被納入，直到有人明确把它加進白名單。
/// </para>
/// <para>
/// <b>葉子內容不含時間。</b>canonical payload 是
/// <c>key ＋ 0x1F ＋ value ＋ 0x1F ＋ trust ＋ 0x1F ＋ source</c>；
/// <c>MeasuredAtUtc</c> 完全不參與——同一台機器、同樣組態，跨次收集必須得到同樣 root。
/// </para>
/// <para>
/// <b>輸出只含鍵與雜湊，不含值。</b>指紋可以貼到工單或公開場合，序號、GUID 這類內容
/// 不會因為「只是算 hash」就出現在輸出裡；<see cref="HardwareFact.Sensitive"/> 的事實
/// 也一律排除，連葉子都不產生。
/// </para>
/// </remarks>
public static class FactMerkleService
{
    public const int SchemaVersion = 1;
    public const string Algorithm = "SHA-256";

    /// <summary>canonical payload 的分隔字元（ASCII Unit Separator）。</summary>
    public const char Separator = '\u001F';

    /// <summary>
    /// 穩定事實白名單：鍵等於前綴，或以「前綴＋.」開頭才算命中。
    /// <b>用「前綴＋點」而不是裸前綴</b>，否則 <c>uefi.db</c> 會誤收 <c>uefi.dbx.*</c>。
    /// </summary>
    private static readonly string[] StablePrefixes =
    [
        "ac.cedt", "ac.dmar", "ac.fadt", "ac.ivrs", "ac.madt", "ac.slit",
        "ac.srat", "ac.tpm2", "ac.vfct", "ac.wpbt",
        "bt", "chipset", "cxl",
        "cpu.die.count", "cpu.generation", "cpu.stepping.known", "cpu.topology.smt", "cpu.uarch",
        "fw.date", "fw.vendor", "fw.version",
        "mc.version",
        "pci.dev",
        "spi",
        "tpm.owned", "tpm.pcr.banks", "tpm.present", "tpm.version",
        "uefi.db", "uefi.dbx", "uefi.deployed_mode", "uefi.kek", "uefi.pk",
        "uefi.secure_boot", "uefi.setup_mode", "uefi.version",
        "virt.hypervisor", "virt.vendor",
    ];

    /// <summary>鍵的區段含這些字就視為易變；白名單裡的鍵仍可能因平台差異而帶到，故再加一道。</summary>
    private static readonly HashSet<string> VolatileTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp", "temperature", "fan", "rpm", "load", "usage", "clock", "watt", "power",
        "voltage", "volt", "current", "latency", "uptime", "elapsed", "rate", "percent",
        "free", "used", "queue", "iops", "throughput", "speed", "bandwidth", "energy",
        "time", "timestamp",
    };

    /// <summary>對一份事實建立 Merkle 樹；純函式，不做 I/O。</summary>
    public static MerkleTree Build(IReadOnlyList<HardwareFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        int sensitive = 0, unavailable = 0, volatileCount = 0, notAllowlisted = 0;
        var selected = new List<(string Key, string Payload)>();

        foreach (var f in facts)
        {
            if (f.Sensitive) { sensitive++; continue; }
            if (f.Availability != FactAvailability.Present) { unavailable++; continue; }
            if (IsVolatile(f.Key)) { volatileCount++; continue; }
            if (!IsAllowlisted(f.Key)) { notAllowlisted++; continue; }
            selected.Add((f.Key, CanonicalPayload(f)));
        }

        // 同鍵只留一筆；若呼叫端真的給了不同值，用 payload 排序取第一筆，確保結果不依輸入順序而變。
        selected = selected
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(x => x.Payload, StringComparer.Ordinal).First())
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

        var hashes = selected
            .Select(x => Sha256(Encoding.UTF8.GetBytes(x.Payload)))
            .ToList();
        var leaves = selected
            .Select((x, i) => new MerkleLeaf(x.Key, Hex(hashes[i])))
            .ToList();

        string? root = hashes.Count == 0 ? null : Root(hashes);
        return new MerkleTree(leaves.Count, root, leaves,
            new MerkleExclusion(sensitive, unavailable, volatileCount, notAllowlisted));
    }

    /// <summary>canonical payload：鍵、值、信任等級、來源，以 0x1F 分隔；不含時間。</summary>
    public static string CanonicalPayload(HardwareFact fact)
        => string.Join(Separator, fact.Key, fact.Value, fact.Trust.ToString(), fact.Source);

    /// <summary>單片葉子的 SHA-256（小寫 hex）。</summary>
    public static string LeafHash(HardwareFact fact)
        => Hex(Sha256(Encoding.UTF8.GetBytes(CanonicalPayload(fact))));

    private static bool IsAllowlisted(string key)
        => StablePrefixes.Any(p => string.Equals(key, p, StringComparison.Ordinal)
            || key.StartsWith(p + ".", StringComparison.Ordinal));

    private static bool IsVolatile(string key)
        => key.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(VolatileTokens.Contains);

    /// <summary>兩兩合併；落單的最後一片原樣提升到下一層（不與自己配對）。</summary>
    private static string Root(IReadOnlyList<byte[]> level)
    {
        var current = level;
        while (current.Count > 1)
        {
            var next = new List<byte[]>((current.Count + 1) / 2);
            for (int i = 0; i < current.Count; i += 2)
            {
                if (i + 1 < current.Count)
                {
                    var pair = new byte[current[i].Length + current[i + 1].Length];
                    Buffer.BlockCopy(current[i], 0, pair, 0, current[i].Length);
                    Buffer.BlockCopy(current[i + 1], 0, pair, current[i].Length, current[i + 1].Length);
                    next.Add(Sha256(pair));
                }
                else
                {
                    next.Add(current[i]);
                }
            }
            current = next;
        }
        return Hex(current[0]);
    }

    private static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}