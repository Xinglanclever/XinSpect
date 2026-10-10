using System.Security.Cryptography;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// TCG log 重放器金標（P0-01／FW-002）：合成事件鏈＋已知答案——延伸運算、起始值模型、
/// EV_NO_ACTION 跳過、越界 PCR 跳過；服務層三態（log 缺、比對一致、不一致）。
/// </summary>
public class TpmReplayTests
{
    private static Tpm2.TcgEvent Ev(uint pcr, ushort alg, byte[] digest, uint type = 0x80000001) =>
        new(pcr, type, [(alg, digest)]);

    private static byte[] D(params byte[] b)
    {
        var d = new byte[32];
        Array.Copy(b, d, Math.Min(b.Length, 32));
        return d;
    }

    [Fact]
    public void 重放_單事件延伸鏈已知答案()
    {
        byte[] d1 = D(0x01), d2 = D(0x02);
        var events = new[] { Ev(1, Tpm2.AlgSha256, d1), Ev(1, Tpm2.AlgSha256, d2) };
        var r = TpmReplay.Run(events, Tpm2.AlgSha256);

        using var sha = SHA256.Create();
        byte[] init = new byte[32];
        byte[] e1 = sha.ComputeHash(init.Concat(d1).ToArray());
        byte[] e2 = sha.ComputeHash(e1.Concat(d2).ToArray());
        Assert.Equal(2, r[1].EventCount);
        Assert.True(r[1].Covered);
        Assert.Equal(e2, r[1].Expected);
    }

    [Fact]
    public void 重放_PCR0起始值是全FF其餘全零()
    {
        byte[] d = D(0x09);
        var events = new[] { Ev(0, Tpm2.AlgSha256, d), Ev(5, Tpm2.AlgSha256, d) };
        var r = TpmReplay.Run(events, Tpm2.AlgSha256);
        using var sha = SHA256.Create();
        Assert.Equal(sha.ComputeHash(Enumerable.Repeat((byte)0xFF, 32).Concat(d).ToArray()), r[0].Expected);
        Assert.Equal(sha.ComputeHash(new byte[32].Concat(d).ToArray()), r[5].Expected);
    }

    [Fact]
    public void 重放_EV_NO_ACTION不延伸()
    {
        byte[] d = D(0x03);
        var events = new[] { Ev(2, Tpm2.AlgSha256, d, type: 0x00000003), Ev(2, Tpm2.AlgSha256, d) };
        var r = TpmReplay.Run(events, Tpm2.AlgSha256);
        Assert.Equal(1, r[2].EventCount); // NO_ACTION 那筆不計
    }

    [Fact]
    public void 重放_沒有該bank的digest就跳過且log未覆蓋()
    {
        byte[] d = D(0x04);
        var events = new[] { Ev(3, Tpm2.AlgSha1, d) }; // 只有 SHA-1
        var r = TpmReplay.Run(events, Tpm2.AlgSha256);
        Assert.False(r[3].Covered);
        Assert.Equal(0, r[3].EventCount);
    }

    [Fact]
    public void 重放_越界PCR跳過不炸()
    {
        byte[] d = D(0x05);
        var events = new[] { Ev(99, Tpm2.AlgSha256, d) };
        var r = TpmReplay.Run(events, Tpm2.AlgSha256);
        Assert.All(r, x => Assert.False(x.Covered));
    }

    [Fact]
    public void 判讀_一致與不一致的文字()
    {
        byte[] a = Enumerable.Repeat((byte)1, 32).ToArray();
        byte[] b = Enumerable.Repeat((byte)2, 32).ToArray();
        Assert.Contains("一致", TpmReplay.Judge(a, a, "SHA-256"));
        string bad = TpmReplay.Judge(a, b, "SHA-256");
        Assert.Contains("不一致", bad);
        Assert.Contains("0101", bad);   // 重放值
        Assert.Contains("0202", bad);   // 實測值
    }

    // ── 服務層（合成 log ＋ 假 TPM）──────────────────────────────────────

