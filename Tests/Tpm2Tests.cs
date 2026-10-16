using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// TPM 2.0 純解碼器（WP14）的契約：PCRRead 命令的金標位元組、回應解析（rc 與摘要邊界）、
/// TCG log 走訪（多演算法摘要、損毀即停並標 truncated）。
/// </summary>
public class Tpm2Tests
{
    // ── 屬性編號的 golden（2026-10-17 修正後钉住）──────────────────────────
    //
    // 這一組值曾經錯過一次，而錯法是本專案最忌的那一種：**讀得到、看起來合理、
    // 但不是它標的那一項**（PtRevision 被寫成 LEVEL、廠商字串被寫成 YEAR／MANUFACTURER）。
    // 所以把「編號＝哪一項」寫成常數 golden——改動它就會紅，而紅的時候要重新對出處。
    // 出處：TPM 2.0 Part 2 Structures「TPM_PT Constants」（TPM_PT_FIXED＝0x100、TPM_PT_VAR＝0x200，
    // 組內第 n 項＝基數＋n）。

    [Fact]
    public void 屬性編號_FIXED那一組的相對位置()
    {
        Assert.Equal(0x00000100u, Tpm2.PtFamilyIndicator);
        Assert.Equal(0x00000101u, Tpm2.PtLevel);
        Assert.Equal(0x00000102u, Tpm2.PtRevision);          // 舊版誤寫 0x101（那是 LEVEL）
        Assert.Equal(0x00000103u, Tpm2.PtDayOfYear);
        Assert.Equal(0x00000104u, Tpm2.PtYear);
        Assert.Equal(0x00000105u, Tpm2.PtManufacturer);      // 「the vendor ID unique to each TPM manufacturer」
        Assert.Equal(0x00000106u, Tpm2.PtVendor1);           // 舊版誤把廠商字串放在 0x104..0x107
        Assert.Equal(0x00000107u, Tpm2.PtVendor2);
        Assert.Equal(0x00000108u, Tpm2.PtVendor3);
        Assert.Equal(0x00000109u, Tpm2.PtVendor4);
        Assert.Equal(0x0000010Au, Tpm2.PtVendorTpmType);
        Assert.Equal(0x0000010Bu, Tpm2.PtFirmwareVersion1);
        Assert.Equal(0x0000010Cu, Tpm2.PtFirmwareVersion2);
        Assert.Equal(0x00000200u, Tpm2.PtPermanent);
        Assert.Equal(0x00000201u, Tpm2.PtStartupClear);
    }

    /// <summary>固定屬性一次要讀滿十項：0x100 起算到 VENDOR_STRING_4（0x109）。
    /// 少一項就會少讀後面的廠商字串，而「少讀」不會有任何錯誤訊息。</summary>
    [Fact]
    public void 固定屬性項數_要涵蓋到廠商字串四()
    {
        Assert.Equal(10u, Tpm2.FixedPropertyCount);
        Assert.Equal(Tpm2.PtVendor4, Tpm2.PtFamilyIndicator + Tpm2.FixedPropertyCount - 1);
    }

    /// <summary>revision 的語意：規格寫的是「版號×100」（Revision 01.01 → 101）。
    /// 116 要讀成 1.16（本機 Win32_Tpm 的 SpecVersion 第 3 欄就是 1.16），不是 0.116。
    /// 形狀不符的值（例如位元欄位式的 0x01000300）回 null——不硬拆。</summary>
    [Fact]
    public void 規格版號_是版號乘一百()
    {
        Assert.Equal((1, 16), Tpm2.Revision(116));
        Assert.Equal((1, 1), Tpm2.Revision(101));
        Assert.Equal((1, 83), Tpm2.Revision(183));
        Assert.Null(Tpm2.Revision(0));            // 沒回報不是版本 0
        Assert.Null(Tpm2.Revision(0x01000300));   // 形狀不符：這一項不是位元欄位
    }

    [Fact]
    public void 四位元組字串_大端打包()
    {
        Assert.Equal("INTC", Tpm2.Ascii4(0x494E5443));
        Assert.Equal("2.0\0", Tpm2.Ascii4(0x322E3000));
        Assert.Null(Tpm2.FamilyIndicator(0));
        Assert.Equal("INTC", Tpm2.FamilyIndicator(0x494E5443));
    }

    // ── TPMA 位元欄位 ────────────────────────────────────────────────────

    [Fact]
    public void 永久標記_位元位置()
    {
        var f = Tpm2.DecodePermanent(0x00000003);
        Assert.True(f.OwnerAuthSet);            // bit0
        Assert.True(f.EndorsementAuthSet);      // bit1
        Assert.False(f.LockoutAuthSet);         // bit2
        Assert.False(f.DisableClear);           // bit8
        Assert.False(f.InLockout);              // bit9

        var g = Tpm2.DecodePermanent(0x00000704);   // lockoutAuthSet｜disableClear｜inLockout｜tpmGeneratedEPS
        Assert.False(g.OwnerAuthSet);
        Assert.True(g.LockoutAuthSet);
        Assert.True(g.DisableClear);
        Assert.True(g.InLockout);
        Assert.True(g.TpmGeneratedEps);         // bit10

        // 保留位（3–7、11–31）不得影響任何一項
        var h = Tpm2.DecodePermanent(0xFFFFF8F8);
        Assert.False(h.OwnerAuthSet && h.EndorsementAuthSet && h.LockoutAuthSet
                 && h.DisableClear && h.InLockout && h.TpmGeneratedEps);
    }

    [Fact]
    public void 開機狀態_位元位置()
    {
        var s = Tpm2.DecodeStartupClear(0x00000007);
        Assert.True(s.PhEnable);                // bit0
        Assert.True(s.ShEnable);                // bit1
        Assert.True(s.EhEnable);                // bit2
        Assert.False(s.PhEnableNv);             // bit3
        Assert.False(s.Orderly);                // bit31

        var t = Tpm2.DecodeStartupClear(0x80000008);
        Assert.False(t.PhEnable);
        Assert.True(t.PhEnableNv);
        Assert.True(t.Orderly);
    }

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
