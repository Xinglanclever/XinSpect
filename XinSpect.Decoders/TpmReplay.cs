using System.Security.Cryptography;

namespace XinSpect;

/// <summary>重放的一顆 PCR 結果：事件數、最終期望值、是否被 log 覆蓋。</summary>
public sealed record PcrReplayResult(int EventCount, byte[] Expected, bool Covered);

/// <summary>
/// TCG 事件 log 重放器（P0-01／FW-002 的解碼器層）。純計算：吃走訪過的事件清單，
/// 對指定雜湊 bank 模擬 extend（PCR_new ＝ Hash(PCR_old ∥ digest)），回 0–23 的期望值。
/// <para>
/// <b>模型假設（界線）</b>：PC Client 起始值——PCR[0] 以 0xFF…FF 起、其餘以 0x00…00 起（TCG PC Client
/// Platform Firmware Profile 的重置行為）；<c>EV_NO_ACTION</c>（type 0x03，含 Spec ID Event03）不延伸；
/// 其餘事件有該 bank 的 digest 就延伸。真實平台若對不上，可能是模型、log 不完整或事件未延伸——
/// <b>不一致是事實，不是「被入侵」判決</b>。
/// </para>
/// </summary>
public static class TpmReplay
{
    /// <summary>PC Client 起始值：PCR 0＝0xFF…FF，其餘＝0x00…00。</summary>
    [SpecRef("TCG PC Client Platform Firmware Profile（PCR 重置行為）：PCR[0] 起始 0xFF…FF、其餘 0x00…00；"
        + "延伸運算 PCR_new ＝ Hash(PCR_old ∥ digest)（TPM 2.0 Part 1 §17.3／Part 2 §10.10）；"
        + "EV_NO_ACTION（type 0x00000003）不延伸")]
    public static byte[] InitialValue(int index, int digestBytes) =>
        Enumerable.Repeat(index == 0 ? (byte)0xFF : (byte)0x00, digestBytes).ToArray();

    /// <summary>對整份事件清單重放一個 bank。回 24 顆 PCR（0–23）的重放結果。</summary>
    [SpecRef("同 InitialValue 的出處；本方法實作「依序 extend」：逐事件取該 bank 的 digest，"
        + "PCR_new ＝ Hash(PCR_old ∥ digest)，EV_NO_ACTION 跳過")]
    public static IReadOnlyList<PcrReplayResult> Run(IReadOnlyList<Tpm2.TcgEvent> events, ushort alg, int pcrCount = 24)
    {
        int size = Tpm2.DigestSize(alg);
        using HashAlgorithm h = alg switch
        {
            Tpm2.AlgSha1 => SHA1.Create(),
            Tpm2.AlgSha256 => SHA256.Create(),
            Tpm2.AlgSha384 => SHA384.Create(),
            Tpm2.AlgSha512 => SHA512.Create(),
            _ => throw new ArgumentException("未收錄的雜湊演算法——不猜", nameof(alg)),
        };

        var pcrs = new byte[pcrCount][];
        var counts = new int[pcrCount];
        for (int i = 0; i < pcrCount; i++) pcrs[i] = InitialValue(i, size);

        foreach (var e in events)
        {
            if (e.EventType == 0x00000003) continue; // EV_NO_ACTION：不延伸（含 Spec ID Event03）
            if (e.PcrIndex >= (uint)pcrCount) continue;
            var digest = e.Digests.FirstOrDefault(d => d.Alg == alg).Digest;
            if (digest is null || digest.Length != size) continue;
            byte[] old = pcrs[e.PcrIndex];
            byte[] input = new byte[old.Length + digest.Length];
            Buffer.BlockCopy(old, 0, input, 0, old.Length);
            Buffer.BlockCopy(digest, 0, input, old.Length, digest.Length);
            pcrs[e.PcrIndex] = h.ComputeHash(input);
            counts[e.PcrIndex]++;
        }

        return Enumerable.Range(0, pcrCount)
            .Select(i => new PcrReplayResult(counts[i], pcrs[i], counts[i] > 0))
            .ToList();
    }

    /// <summary>重放值與實測值的比較：一致／不一致／實測缺席。三態由呼叫端（服務層）誠實呈現。</summary>
    [SpecRef("比較＝逐位元組相等；不一致只陳述差異（兩個十六進位值並列），不宣稱成因")]
    public static string Judge(byte[] expected, byte[] actual, string algName) =>
        expected.AsSpan().SequenceEqual(actual)
            ? $"{algName} 一致"
            : $"{algName} 不一致：重放 0x{Convert.ToHexStringLower(expected)}／實測 0x{Convert.ToHexStringLower(actual)}";
}