    private static byte[] BuildLog(IReadOnlyList<Tpm2.TcgEvent> events)
    {
        var b = new List<byte>();
        foreach (var e in events)
        {
            void U32(uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
            void U16(ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
            U32(e.PcrIndex); U32(e.EventType); U32((uint)e.Digests.Count);
            foreach (var (alg, digest) in e.Digests) { U16(alg); b.AddRange(digest); }
            U32(4); b.AddRange([1, 2, 3, 4]); // event payload
        }
        return b.ToArray();
    }

    private static (byte[] Log, byte[] Expected) OneEventLog(uint pcr = 1)
    {
        byte[] d = new byte[32]; d[0] = 0xAB;
        var events = new[] { Ev(pcr, Tpm2.AlgSha256, d) };
        var replay = TpmReplay.Run(events, Tpm2.AlgSha256);
        return (BuildLog(events), replay[(int)pcr].Expected);
    }

    [Fact]
    public void 服務_一致時逐顆對得上且摘要帶界線()
    {
        var (log, expected) = OneEventLog(1);
        var facts = TpmReplayFactsService.Collect(At,
            createContext: () => (nint)1,
            submit: (ctx, cmd) =>
            {
                // PCR read 命令的 handle 在位移 10（u32 大端）——只有被 log 覆蓋的 PCR 會被查
                uint idx = (uint)((cmd[10] << 24) | (cmd[11] << 16) | (cmd[12] << 8) | cmd[13]);
                Assert.Equal(1u, idx);
                var resp = BuildPcrResponse(expected);
                return (0u, resp, (uint)resp.Length);
            },
            readLog: ctx => log);

        var summary = facts.Single(f => f.Key == "tpm.replay.summary");
        Assert.Contains("一致 1", summary.Value);
        Assert.Contains("不一致 0", summary.Value);
        Assert.Contains("log 未覆蓋 23", summary.Value);
        Assert.Contains("不是『被入侵』判決", summary.Value);
        var pcr1 = facts.Single(f => f.Key == "tpm.replay.pcr.sha256.1");
        Assert.Equal(FactAvailability.Present, pcr1.Availability);
        Assert.Contains("SHA-256 一致", pcr1.Value);
        var pcr7 = facts.Single(f => f.Key == "tpm.replay.pcr.sha256.7");
        Assert.Equal(FactAvailability.NotApplicable, pcr7.Availability);
    }

    [Fact]
    public void 服務_不一致時如實並列兩個值不宣稱入侵()
    {
        var (log, expected) = OneEventLog(1);
        byte[] wrong = new byte[32]; wrong[31] = 0xEE;
        var facts = TpmReplayFactsService.Collect(At,
            createContext: () => (nint)1,
            submit: (ctx, cmd) => { var resp = BuildPcrResponse(wrong); return (0u, resp, (uint)resp.Length); },
            readLog: ctx => log);

        var summary = facts.Single(f => f.Key == "tpm.replay.summary");
        Assert.Contains("不一致 1", summary.Value);
        var pcr1 = facts.Single(f => f.Key == "tpm.replay.pcr.sha256.1");
        Assert.Contains("重放 0x", pcr1.Value);
        Assert.Contains("實測 0x", pcr1.Value);
        Assert.Contains("不是入侵判決", pcr1.Value);
    }

    [Fact]
    public void 服務_log讀不到_如實標示不是量測鏈完整()
    {
        var facts = TpmReplayFactsService.Collect(At,
            createContext: () => (nint)1,
            submit: (ctx, cmd) => (0u, [], 0u),
            readLog: ctx => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("log 缺＝無法重放", f.UnavailableReason);
    }

    [Fact]
    public void 服務_TBS不可用_標不適用()
    {
        var facts = TpmReplayFactsService.Collect(At, createContext: () => nint.Zero);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
    }

    /// <summary>
    /// 實機交叉（真 TBS＋真 log＋真 PCR）：只驗良構——log 在時摘要必帶比對計數、log 缺時如實標；
    /// 一致與不一致都算通過（不一致是事實不是失敗）。與真硬體對話，整組禁並行。
    /// </summary>
    [Collection("RealHardwareReconcile")]
    public class RealHardwareReplay
    {
        [Fact]
        [Trait("Category", "RealHardware")]
        public void 實機重放_良構且不炸()
        {
            var facts = TpmReplayFactsService.Collect(DateTimeOffset.UtcNow);
            var summary = facts.Single(f => f.Key == "tpm.replay.summary");
            if (summary.Availability != FactAvailability.Present)
            {
                Assert.True(facts.Count == 1, "TBS 不可用時只有摘要一筆（如實標）");
                return; // 無 TPM 的機器：NotApplicable 路徑本身是驗證目標
            }
            Assert.Contains("比對", summary.Value);
            Assert.True(facts.Count == 26, "良構＝摘要＋24 顆 PCR 重放結果");
            var covered = facts.Where(f => f.Key.StartsWith("tpm.replay.pcr.sha256.") && f.Availability == FactAvailability.Present).ToList();
            Assert.True(covered.Count > 0, "log 至少覆蓋一顆 PCR（Spec ID Event03 之後的量測鏈）");
        }
    }

    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    /// <summary>合成 TPM2 PCR read 回應（tag u16 + paramSize u32 + rc u32 + updateCounter u32 + digest）。</summary>
    private static byte[] BuildPcrResponse(byte[] digest)
    {
        var b = new List<byte>();
        void U16(ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void U32(uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
        U16(0x8002); U32((uint)(12 + digest.Length)); U32(0);
        U16((ushort)digest.Length);
        b.AddRange(digest);
        return b.ToArray();
    }
}
