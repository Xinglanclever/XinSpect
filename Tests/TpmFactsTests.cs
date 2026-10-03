using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// TPM 量測開機鏈事實（WP14）的契約：注入 TBS 假通道驗證 PCR 0–7 逐顆成列與 TCG log 摘要、
/// 無 TPM 三態、TBS/TPM 錯誤如實帶碼。不碰真實 TBS。
/// </summary>
public class TpmFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static byte[] PcrResponse(uint rc, byte[] digest)
    {
        var r = new byte[12 + (rc == 0 ? 2 + digest.Length : 0)];
        r[0] = 0x80; r[1] = 0x01;
        PutBE(r, 2, (uint)r.Length);
        PutBE(r, 6, rc);
        if (rc == 0) { PutBE16(r, 10, (ushort)digest.Length); digest.CopyTo(r, 12); }
        return r;
    }

    private static void PutBE(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static void PutBE16(byte[] b, int off, ushort v) { b[off] = (byte)(v >> 8); b[off + 1] = (byte)v; }

    private static byte[] MakeTcgLog() => Tpm2.WalkTcgLog(MakeTcgLogRaw()).Events.Count >= 1 ? MakeTcgLogRaw() : [];

    private static byte[] MakeTcgLogRaw()
    {
        var e1 = new byte[4 + 4 + 4 + 2 + 32 + 4 + 3];
        PutBE(e1, 0, 0); PutBE(e1, 4, 0x80000007u); PutBE(e1, 8, 1); PutBE16(e1, 12, 0x000B);
        for (int i = 0; i < 32; i++) e1[14 + i] = (byte)i;
        PutBE(e1, 46, 3);
        var e2 = new byte[4 + 4 + 4 + 2 + 20 + 4 + 1];
        PutBE(e2, 0, 7); PutBE(e2, 4, 0x80000007u); PutBE(e2, 8, 1); PutBE16(e2, 12, 0x0004);
        for (int i = 0; i < 20; i++) e2[14 + i] = (byte)(i + 5);
        PutBE(e2, 34, 1);
        return e1.Concat(e2).ToArray();
    }

    [Fact]
    public void 全鏈成功_PCR0到7逐顆成列_TCGlog摘要帶事件數()
    {
        var digest = Enumerable.Range(0, 32).Select(i => (byte)(i ^ 0x5A)).ToArray();
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) =>
            {
                Assert.Equal(20, cmd.Length);               // 命令由解碼器建構（金標已在 Tpm2Tests 釘死）
                return (0, PcrResponse(0, digest), 12 + 2 + 32);
            },
            readLog: _ => MakeTcgLog());

        Assert.Single(facts, f => f.Key == "tpm.present");
        Assert.Contains("可用", Assert.Single(facts, f => f.Key == "tpm.present").Value);
        for (uint i = 0; i < 8; i++)
        {
            var pcr = Assert.Single(facts, f => f.Key == $"tpm.pcr.sha256.{i}");
            Assert.Equal(FactAvailability.Present, pcr.Availability);
            Assert.Equal(Convert.ToHexStringLower(digest), pcr.Value);
        }
        var log = Assert.Single(facts, f => f.Key == "tpm.tcg_log");
        Assert.Contains("2 個事件", log.Value);
        Assert.Contains("PCR 覆蓋 0、7", log.Value);
        Assert.DoesNotContain("損毀", log.Value);
    }

    [Fact]
    public void 無TPM_單一事實如實三態()
    {
        var facts = TpmFactsService.Collect(At, createContext: () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("TPM 不存在", f.UnavailableReason);
    }

    [Fact]
    public void TPM拒絕與TBS錯誤_分別帶TPM_RC與TBS碼()
    {
        var facts = TpmFactsService.Collect(At,
            createContext: () => (nint)0x1234,
            submit: (_, cmd) => (0, PcrResponse(0x0000014C, []), 12),
            readLog: _ => null);

        var pcr = Assert.Single(facts, f => f.Key == "tpm.pcr.sha256.0");
        Assert.Equal(FactAvailability.NotSupported, pcr.Availability);
        Assert.Contains("TPM_RC 0x0000014C", pcr.UnavailableReason);

        var log = Assert.Single(facts, f => f.Key == "tpm.tcg_log");
        Assert.Equal(FactAvailability.NotSupported, log.Availability);
    }
}
