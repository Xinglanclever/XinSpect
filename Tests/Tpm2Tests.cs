using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// TPM 2.0 純解碼器（WP14）的契約：PCRRead 命令的金標位元組、回應解析（rc 與摘要邊界）、
/// TCG log 走訪（多演算法摘要、損毀即停並標 truncated）。
/// </summary>
public class Tpm2Tests
{
    [Fact]
    public void PCRRead命令_金標位元組釘死()
    {
        var cmd = Tpm2.BuildPcrReadCommand(0);
        // tag 8001、paramSize 00000014、cc 0000017E、pcr 00000000、alg 000B、select 03 01 00 00
        Assert.Equal(
            new byte[] { 0x80, 0x01, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x01, 0x7E,
                         0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x03, 0x01, 0x00, 0x00 },
            cmd);

        var cmd7 = Tpm2.BuildPcrReadCommand(7);
        Assert.Equal(0x80, cmd7[17]); // PCR 7 → 第一個遮罩位元組的 bit7
        Assert.Equal((byte)0x00, cmd7[18]);
    }

    [Fact]
    public void PCRRead命令_PCR索引上限24()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Tpm2.BuildPcrReadCommand(24));
    }

    [Fact]
    public void 回應解析_成功與錯誤碼與邊界()
    {
        // 成功：tag、paramSize、rc=0、digest size=32、32 位元組
        var resp = new byte[12 + 2 + 32];
        resp[0] = 0x80; resp[1] = 0x01;                       // tag（大端）
        resp[2] = (byte)(resp.Length >> 24); resp[3] = (byte)(resp.Length >> 16); resp[4] = (byte)(resp.Length >> 8); resp[5] = (byte)resp.Length;
        resp[10] = 0x00; resp[11] = 0x20;                     // digest size 32（大端 u16，不污染 rc）
        for (int i = 0; i < 32; i++) resp[12 + i] = (byte)i;
        var digest = Tpm2.DecodePcrResponse(resp, out uint rc);
        Assert.Equal(0u, rc);
        Assert.NotNull(digest);
        Assert.Equal(32, digest!.Length);
        Assert.Equal(0x1F, digest[31]);

        // rc≠0 → null 帶 rc（TPM_RC_VALUE=0x14C 例）
        resp[6] = 0x00; resp[7] = 0x00; resp[8] = 0x01; resp[9] = 0x4C; // rc 在 offset 6..10（大端）
        var fail = Tpm2.DecodePcrResponse(resp, out uint rc2);
        Assert.Equal(0x14Cu, rc2);
        Assert.Null(fail);

        // 截斷回應 → null
        Assert.Null(Tpm2.DecodePcrResponse(resp[..10], out _));
    }

    [Fact]
    public void 摘要長度_已知演算法_未收錄為零()
    {
        Assert.Equal(20, Tpm2.DigestSize(Tpm2.AlgSha1));
        Assert.Equal(32, Tpm2.DigestSize(Tpm2.AlgSha256));
        Assert.Equal(64, Tpm2.DigestSize(Tpm2.AlgSha512));
        Assert.Equal(0, Tpm2.DigestSize(0x9999)); // 不猜
    }

    [Fact]
    public void TCGlog走訪_兩事件完整_損毀即停標truncated()
    {
        byte[] MakeEvent(uint pcr, ushort alg, byte[] digest, byte[] payload)
        {
            var e = new byte[4 + 4 + 4 + 2 + digest.Length + 4 + payload.Length];
            int off = 0;
            PutBE(e, off, pcr); off += 4;
            PutBE(e, off, 0x80000007u); off += 4;                             // EV_POST_CODE
            PutBE(e, off, 1u); off += 4;                                      // 一個摘要
            PutBE16(e, off, alg); off += 2;                                   // AlgId 是 u16！
            digest.CopyTo(e, off); off += digest.Length;
            PutBE(e, off, (uint)payload.Length); off += 4;
            payload.CopyTo(e, off);
            return e;
        }

        var log = MakeEvent(0, Tpm2.AlgSha256, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), [1, 2, 3])
            .Concat(MakeEvent(7, Tpm2.AlgSha1, Enumerable.Range(0, 20).Select(i => (byte)(i + 5)).ToArray(), [9]))
            .ToArray();

        var walk = Tpm2.WalkTcgLog(log);
        Assert.False(walk.Truncated);
        Assert.Equal(2, walk.Events.Count);
        Assert.Equal(7u, walk.Events[1].PcrIndex);
        Assert.Equal(32, walk.Events[0].Digests[0].Digest.Length);
        Assert.Equal(20, walk.Events[1].Digests[0].Digest.Length);

        // 截斷：砍掉最後 2 位元組 → 第二事件 EventSize 越界，走訪停且標 truncated
        var cut = Tpm2.WalkTcgLog(log[..^2]);
        Assert.True(cut.Truncated);
        Assert.Single(cut.Events); // 只有第一個事件完整
    }

    private static void PutBE(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16); b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static void PutBE16(byte[] b, int off, ushort v) { b[off] = (byte)(v >> 8); b[off + 1] = (byte)v; }
}
